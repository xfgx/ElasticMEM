// ElasticMem active-trim probe: можно ли сжимать память АКТИВНОГО приложения без VM и драйвера.
// Проверяет на настоящей Windows:
//   1) постраничное вытеснение ЧУЖОГО процесса (NtSetInformationVirtualMemory/VmRemoveFromWorkingSet,
//      NtUnlockVirtualMemory) — только выбранные страницы, а не весь рабочий набор;
//   2) точный сигнал доступа без драйвера: Working Set Watch (InitializeProcessForWsWatch + GetWsChangesEx)
//      даёт адрес каждой страницы, которую процесс вернул после вытеснения;
//   3) A/B на синтетическом «активном» приложении (60 кадров/с, горячий набор + тёплый + холодный):
//      без вмешательства / EmptyWorkingSet всего процесса / постраничный «холодный» алгоритм.
//   4) цену одного возврата страницы (soft fault) сразу и после паузы (когда ОС успела сжать).
// Сборка: cl /O2 /W3 probe.c psapi.lib advapi32.lib winmm.lib
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <psapi.h>
#include <mmsystem.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

typedef LONG NTSTATUS;
typedef NTSTATUS (NTAPI *PFN_NtUnlockVirtualMemory)(HANDLE, PVOID*, PSIZE_T, ULONG);
typedef NTSTATUS (NTAPI *PFN_NtSetInformationVirtualMemory)(HANDLE, int, ULONG_PTR, PWIN32_MEMORY_RANGE_ENTRY, PVOID, ULONG);
static PFN_NtUnlockVirtualMemory pNtUnlock;
static PFN_NtSetInformationVirtualMemory pNtSetVm;
// MSDN: BOOL GetWsChangesEx(HANDLE, PPSAPI_WS_WATCH_INFORMATION_EX, PDWORD cb) — грузим сами (в mingw заголовке неверная сигнатура).
typedef BOOL (WINAPI *PFN_GetWsChangesEx)(HANDLE, PPSAPI_WS_WATCH_INFORMATION_EX, PDWORD);
static PFN_GetWsChangesEx pGetWsChangesEx;
static double ft_ms(FILETIME a, FILETIME b) { ULARGE_INTEGER x, y; x.LowPart = a.dwLowDateTime; x.HighPart = a.dwHighDateTime; y.LowPart = b.dwLowDateTime; y.HighPart = b.dwHighDateTime; return (double)(y.QuadPart - x.QuadPart) / 10000.0; }
enum { VmPrefetchInformation = 0, VmPagePriorityInformation = 1, VmRemoveFromWorkingSetInformation = 7 };

#define PAGE 4096ull
#define MAXPH 8
#define MAXFR 4096
typedef struct { volatile LONG phase; volatile LONG quit; } Shared;

static double qpf;
static double now_ms(void) { LARGE_INTEGER t; QueryPerformanceCounter(&t); return t.QuadPart * 1000.0 / qpf; }
static uint64_t rng = 88172645463325252ull;
static uint64_t xr(void) { rng ^= rng << 13; rng ^= rng >> 7; rng ^= rng << 17; return rng; }

static void fill(uint8_t* p, size_t n) {
    // Похоже на кучу приложения: мелкие числа, указатели, текст, нули.
    for (size_t pg = 0; pg < n / PAGE; pg++) {
        uint32_t* w = (uint32_t*)(p + pg * PAGE);
        int kind = (int)(pg % 4);
        for (int i = 0; i < 1024; i++) {
            if (kind == 0) w[i] = (uint32_t)(i * 7 + pg) & 1023;
            else if (kind == 1) w[i] = (i & 1) ? 0x00007ff6u : (uint32_t)(0x10000000u + ((pg * 1024 + i) * 16 & 0xfffff));
            else if (kind == 2) w[i] = 0x20202020u ^ (uint32_t)(xr() & 0x1f1f1f1f);
            else w[i] = (i % 16 == 0) ? (uint32_t)xr() : 0;
        }
    }
}

static int cmpd(const void* a, const void* b) { double x = *(const double*)a, y = *(const double*)b; return x < y ? -1 : x > y; }

// ---------------- target: синтетическое активное приложение ----------------
static int run_target(size_t totalMiB, size_t hotMiB, size_t warmMiB) {
    HANDLE m = OpenFileMappingA(FILE_MAP_READ, FALSE, "Local\\EmActiveTrimProbe");
    if (!m) { printf("ERR no mapping\n"); return 2; }
    Shared* sh = (Shared*)MapViewOfFile(m, FILE_MAP_READ, 0, 0, sizeof(Shared));
    size_t size = totalMiB << 20, hotP = (hotMiB << 20) / PAGE, warmP = (warmMiB << 20) / PAGE;
    uint8_t* base = (uint8_t*)VirtualAlloc(NULL, size, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    if (!base) { printf("ERR alloc\n"); return 2; }
    fill(base, size);
    // Горячие/тёплые страницы разбросаны по региону (как в реальной куче), а не лежат подряд.
    size_t np = size / PAGE; uint32_t* perm = (uint32_t*)malloc(np * 4);
    for (size_t i = 0; i < np; i++) perm[i] = (uint32_t)i;
    for (size_t i = np - 1; i > 0; i--) { size_t j = xr() % (i + 1); uint32_t t = perm[i]; perm[i] = perm[j]; perm[j] = t; }
    uint8_t* cls = (uint8_t*)calloc(np, 1);
    for (size_t i = 0; i < hotP; i++) cls[perm[i]] = 2;
    for (size_t i = hotP; i < hotP + warmP; i++) cls[perm[i]] = 1;
    // Отдать контроллеру карту классов (только для оценки точности, алгоритм её не видит).
    printf("BASE %p SIZE %zu HOT %zu WARM %zu\n", base, size, hotP, warmP);
    HANDLE cf = CreateFileA("classes.bin", GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
    DWORD wr; WriteFile(cf, cls, (DWORD)np, &wr, NULL); CloseHandle(cf);
    fflush(stdout);
    static double fr[MAXPH][MAXFR]; int nf[MAXPH] = {0}; double maxf[MAXPH] = {0};
    timeBeginPeriod(1);
    double next = now_ms(), lastWarm = now_ms(); volatile uint64_t sink = 0;
    while (!sh->quit) {
        int ph = sh->phase; if (ph < 0 || ph >= MAXPH) ph = 0;
        double t0 = now_ms();
        for (size_t i = 0; i < hotP; i++) { uint64_t* q = (uint64_t*)(base + (size_t)perm[i] * PAGE + ((xr() & 511) * 8)); sink += *q; *q += 1; }
        if (t0 - lastWarm > 4000) { for (size_t i = hotP; i < hotP + warmP; i++) sink += *(volatile uint64_t*)(base + (size_t)perm[i] * PAGE + 64); lastWarm = t0; }
        double dt = now_ms() - t0;
        if (nf[ph] < MAXFR) fr[ph][nf[ph]++] = dt;
        if (dt > maxf[ph]) maxf[ph] = dt;
        next += 16.667; double s = next - now_ms(); if (s > 0) Sleep((DWORD)s); else next = now_ms();
    }
    for (int p = 0; p < MAXPH; p++) if (nf[p]) {
        qsort(fr[p], nf[p], sizeof(double), cmpd);
        double sum = 0; for (int i = 0; i < nf[p]; i++) sum += fr[p][i];
        int over = 0; for (int i = 0; i < nf[p]; i++) if (fr[p][i] > 16.667) over++;
        printf("FRAMES phase=%d n=%d mean_ms=%.3f p50_ms=%.3f p99_ms=%.3f p999_ms=%.3f max_ms=%.3f over16ms=%d\n", p, nf[p], sum / nf[p],
            fr[p][nf[p] / 2], fr[p][(int)(nf[p] * 0.99)], fr[p][(int)(nf[p] * 0.999)], maxf[p], over);
    }
    fflush(stdout); return (int)(sink & 0);
}

// ---------------- controller helpers ----------------
static size_t g_np; static uint8_t* g_base; static PSAPI_WORKING_SET_EX_INFORMATION* g_ws; static uint8_t* g_cls;

static int query_valid(HANDLE h, uint8_t* valid, size_t* nHot, size_t* nWarm, size_t* nCold) {
    for (size_t i = 0; i < g_np; i++) g_ws[i].VirtualAddress = g_base + i * PAGE;
    if (!QueryWorkingSetEx(h, g_ws, (DWORD)(g_np * sizeof(*g_ws)))) return 0;
    size_t a = 0, b = 0, c = 0;
    for (size_t i = 0; i < g_np; i++) { int v = g_ws[i].VirtualAttributes.Valid; if (valid) valid[i] = (uint8_t)v; if (v) { if (g_cls[i] == 2) a++; else if (g_cls[i] == 1) b++; else c++; } }
    if (nHot) *nHot = a;
    if (nWarm) *nWarm = b;
    if (nCold) *nCold = c;
    return 1;
}

static int g_method = 0; // 1 = VmRemoveFromWorkingSet, 2 = NtUnlockVirtualMemory
static NTSTATUS evict_range(HANDLE h, uint8_t* addr, size_t bytes) {
    if (g_method == 1) {
        WIN32_MEMORY_RANGE_ENTRY e = { addr, bytes }; ULONG flags = 0;
        return pNtSetVm(h, VmRemoveFromWorkingSetInformation, 1, &e, &flags, sizeof(flags));
    }
    PVOID b = addr; SIZE_T s = bytes; return pNtUnlock(h, &b, &s, 1 /*MAP_PROCESS*/);
}

static PSAPI_WS_WATCH_INFORMATION_EX* g_wbuf; static DWORD g_wcap = 1 << 16;
static long long g_wLost = 0, g_missed = 0;
// Возвращает число записей, по каждой зовёт cb(page index) для страниц региона.
static int drain_ws(HANDLE h, void (*cb)(size_t, double), double t) {
    int total = 0;
    for (int round = 0; round < 8; round++) {
        DWORD cb2 = g_wcap * sizeof(*g_wbuf);
        BOOL ok = pGetWsChangesEx(h, g_wbuf, &cb2);
        if (!ok) {
            DWORD err = GetLastError();
            if (err == ERROR_NO_MORE_ITEMS) break;
            if (err == ERROR_INSUFFICIENT_BUFFER) { g_wLost++; g_wcap = cb2 / sizeof(*g_wbuf) + 4096; g_wbuf = (PSAPI_WS_WATCH_INFORMATION_EX*)realloc(g_wbuf, g_wcap * sizeof(*g_wbuf)); continue; }
            return total ? total : -(int)err;
        }
        int n = 0;
        for (DWORD i = 0; i < g_wcap && g_wbuf[i].BasicInfo.FaultingPc; i++, n++) {
            uintptr_t va = (uintptr_t)g_wbuf[i].BasicInfo.FaultingVa & ~(uintptr_t)(PAGE - 1);
            if (va >= (uintptr_t)g_base && va < (uintptr_t)g_base + g_np * PAGE && cb) cb((va - (uintptr_t)g_base) / PAGE, t);
        }
        total += n;
        if (n == 0) break;
    }
    return total;
}

static void noop_cb(size_t i, double t) { (void)i; (void)t; }

// Состояние постраничного алгоритма
typedef struct { double nextProbe; float backoff; uint8_t out; uint8_t everHot; } Pg;
static Pg* g_pg; static long long g_refaults, g_refHot, g_refWarm, g_refCold;
static void on_fault(size_t i, double t) {
    Pg* p = &g_pg[i];
    if (p->out) {
        g_refaults++; if (g_cls[i] == 2) g_refHot++; else if (g_cls[i] == 1) g_refWarm++; else g_refCold++;
        p->out = 0; p->everHot = 1;
        p->backoff = p->backoff < 1000 ? 2000 : (p->backoff * 2 > 64000 ? 64000 : p->backoff * 2);
        p->nextProbe = t + p->backoff * (0.75 + 0.5 * (double)(xr() % 1000) / 1000.0);
    }
}

static PROCESS_MEMORY_COUNTERS_EX pmc(HANDLE h) { PROCESS_MEMORY_COUNTERS_EX c = {0}; c.cb = sizeof(c); GetProcessMemoryInfo(h, (PROCESS_MEMORY_COUNTERS*)&c, sizeof(c)); return c; }

static void sysline(const char* tag) {
    PERFORMANCE_INFORMATION pi = { sizeof(pi) }; GetPerformanceInfo(&pi, sizeof(pi));
    printf("SYS %s avail_mib=%llu commit_mib=%llu\n", tag, (unsigned long long)pi.PhysicalAvailable * pi.PageSize >> 20, (unsigned long long)pi.CommitTotal * pi.PageSize >> 20);
}

// Цена возврата страницы в СВОЁМ процессе: сразу после вытеснения и после паузы (ОС успевает сжать/записать).
static void self_refault_cost(void) {
    size_t n = 64ull << 20; uint8_t* p = (uint8_t*)VirtualAlloc(NULL, n, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    fill(p, n);
    int waits[] = { 0, 3000, 15000 };
    for (int w = 0; w < 3; w++) {
        for (size_t i = 0; i < n; i += PAGE) p[i + 8]++;
        WIN32_MEMORY_RANGE_ENTRY e = { p, n }; ULONG f = 0;
        NTSTATUS st = g_method == 1 ? pNtSetVm(GetCurrentProcess(), VmRemoveFromWorkingSetInformation, 1, &e, &f, 4) : (VirtualUnlock(p, n), 0);
        Sleep(waits[w]);
        double t0 = now_ms(); volatile uint8_t s = 0;
        for (size_t i = 0; i < n; i += PAGE) s += p[i];
        double t1 = now_ms();
        printf("REFAULT_COST wait_ms=%d evict_status=0x%08lx us_per_page=%.2f pages=%zu\n", waits[w], (unsigned long)st, (t1 - t0) * 1000.0 / (n / PAGE), n / PAGE);
    }
    VirtualFree(p, 0, MEM_RELEASE);
}

// ---------------- realapp: тот же постраничный алгоритм на настоящих процессах (по имени exe) ----------------
#include <tlhelp32.h>
typedef struct { uint8_t* base; size_t n, off; } Rg;
typedef struct {
    DWORD pid; HANDLE h; int wsw; Rg* rg; size_t nrg, N; Pg* pg; uint8_t* valid; PSAPI_WORKING_SET_EX_INFORMATION* ws;
    long long evicted, refaults, missed, records; double lastPass;
} Proc;

static void proc_scan(Proc* p) {
    // Приватная RW-память без guard/nocache; состояние страниц переносим по совпадающему началу региона.
    Rg* nr = NULL; size_t cap = 0, cnt = 0, N = 0; MEMORY_BASIC_INFORMATION mb; uint8_t* a = NULL;
    while (VirtualQueryEx(p->h, a, &mb, sizeof mb) == sizeof mb) {
        if (mb.State == MEM_COMMIT && mb.Type == MEM_PRIVATE && (mb.Protect & 0xff) == PAGE_READWRITE && !(mb.Protect & (PAGE_GUARD | PAGE_NOCACHE | PAGE_WRITECOMBINE)) && mb.RegionSize >= PAGE * 4) {
            if (cnt == cap) { cap = cap ? cap * 2 : 1024; nr = (Rg*)realloc(nr, cap * sizeof(Rg)); }
            nr[cnt].base = (uint8_t*)mb.BaseAddress; nr[cnt].n = mb.RegionSize / PAGE; nr[cnt].off = N; N += nr[cnt].n; cnt++;
        }
        uint8_t* nx = (uint8_t*)mb.BaseAddress + mb.RegionSize; if (nx <= a) break; a = nx;
    }
    Pg* npg = (Pg*)calloc(N ? N : 1, sizeof(Pg));
    double t = now_ms();
    size_t j = 0;
    for (size_t i = 0; i < cnt; i++) {
        for (size_t q = 0; q < nr[i].n; q++) npg[nr[i].off + q].nextProbe = t + 3000 + (double)(xr() % 15000); // разгон: пробы растянуты на 15 с
        while (j < p->nrg && p->rg[j].base < nr[i].base) j++;
        if (j < p->nrg && p->rg[j].base == nr[i].base) { size_t m = p->rg[j].n < nr[i].n ? p->rg[j].n : nr[i].n; memcpy(npg + nr[i].off, p->pg + p->rg[j].off, m * sizeof(Pg)); }
    }
    free(p->rg); free(p->pg); free(p->valid); free(p->ws);
    p->rg = nr; p->nrg = cnt; p->N = N; p->pg = npg; p->valid = (uint8_t*)calloc(N ? N : 1, 1);
    p->ws = (PSAPI_WORKING_SET_EX_INFORMATION*)malloc((N ? N : 1) * sizeof(*p->ws));
    for (size_t i = 0; i < cnt; i++) for (size_t q = 0; q < nr[i].n; q++) p->ws[nr[i].off + q].VirtualAddress = nr[i].base + q * PAGE;
}

static long long proc_find(Proc* p, uintptr_t va) {
    size_t lo = 0, hi = p->nrg;
    while (lo < hi) { size_t m = (lo + hi) / 2; if ((uintptr_t)p->rg[m].base + p->rg[m].n * PAGE <= va) lo = m + 1; else hi = m; }
    if (lo < p->nrg && va >= (uintptr_t)p->rg[lo].base) return (long long)(p->rg[lo].off + (va - (uintptr_t)p->rg[lo].base) / PAGE);
    return -1;
}

static void proc_fault(Proc* p, size_t i, double t) {
    Pg* g = &p->pg[i]; if (!g->out) return;
    p->refaults++; g->out = 0; g->everHot = 1;
    g->backoff = g->backoff < 1000 ? 4000 : (g->backoff * 2 > 120000 ? 120000 : g->backoff * 2);
    g->nextProbe = t + g->backoff * (0.75 + 0.5 * (double)(xr() % 1000) / 1000.0);
}

static void proc_drain(Proc* p, double t) {
    if (!p->wsw) return;
    for (int round = 0; round < 8; round++) {
        DWORD cb2 = g_wcap * sizeof(*g_wbuf);
        if (!pGetWsChangesEx(p->h, g_wbuf, &cb2)) {
            DWORD e = GetLastError();
            if (e == ERROR_INSUFFICIENT_BUFFER) { g_wcap = cb2 / sizeof(*g_wbuf) + 4096; g_wbuf = (PSAPI_WS_WATCH_INFORMATION_EX*)realloc(g_wbuf, g_wcap * sizeof(*g_wbuf)); continue; }
            return;
        }
        int n = 0;
        for (DWORD i = 0; i < g_wcap && g_wbuf[i].BasicInfo.FaultingPc; i++, n++) {
            long long k = proc_find(p, (uintptr_t)g_wbuf[i].BasicInfo.FaultingVa & ~(uintptr_t)(PAGE - 1));
            if (k >= 0) proc_fault(p, (size_t)k, t);
        }
        p->records += n; if (n == 0) break;
    }
}

// Возвращает резидентные страницы (MiB*256) из отслеживаемых.
static size_t proc_pass(Proc* p, double t, int act) {
    if (!p->N || !QueryWorkingSetEx(p->h, p->ws, (DWORD)(p->N * sizeof(*p->ws)))) return 0;
    size_t res = 0;
    for (size_t i = 0; i < p->N; i++) {
        PSAPI_WORKING_SET_EX_BLOCK b = p->ws[i].VirtualAttributes;
        p->valid[i] = (uint8_t)(b.Valid && !b.Locked && !b.LargePage && !b.Shared);
        res += b.Valid;
        if (act && p->valid[i] && p->pg[i].out) { p->missed++; proc_fault(p, i, t); }
    }
    if (!act) return res;
    for (size_t r = 0; r < p->nrg; r++) {
        size_t i = p->rg[r].off, end = p->rg[r].off + p->rg[r].n;
        while (i < end) {
            if (!(p->valid[i] && !p->pg[i].out && t >= p->pg[i].nextProbe)) { i++; continue; }
            size_t j = i; while (j < end && p->valid[j] && !p->pg[j].out && t >= p->pg[j].nextProbe) { p->pg[j].out = 1; j++; }
            evict_range(p->h, (uint8_t*)p->ws[i].VirtualAddress, (j - i) * PAGE); p->evicted += j - i; i = j;
        }
    }
    return res;
}

static long long unix_ms(void) { FILETIME f; GetSystemTimeAsFileTime(&f); ULARGE_INTEGER u; u.LowPart = f.dwLowDateTime; u.HighPart = f.dwHighDateTime; return (long long)(u.QuadPart / 10000ull) - 11644473600000ll; }
static double proc_cpu(HANDLE h) { FILETIME a, b, k, u, z = {0, 0}; GetProcessTimes(h, &a, &b, &k, &u); return ft_ms(z, k) + ft_ms(z, u); }

static int run_realapp(const char* exe, int baseSec, int actSec) {
    Proc ps[256]; int np = 0;
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0); PROCESSENTRY32 pe = { sizeof(pe) };
    for (BOOL ok = Process32First(snap, &pe); ok && np < 256; ok = Process32Next(snap, &pe)) {
        if (_stricmp(pe.szExeFile, exe)) continue;
        HANDLE h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_INFORMATION | PROCESS_VM_OPERATION | PROCESS_SET_QUOTA, FALSE, pe.th32ProcessID);
        if (!h) { printf("REAL open pid=%lu failed err=%lu\n", pe.th32ProcessID, GetLastError()); continue; }
        memset(&ps[np], 0, sizeof(Proc)); ps[np].pid = pe.th32ProcessID; ps[np].h = h; ps[np].wsw = InitializeProcessForWsWatch(h);
        proc_scan(&ps[np]); np++;
    }
    CloseHandle(snap);
    if (!g_wbuf) g_wbuf = (PSAPI_WS_WATCH_INFORMATION_EX*)malloc(g_wcap * sizeof(*g_wbuf));
    printf("REAL exe=%s processes=%d\n", exe, np);
    for (int i = 0; i < np; i++) printf("REAL pid=%lu wswatch=%d tracked_private_mib=%.1f regions=%zu\n", ps[i].pid, ps[i].wsw, ps[i].N * 4 / 1024.0, ps[i].nrg);
    const char* phn[] = { "baseline", "page_precise" };
    for (int ph = 0; ph < 2; ph++) {
        int sec = ph ? actSec : baseSec; double t0 = now_ms(), lastPass = 0, lastScan = t0, lastRep = t0;
        double cpu0[256]; for (int i = 0; i < np; i++) { cpu0[i] = proc_cpu(ps[i].h); ps[i].evicted = ps[i].refaults = ps[i].missed = ps[i].records = 0; }
        double self0 = proc_cpu(GetCurrentProcess()); double resSum = 0, resLast = 0; int resN = 0;
        PERFORMANCE_INFORMATION pi0 = { sizeof(pi0) }; GetPerformanceInfo(&pi0, sizeof(pi0));
        printf("REAL PHASE_START %s unix_ms=%lld\n", phn[ph], unix_ms()); fflush(stdout);
        while (now_ms() - t0 < sec * 1000.0) {
            double t = now_ms();
            for (int i = 0; i < np; i++) proc_drain(&ps[i], t);
            if (t - lastScan > 10000) { for (int i = 0; i < np; i++) proc_scan(&ps[i]); lastScan = t; }
            if (t - lastPass >= 1000) {
                lastPass = t; double r = 0; size_t tr = 0;
                for (int i = 0; i < np; i++) { r += proc_pass(&ps[i], t, ph == 1); tr += ps[i].N; }
                resLast = r * 4 / 1024.0; if (t - t0 > sec * 500.0) { resSum += resLast; resN++; } // вторая половина фазы
                if (t - lastRep >= 10000) {
                    long long ev = 0, rf = 0; for (int i = 0; i < np; i++) { ev += ps[i].evicted; rf += ps[i].refaults; }
                    printf("REAL tick phase=%s t=%.0fs resident_private_mib=%.1f tracked_mib=%.1f evicted=%lld refaults=%lld\n", phn[ph], (t - t0) / 1000, resLast, tr * 4 / 1024.0, ev, rf);
                    fflush(stdout); lastRep = t;
                }
            }
            Sleep(100);
        }
        double cpu = 0; long long ev = 0, rf = 0, ms = 0, rec = 0; size_t tr = 0;
        for (int i = 0; i < np; i++) { cpu += proc_cpu(ps[i].h) - cpu0[i]; ev += ps[i].evicted; rf += ps[i].refaults; ms += ps[i].missed; rec += ps[i].records; tr += ps[i].N; }
        PERFORMANCE_INFORMATION pi1 = { sizeof(pi1) }; GetPerformanceInfo(&pi1, sizeof(pi1));
        printf("REAL PHASE_END %s unix_ms=%lld\n", phn[ph], unix_ms());
        printf("REAL PHASE %s sec=%d resident_private_mib_avg_2nd_half=%.1f tracked_mib=%.1f evicted_pages=%lld refaults=%lld refaults_per_s=%.1f missed=%lld wsrecords=%lld app_cpu_ms=%.0f agent_cpu_ms=%.0f sys_avail_delta_mib=%lld\n",
            phn[ph], sec, resN ? resSum / resN : resLast, tr * 4 / 1024.0, ev, rf, rf / (double)sec, ms, rec, cpu, proc_cpu(GetCurrentProcess()) - self0,
            ((long long)pi1.PhysicalAvailable - (long long)pi0.PhysicalAvailable) * (long long)pi0.PageSize >> 20);
        fflush(stdout);
    }
    return 0;
}

int main(int argc, char** argv) {
    LARGE_INTEGER f; QueryPerformanceFrequency(&f); qpf = (double)f.QuadPart;
    HMODULE nt = GetModuleHandleA("ntdll.dll");
    pNtUnlock = (PFN_NtUnlockVirtualMemory)GetProcAddress(nt, "NtUnlockVirtualMemory");
    pNtSetVm = (PFN_NtSetInformationVirtualMemory)GetProcAddress(nt, "NtSetInformationVirtualMemory");
    pGetWsChangesEx = (PFN_GetWsChangesEx)GetProcAddress(GetModuleHandleA("kernel32.dll"), "K32GetWsChangesEx");
    if (argc >= 5 && !strcmp(argv[1], "target")) return run_target(atoi(argv[2]), atoi(argv[3]), atoi(argv[4]));
    if (argc >= 5 && !strcmp(argv[1], "realapp")) {
        // Метод вытеснения: тот, что сработал в синтетике; по умолчанию VmRemoveFromWorkingSet, иначе NtUnlock.
        g_method = argc > 5 ? atoi(argv[5]) : 1;
        return run_realapp(argv[2], atoi(argv[3]), atoi(argv[4]));
    }

    int totalMiB = argc > 1 ? atoi(argv[1]) : 1024, hotMiB = argc > 2 ? atoi(argv[2]) : 96, warmMiB = argc > 3 ? atoi(argv[3]) : 64, phaseSec = argc > 4 ? atoi(argv[4]) : 25;
    OSVERSIONINFOEXW ov = { sizeof(ov) }; typedef LONG (WINAPI *RtlGV)(OSVERSIONINFOEXW*); ((RtlGV)GetProcAddress(nt, "RtlGetVersion"))(&ov);
    printf("OS %lu.%lu.%lu total=%d hot=%d warm=%d phase_s=%d\n", ov.dwMajorVersion, ov.dwMinorVersion, ov.dwBuildNumber, totalMiB, hotMiB, warmMiB, phaseSec);

    HANDLE m = CreateFileMappingA(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, sizeof(Shared), "Local\\EmActiveTrimProbe");
    Shared* sh = (Shared*)MapViewOfFile(m, FILE_MAP_WRITE, 0, 0, sizeof(Shared)); sh->phase = 0; sh->quit = 0;

    SECURITY_ATTRIBUTES sa = { sizeof(sa), NULL, TRUE }; HANDLE rd, wrp; CreatePipe(&rd, &wrp, &sa, 0); SetHandleInformation(rd, HANDLE_FLAG_INHERIT, 0);
    STARTUPINFOA si = { sizeof(si) }; si.dwFlags = STARTF_USESTDHANDLES; si.hStdOutput = wrp; si.hStdError = wrp;
    PROCESS_INFORMATION pi; char cmd[512]; char self[MAX_PATH]; GetModuleFileNameA(NULL, self, MAX_PATH);
    snprintf(cmd, sizeof cmd, "\"%s\" target %d %d %d", self, totalMiB, hotMiB, warmMiB);
    if (!CreateProcessA(NULL, cmd, NULL, NULL, TRUE, 0, NULL, NULL, &si, &pi)) { printf("ERR CreateProcess %lu\n", GetLastError()); return 1; }
    CloseHandle(wrp);
    char line[512]; int li = 0; DWORD got; char ch;
    while (ReadFile(rd, &ch, 1, &got, NULL) && got) { if (ch == '\n') break; if (li < 510) line[li++] = ch; }
    line[li] = 0; printf("TARGET %s\n", line);
    void* bp; size_t sz, hp, wp;
    if (sscanf(line, "BASE %p SIZE %zu HOT %zu WARM %zu", &bp, &sz, &hp, &wp) != 4) { printf("ERR target line\n"); return 1; }
    g_base = (uint8_t*)bp; g_np = sz / PAGE;
    Sleep(300);
    g_cls = (uint8_t*)malloc(g_np); { HANDLE cf = CreateFileA("classes.bin", GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL); DWORD r; ReadFile(cf, g_cls, (DWORD)g_np, &r, NULL); CloseHandle(cf); }
    g_ws = (PSAPI_WORKING_SET_EX_INFORMATION*)malloc(g_np * sizeof(*g_ws));
    g_wbuf = (PSAPI_WS_WATCH_INFORMATION_EX*)malloc(g_wcap * sizeof(*g_wbuf));
    g_pg = (Pg*)calloc(g_np, sizeof(Pg));
    uint8_t* valid = (uint8_t*)malloc(g_np);
    // Минимальные права, которые реально понадобятся агенту для чужого процесса.
    HANDLE h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_INFORMATION | PROCESS_VM_OPERATION | PROCESS_SET_QUOTA, FALSE, pi.dwProcessId);
    printf("OPEN rights=QUERY|SET_INFORMATION|VM_OPERATION|SET_QUOTA handle=%s err=%lu\n", h ? "ok" : "fail", h ? 0 : GetLastError());
    if (!h) return 1;

    // -------- 1. API check на чужом процессе (холодные страницы) --------
    size_t a, b, c; query_valid(h, NULL, &a, &b, &c); printf("WS_BEFORE hot=%zu warm=%zu cold=%zu (pages)\n", a, b, c);
    BOOL wsw = InitializeProcessForWsWatch(h); printf("API InitializeProcessForWsWatch=%d err=%lu\n", wsw, wsw ? 0 : GetLastError());
    // Найти 4096 подряд идущих холодных страниц для проверки методов вытеснения
    size_t run0 = 0, runLen = 0;
    for (size_t i = 0, s = 0; i < g_np; i++) { if (g_cls[i]) s = i + 1; else if (i + 1 - s >= 256) { run0 = s; runLen = 256; break; } }
    const char* names[] = { "", "VmRemoveFromWorkingSet", "NtUnlockVirtualMemory" };
    int best = 0;
    for (int meth = 1; meth <= 2; meth++) {
        if ((meth == 1 && !pNtSetVm) || (meth == 2 && !pNtUnlock) || !runLen) { printf("API %s missing\n", names[meth]); continue; }
        size_t r0 = run0 + (meth - 1) * 128, rl = 128; // половина серии на каждый метод
        size_t before = 0, after = 0; query_valid(h, valid, NULL, NULL, NULL); for (size_t i = r0; i < r0 + rl; i++) before += valid[i];
        g_method = meth; double t0 = now_ms(); NTSTATUS st = evict_range(h, g_base + r0 * PAGE, rl * PAGE); double t1 = now_ms();
        query_valid(h, valid, NULL, NULL, NULL); for (size_t i = r0; i < r0 + rl; i++) after += valid[i];
        size_t hotAfter; query_valid(h, NULL, &hotAfter, NULL, NULL);
        printf("API %s status=0x%08lx resident_before=%zu resident_after=%zu of %zu call_us=%.0f hot_resident=%zu\n", names[meth], (unsigned long)st, before, after, rl, (t1 - t0) * 1000, hotAfter);
        if (after < before / 4 && !best) best = meth;
    }
    {   // PrefetchVirtualMemory на чужом процессе (возврат заранее) и приоритет страниц
        WIN32_MEMORY_RANGE_ENTRY e = { g_base + run0 * PAGE, 128 * PAGE };
        BOOL pf = PrefetchVirtualMemory(h, 1, &e, 0); DWORD pe = pf ? 0 : GetLastError(); Sleep(200);
        size_t back = 0; query_valid(h, valid, NULL, NULL, NULL); for (size_t i = run0; i < run0 + 128; i++) back += valid[i];
        printf("API PrefetchVirtualMemory(foreign)=%d err=%lu resident_after_prefetch=%zu of 128 (prefetch may fill cache without WS)\n", pf, pe, back);
        ULONG prio = 1; NTSTATUS ps = pNtSetVm ? pNtSetVm(h, VmPagePriorityInformation, 1, &e, &prio, 4) : -1;
        printf("API VmPagePriority(foreign, prio=1) status=0x%08lx\n", (unsigned long)ps);
    }
    if (!best) { printf("RESULT no page-granular eviction method works on foreign process\n"); g_method = 2; } else g_method = best;
    printf("METHOD %s\n", names[g_method]);
    int dr = drain_ws(h, noop_cb, now_ms()); printf("API GetWsChangesEx first drain=%d\n", dr);
    self_refault_cost();

    // -------- 2. фазы A/B --------
    const char* phn[] = { "baseline", "empty_ws_every_2s", "page_precise", "baseline2" };
    for (int ph = 0; ph < 4; ph++) {
        sh->phase = ph; drain_ws(h, noop_cb, now_ms());
        PROCESS_MEMORY_COUNTERS_EX m0 = pmc(h); FILETIME x, y, k0, u0, k1, u1; GetProcessTimes(h, &x, &y, &k0, &u0);
        FILETIME sk0, su0, sk1, su1; GetProcessTimes(GetCurrentProcess(), &x, &y, &sk0, &su0);
        g_refaults = g_refHot = g_refWarm = g_refCold = 0; g_missed = 0;
        double t0 = now_ms(), lastPass = 0, lastTrim = 0; double sumHot = 0, sumWarm = 0, sumCold = 0; int samples = 0; long long evicted = 0, wsRecords = 0, ranges = 0;
        if (ph == 2) for (size_t i = 0; i < g_np; i++) { g_pg[i].nextProbe = t0 + (double)(xr() % 8000); g_pg[i].backoff = 0; g_pg[i].out = 0; }
        sysline(phn[ph]);
        while (now_ms() - t0 < phaseSec * 1000.0) {
            double t = now_ms();
            int d = drain_ws(h, ph == 2 ? on_fault : noop_cb, t); if (d > 0) wsRecords += d;
            if (ph == 1 && t - lastTrim >= 2000) { EmptyWorkingSet(h); lastTrim = t; }
            if (t - lastPass >= 500) {
                lastPass = t; size_t vh, vw, vc; query_valid(h, valid, &vh, &vw, &vc);
                if (t - t0 > 8000) { sumHot += vh; sumWarm += vw; sumCold += vc; samples++; } // после разгона
                if (ph == 2) {
                    // Страница снова резидентна, а запись WS Watch потерялась — тоже считаем возвратом.
                    for (size_t q = 0; q < g_np; q++) if (valid[q] && g_pg[q].out) { g_missed++; on_fault(q, t); }
                    // Вытеснить резидентные страницы, у которых подошёл срок пробы. Сливаем в непрерывные диапазоны.
                    size_t i = 0;
                    while (i < g_np) {
                        if (!(valid[i] && !g_pg[i].out && t >= g_pg[i].nextProbe)) { i++; continue; }
                        size_t j = i; while (j < g_np && valid[j] && !g_pg[j].out && t >= g_pg[j].nextProbe) { g_pg[j].out = 1; j++; }
                        evict_range(h, g_base + i * PAGE, (j - i) * PAGE); evicted += j - i; ranges++; i = j;
                    }
                }
            }
            Sleep(100);
        }
        PROCESS_MEMORY_COUNTERS_EX m1 = pmc(h); GetProcessTimes(h, &x, &y, &k1, &u1); GetProcessTimes(GetCurrentProcess(), &x, &y, &sk1, &su1);
        double tcpu = ft_ms(k0, k1) + ft_ms(u0, u1);
        double scpu = ft_ms(sk0, sk1) + ft_ms(su0, su1);
        double ns = samples ? samples : 1;
        printf("PHASE %s resident_mib hot=%.1f warm=%.1f cold=%.1f total=%.1f of %zu | pagefaults=%lu ws_records=%lld | evicted_pages=%lld ranges=%lld refaults hot=%lld warm=%lld cold=%lld missed_ws_records=%lld | target_cpu_ms=%.0f agent_cpu_ms=%.0f\n",
            phn[ph], sumHot / ns * 4 / 1024, sumWarm / ns * 4 / 1024, sumCold / ns * 4 / 1024, (sumHot + sumWarm + sumCold) / ns * 4 / 1024, sz >> 20,
            m1.PageFaultCount - m0.PageFaultCount, wsRecords, evicted, ranges, g_refHot, g_refWarm, g_refCold, g_missed, tcpu, scpu);
        sysline(phn[ph]);
        fflush(stdout);
    }
    sh->quit = 1;
    WaitForSingleObject(pi.hProcess, 10000);
    li = 0; while (ReadFile(rd, &ch, 1, &got, NULL) && got) { if (ch == '\n') { line[li] = 0; printf("TARGET %s\n", line); li = 0; } else if (li < 510) line[li++] = ch; }
    return 0;
}
