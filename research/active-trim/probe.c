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
#define MAXPH 16
#define MAXFR 8192
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

// ---------------- движок постраничного вытеснения (v2) ----------------
// Сигнал доступа: страница, которую мы вытеснили, снова стала резидентной (QueryWorkingSetEx.Valid) —
// значит процесс её тронул. Проверяем только вытесненные страницы (разреженный запрос), часто и дёшево.
// Политика: «неизвестные» страницы пробуем с бюджетом, который растёт, пока они не возвращаются (<5 %),
// и падает, если возвращаются (>20 %) — контроллер по доле возвратов, как TMO/PSI. Горячие (уже возвращались)
// пробуем редко (экспоненциальная пауза 8 с → ×4 → 5 мин) и малыми порциями, чтобы не было всплеска в кадре.
#include <tlhelp32.h>
static int g_method = 2; // 1 = VmRemoveFromWorkingSet, 2 = NtUnlockVirtualMemory
static NTSTATUS evict_range(HANDLE h, uint8_t* addr, size_t bytes) {
    if (g_method == 1) { WIN32_MEMORY_RANGE_ENTRY e = { addr, bytes }; ULONG flags = 0; return pNtSetVm(h, VmRemoveFromWorkingSetInformation, 1, &e, &flags, sizeof(flags)); }
    PVOID b = addr; SIZE_T s = bytes; return pNtUnlock(h, &b, &s, 1 /*MAP_PROCESS*/);
}
static long long unix_ms(void) { FILETIME f; GetSystemTimeAsFileTime(&f); ULARGE_INTEGER u; u.LowPart = f.dwLowDateTime; u.HighPart = f.dwHighDateTime; return (long long)(u.QuadPart / 10000ull) - 11644473600000ll; }
static double proc_cpu(HANDLE h) { FILETIME a, b, k, u, z = {0, 0}; GetProcessTimes(h, &a, &b, &k, &u); return ft_ms(z, k) + ft_ms(z, u); }

typedef struct { double nextProbe; float backoff; uint8_t out, everHot, pick; } Pg;
typedef struct { uint8_t* base; size_t n, off; } Rg;
typedef struct {
    DWORD pid; HANDLE h; Rg* rg; size_t nrg, N; Pg* pg; uint8_t* valid; PSAPI_WORKING_SET_EX_INFORMATION* ws;
    uint32_t* out; size_t nOut, capOut; PSAPI_WORKING_SET_EX_INFORMATION* q;
    double budgetU; long long probedU, backU;          // окно контроллера
    long long evicted, refaults, refHot, refUnknown, hotProbes; size_t cursor; size_t resident;
    const uint8_t* cls; long long refCls[3];
} Proc;

static void proc_set_regions(Proc* p, Rg* nr, size_t cnt) {
    size_t N = 0; for (size_t i = 0; i < cnt; i++) { nr[i].off = N; N += nr[i].n; }
    Pg* npg = (Pg*)calloc(N ? N : 1, sizeof(Pg)); double t = now_ms(); size_t j = 0;
    for (size_t i = 0; i < cnt; i++) {
        for (size_t k = 0; k < nr[i].n; k++) npg[nr[i].off + k].nextProbe = t + 2000;
        while (j < p->nrg && p->rg[j].base < nr[i].base) j++;
        if (j < p->nrg && p->rg[j].base == nr[i].base) { size_t m = p->rg[j].n < nr[i].n ? p->rg[j].n : nr[i].n; memcpy(npg + nr[i].off, p->pg + p->rg[j].off, m * sizeof(Pg)); }
    }
    free(p->rg); free(p->pg); free(p->valid); free(p->ws);
    p->rg = nr; p->nrg = cnt; p->N = N; p->pg = npg; p->valid = (uint8_t*)calloc(N ? N : 1, 1);
    p->ws = (PSAPI_WORKING_SET_EX_INFORMATION*)malloc((N ? N : 1) * sizeof(*p->ws));
    for (size_t i = 0; i < cnt; i++) for (size_t k = 0; k < nr[i].n; k++) p->ws[nr[i].off + k].VirtualAddress = nr[i].base + k * PAGE;
    // список вытесненных пересобираем из состояния
    p->nOut = 0; if (p->capOut < N) { p->capOut = N; p->out = (uint32_t*)realloc(p->out, N * 4); p->q = (PSAPI_WORKING_SET_EX_INFORMATION*)realloc(p->q, N * sizeof(*p->q)); }
    for (size_t i = 0; i < N; i++) if (npg[i].out) p->out[p->nOut++] = (uint32_t)i;
    if (p->cursor >= N) p->cursor = 0;
}

static void proc_scan(Proc* p) {   // приватная RW-память без guard/nocache
    Rg* nr = NULL; size_t cap = 0, cnt = 0; MEMORY_BASIC_INFORMATION mb; uint8_t* a = NULL;
    while (VirtualQueryEx(p->h, a, &mb, sizeof mb) == sizeof mb) {
        if (mb.State == MEM_COMMIT && mb.Type == MEM_PRIVATE && (mb.Protect & 0xff) == PAGE_READWRITE && !(mb.Protect & (PAGE_GUARD | PAGE_NOCACHE | PAGE_WRITECOMBINE)) && mb.RegionSize >= PAGE * 4) {
            if (cnt == cap) { cap = cap ? cap * 2 : 1024; nr = (Rg*)realloc(nr, cap * sizeof(Rg)); }
            nr[cnt].base = (uint8_t*)mb.BaseAddress; nr[cnt].n = mb.RegionSize / PAGE; cnt++;
        }
        uint8_t* nx = (uint8_t*)mb.BaseAddress + mb.RegionSize; if (nx <= a) break; a = nx;
    }
    proc_set_regions(p, nr, cnt);
}

static void proc_init(Proc* p, DWORD pid, HANDLE h) { memset(p, 0, sizeof *p); p->pid = pid; p->h = h; p->budgetU = 2048; }

static void on_return(Proc* p, size_t i, double t) {
    Pg* g = &p->pg[i]; p->refaults++;
    if (g->everHot) p->refHot++; else { p->refUnknown++; p->backU++; }
    if (p->cls) p->refCls[p->cls[i]]++;
    g->out = 0; g->everHot = 1;
    g->backoff = g->backoff < 1000 ? 8000 : (g->backoff * 4 > 300000 ? 300000 : g->backoff * 4);
    g->nextProbe = t + g->backoff * (0.75 + 0.5 * (double)(xr() % 1000) / 1000.0);
}

// Быстрая проверка: какие из вытесненных страниц вернулись.
static void proc_check(Proc* p, double t) {
    if (!p->nOut) return;
    for (size_t k = 0; k < p->nOut; k++) p->q[k].VirtualAddress = p->ws[p->out[k]].VirtualAddress;
    if (!QueryWorkingSetEx(p->h, p->q, (DWORD)(p->nOut * sizeof(*p->q)))) return;
    size_t w = 0;
    for (size_t k = 0; k < p->nOut; k++) { if (p->q[k].VirtualAttributes.Valid) on_return(p, p->out[k], t); else p->out[w++] = p->out[k]; }
    p->nOut = w;
}

// Полный опрос резидентности (раз в 2 с) + обновление бюджета по доле возвратов «неизвестных» страниц.
// Бюджет задаётся так, чтобы на один срез (250 мс) приходилось не больше ~TARGET_BACK возвратов:
// всплеск в кадре = возвраты × цена возврата, а не доля памяти.
#define TARGET_BACK 100.0
static void proc_refresh(Proc* p, int act) {
    if (!p->N || !QueryWorkingSetEx(p->h, p->ws, (DWORD)(p->N * sizeof(*p->ws)))) return;
    size_t res = 0;
    for (size_t i = 0; i < p->N; i++) { PSAPI_WORKING_SET_EX_BLOCK b = p->ws[i].VirtualAttributes; res += b.Valid; p->valid[i] = (uint8_t)(b.Valid && !b.Locked && !b.LargePage && !b.Shared); }
    p->resident = res;
    if (!act) return;
    if (p->probedU >= 64) {
        double r = (double)p->backU / (double)p->probedU; if (r < 0.005) r = 0.005;
        double want = TARGET_BACK / r; p->budgetU = 0.5 * p->budgetU + 0.5 * want;
        if (p->budgetU < 128) p->budgetU = 128; if (p->budgetU > 65536) p->budgetU = 65536;
        p->probedU = 0; p->backU = 0;
    }
}

// Срез вытеснения (раз в 250 мс) по кэшированной карте резидентности.
static void proc_evict(Proc* p, double t, int hotBudget) {
    long long bu = (long long)p->budgetU, bh = hotBudget;
    size_t s = 0;
    for (; s < p->N && (bu > 0 || bh > 0); s++) {
        size_t i = (p->cursor + s) % p->N; Pg* g = &p->pg[i];
        if (!p->valid[i] || g->out || t < g->nextProbe) continue;
        if (g->everHot) { if (bh <= 0) continue; bh--; p->hotProbes++; } else { if (bu <= 0) continue; bu--; p->probedU++; }
        g->pick = 1; p->valid[i] = 0;
    }
    p->cursor = p->N ? (p->cursor + s) % p->N : 0;
    for (size_t r = 0; r < p->nrg; r++) {
        size_t i = p->rg[r].off, end = i + p->rg[r].n;
        while (i < end) {
            if (!p->pg[i].pick) { i++; continue; }
            size_t j = i; while (j < end && p->pg[j].pick) { p->pg[j].pick = 0; p->pg[j].out = 1; p->out[p->nOut++] = (uint32_t)j; j++; }
            evict_range(p->h, (uint8_t*)p->ws[i].VirtualAddress, (j - i) * PAGE); p->evicted += j - i; i = j;
        }
    }
}

static void sysline(const char* tag) {
    PERFORMANCE_INFORMATION pi = { sizeof(pi) }; GetPerformanceInfo(&pi, sizeof(pi));
    printf("SYS %s avail_mib=%llu commit_mib=%llu\n", tag, (unsigned long long)pi.PhysicalAvailable * pi.PageSize >> 20, (unsigned long long)pi.CommitTotal * pi.PageSize >> 20);
}

// Цена возврата страницы в своём процессе сразу и после паузы.
static void self_refault_cost(void) {
    size_t n = 64ull << 20; uint8_t* p = (uint8_t*)VirtualAlloc(NULL, n, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE); fill(p, n);
    int waits[] = { 0, 3000, 15000 };
    for (int w = 0; w < 3; w++) {
        for (size_t i = 0; i < n; i += PAGE) p[i + 8]++;
        NTSTATUS st = evict_range(GetCurrentProcess(), p, n); Sleep(waits[w]);
        double t0 = now_ms(); volatile uint8_t s = 0; for (size_t i = 0; i < n; i += PAGE) s += p[i]; double t1 = now_ms();
        printf("REFAULT_COST wait_ms=%d evict_status=0x%08lx us_per_page=%.2f pages=%zu\n", waits[w], (unsigned long)st, (t1 - t0) * 1000.0 / (n / PAGE), n / PAGE);
    }
    VirtualFree(p, 0, MEM_RELEASE);
}

static void api_check(HANDLE h, uint8_t* base, size_t np, const uint8_t* cls) {
    const char* names[] = { "", "VmRemoveFromWorkingSet", "NtUnlockVirtualMemory" };
    PSAPI_WORKING_SET_EX_INFORMATION q[16]; size_t found = 0;
    for (size_t i = 0; i + 16 < np && found < 2; i++) {
        int ok = 1; for (size_t k = 0; k < 16; k++) if (cls[i + k]) { ok = 0; break; }
        if (!ok) continue;
        int meth = (int)found + 1; g_method = meth;
        double t0 = now_ms(); NTSTATUS st = evict_range(h, base + i * PAGE, 16 * PAGE); double t1 = now_ms();
        for (size_t k = 0; k < 16; k++) q[k].VirtualAddress = base + (i + k) * PAGE;
        QueryWorkingSetEx(h, q, sizeof q); int left = 0; for (int k = 0; k < 16; k++) left += q[k].VirtualAttributes.Valid;
        printf("API %s(foreign) status=0x%08lx resident_after=%d of 16 call_us=%.1f\n", names[meth], (unsigned long)st, left, (t1 - t0) * 1000);
        if (meth == 1) {
            WIN32_MEMORY_RANGE_ENTRY e = { base + i * PAGE, 16 * PAGE }; BOOL pf = PrefetchVirtualMemory(h, 1, &e, 0); Sleep(100);
            QueryWorkingSetEx(h, q, sizeof q); int back = 0; for (int k = 0; k < 16; k++) back += q[k].VirtualAttributes.Valid;
            printf("API PrefetchVirtualMemory(foreign)=%d resident_after=%d of 16\n", pf, back);
            ULONG prio = 1; printf("API VmPagePriority(foreign) status=0x%08lx\n", (unsigned long)pNtSetVm(h, VmPagePriorityInformation, 1, &e, &prio, 4));
        }
        found++; i += 16;
    }
    BOOL w = InitializeProcessForWsWatch(h); printf("API InitializeProcessForWsWatch=%d (v2 не использует: в v1 терялось ~70%% записей)\n", w);
}

// ---------------- синтетика ----------------
static int run_synthetic(int totalMiB, int hotMiB, int warmMiB, int phaseSec) {
    HMODULE nt = GetModuleHandleA("ntdll.dll");
    OSVERSIONINFOEXW ov = { sizeof(ov) }; typedef LONG (WINAPI *RtlGV)(OSVERSIONINFOEXW*); ((RtlGV)GetProcAddress(nt, "RtlGetVersion"))(&ov);
    printf("OS %lu.%lu.%lu total=%d hot=%d warm=%d phase_s=%d\n", ov.dwMajorVersion, ov.dwMinorVersion, ov.dwBuildNumber, totalMiB, hotMiB, warmMiB, phaseSec);
    HANDLE m = CreateFileMappingA(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, sizeof(Shared), "Local\\EmActiveTrimProbe");
    Shared* sh = (Shared*)MapViewOfFile(m, FILE_MAP_WRITE, 0, 0, sizeof(Shared)); sh->phase = 0; sh->quit = 0;
    SECURITY_ATTRIBUTES sa = { sizeof(sa), NULL, TRUE }; HANDLE rd, wrp; CreatePipe(&rd, &wrp, &sa, 0); SetHandleInformation(rd, HANDLE_FLAG_INHERIT, 0);
    STARTUPINFOA si = { sizeof(si) }; si.dwFlags = STARTF_USESTDHANDLES; si.hStdOutput = wrp; si.hStdError = wrp;
    PROCESS_INFORMATION pi; char cmd[512], self[MAX_PATH]; GetModuleFileNameA(NULL, self, MAX_PATH);
    snprintf(cmd, sizeof cmd, "\"%s\" target %d %d %d", self, totalMiB, hotMiB, warmMiB);
    if (!CreateProcessA(NULL, cmd, NULL, NULL, TRUE, 0, NULL, NULL, &si, &pi)) { printf("ERR CreateProcess %lu\n", GetLastError()); return 1; }
    CloseHandle(wrp);
    char line[512]; int li = 0; DWORD got; char ch;
    while (ReadFile(rd, &ch, 1, &got, NULL) && got) { if (ch == '\n') break; if (li < 510) line[li++] = ch; }
    line[li] = 0; printf("TARGET %s\n", line);
    void* bp; size_t sz, hp, wp;
    if (sscanf(line, "BASE %p SIZE %zu HOT %zu WARM %zu", &bp, &sz, &hp, &wp) != 4) { printf("ERR target line\n"); return 1; }
    size_t np = sz / PAGE; Sleep(300);
    uint8_t* cls = (uint8_t*)malloc(np); { HANDLE cf = CreateFileA("classes.bin", GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL); DWORD r; ReadFile(cf, cls, (DWORD)np, &r, NULL); CloseHandle(cf); }
    // Минимальные права агента: чтение рабочего набора + операции с памятью (+ SET_QUOTA только для EmptyWorkingSet-сравнения).
    HANDLE h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_SET_QUOTA, FALSE, pi.dwProcessId);
    printf("OPEN rights=QUERY_INFORMATION|VM_OPERATION|SET_QUOTA handle=%s\n", h ? "ok" : "fail"); if (!h) return 1;
    api_check(h, (uint8_t*)bp, np, cls);
    g_method = 1; self_refault_cost();

    Proc P; proc_init(&P, pi.dwProcessId, h); P.cls = cls;
    Rg* one = (Rg*)malloc(sizeof(Rg)); one->base = (uint8_t*)bp; one->n = np; proc_set_regions(&P, one, 1);
    const char* phn[] = { "baseline", "empty_ws_every_2s", "page_precise_v2", "baseline_after" };
    int secs[] = { phaseSec / 2, phaseSec, phaseSec * 2, phaseSec / 2 };
    for (int ph = 0; ph < 4; ph++) {
        double t0 = now_ms(), lastPass = 0, lastChk = 0, lastTrim = 0, halfAt = t0 + secs[ph] * 500.0; int half = 0;
        sh->phase = ph * 2;
        P.evicted = P.refaults = P.refHot = P.refUnknown = P.hotProbes = 0; memset(P.refCls, 0, sizeof P.refCls);
        PROCESS_MEMORY_COUNTERS c0 = { sizeof(c0) }, c1 = { sizeof(c1) }; double tc0 = proc_cpu(h), sc0 = proc_cpu(GetCurrentProcess());
        double sumC[3] = {0}; int ns = 0; long long ev2 = 0, rf2 = 0, rh2 = 0;
        sysline(phn[ph]);
        while (now_ms() - t0 < secs[ph] * 1000.0) {
            double t = now_ms();
            if (!half && t >= halfAt) { half = 1; sh->phase = ph * 2 + 1; GetProcessMemoryInfo(h, &c0, sizeof c0); ev2 = P.evicted; rf2 = P.refaults; rh2 = P.refHot; }
            if (ph == 1 && t - lastTrim >= 2000) { EmptyWorkingSet(h); lastTrim = t; }
            if (ph == 2 && t - lastChk >= 250) { proc_check(&P, t); proc_evict(&P, t, 8); lastChk = t; }
            if (t - lastPass >= 2000) {
                lastPass = t; proc_refresh(&P, ph == 2);
                if (half) { for (size_t i = 0; i < np; i++) if (P.ws[i].VirtualAttributes.Valid) sumC[cls[i]] += 1; ns++; }
            }
            Sleep(50);
        }
        GetProcessMemoryInfo(h, &c1, sizeof c1);
        double d = ns ? ns : 1, sec2 = secs[ph] / 2.0;
        printf("PHASE %s sec=%d [2nd half] resident_mib hot=%.1f warm=%.1f cold=%.1f total=%.1f of %zu | pagefaults_per_s=%.0f | evicted_per_s=%.0f refaults_per_s=%.0f (of them hot=%.0f) budgetU=%.0f | target_cpu_ms=%.0f agent_cpu_ms=%.0f (whole phase)\n",
            phn[ph], secs[ph], sumC[2] / d / 256, sumC[1] / d / 256, sumC[0] / d / 256, (sumC[0] + sumC[1] + sumC[2]) / d / 256, sz >> 20,
            (c1.PageFaultCount - c0.PageFaultCount) / sec2, (P.evicted - ev2) / sec2, (P.refaults - rf2) / sec2, (P.refHot - rh2) / sec2, P.budgetU, proc_cpu(h) - tc0, proc_cpu(GetCurrentProcess()) - sc0);
        sysline(phn[ph]); fflush(stdout);
    }
    sh->quit = 1; WaitForSingleObject(pi.hProcess, 10000);
    printf("FRAMES legend: phase=2k -> 1st half of phase k, 2k+1 -> 2nd half (steady state)\n");
    li = 0; while (ReadFile(rd, &ch, 1, &got, NULL) && got) { if (ch == '\n') { line[li] = 0; if (li) printf("TARGET %s\n", line); li = 0; } else if (ch != '\r' && li < 510) line[li++] = ch; }
    return 0;
}

// ---------------- настоящие процессы по имени exe ----------------
static int run_realapp(const char* exe, int baseSec, int actSec) {
    static Proc ps[256]; int np = 0;
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0); PROCESSENTRY32 pe = { sizeof(pe) };
    for (BOOL ok = Process32First(snap, &pe); ok && np < 256; ok = Process32Next(snap, &pe)) {
        if (_stricmp(pe.szExeFile, exe)) continue;
        HANDLE h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION, FALSE, pe.th32ProcessID);
        if (!h) { printf("REAL open pid=%lu failed err=%lu\n", pe.th32ProcessID, GetLastError()); continue; }
        proc_init(&ps[np], pe.th32ProcessID, h); proc_scan(&ps[np]); np++;
    }
    CloseHandle(snap);
    size_t tr = 0; for (int i = 0; i < np; i++) tr += ps[i].N;
    printf("REAL exe=%s processes=%d tracked_private_mib=%.1f method=%d\n", exe, np, tr / 256.0, g_method);
    const char* phn[] = { "baseline", "page_precise" };
    for (int ph = 0; ph < 2; ph++) {
        int sec = ph ? actSec : baseSec; double t0 = now_ms(), lastPass = 0, lastChk = 0, lastScan = t0, lastRep = t0, halfAt = t0 + sec * 500.0; int half = 0;
        double cpu0 = 0, cpuH = 0; for (int i = 0; i < np; i++) { cpu0 += proc_cpu(ps[i].h); ps[i].evicted = ps[i].refaults = ps[i].refHot = ps[i].hotProbes = 0; }
        double selfH = 0; double resSum = 0; int resN = 0; long long ev2 = 0, rf2 = 0, rh2 = 0;
        PERFORMANCE_INFORMATION pi0 = { sizeof(pi0) }; GetPerformanceInfo(&pi0, sizeof(pi0));
        printf("REAL PHASE_START %s unix_ms=%lld\n", phn[ph], unix_ms()); fflush(stdout);
        while (now_ms() - t0 < sec * 1000.0) {
            double t = now_ms();
            if (!half && t >= halfAt) { half = 1; cpuH = 0; for (int i = 0; i < np; i++) { cpuH += proc_cpu(ps[i].h); ev2 += ps[i].evicted; rf2 += ps[i].refaults; rh2 += ps[i].refHot; } selfH = proc_cpu(GetCurrentProcess()); printf("REAL HALF %s unix_ms=%lld\n", phn[ph], unix_ms()); }
            if (ph == 1 && t - lastChk >= 250) { for (int i = 0; i < np; i++) { proc_check(&ps[i], t); proc_evict(&ps[i], t, 4); } lastChk = t; }
            if (t - lastScan > 15000) { for (int i = 0; i < np; i++) proc_scan(&ps[i]); lastScan = t; }
            if (t - lastPass >= 2000) {
                lastPass = t; size_t r = 0; for (int i = 0; i < np; i++) { proc_refresh(&ps[i], ph == 1); r += ps[i].resident; }
                if (half) { resSum += r / 256.0; resN++; }
                if (t - lastRep >= 15000) { long long ev = 0, rf = 0; tr = 0; for (int i = 0; i < np; i++) { ev += ps[i].evicted; rf += ps[i].refaults; tr += ps[i].N; }
                    printf("REAL tick %s t=%.0fs resident_mib=%.1f tracked_mib=%.1f evicted=%lld refaults=%lld\n", phn[ph], (t - t0) / 1000, r / 256.0, tr / 256.0, ev, rf); fflush(stdout); lastRep = t; }
            }
            Sleep(50);
        }
        double cpu = 0; long long ev = 0, rf = 0, rh = 0; tr = 0;
        for (int i = 0; i < np; i++) { cpu += proc_cpu(ps[i].h); ev += ps[i].evicted; rf += ps[i].refaults; rh += ps[i].refHot; tr += ps[i].N; }
        double sec2 = sec / 2.0;
        PERFORMANCE_INFORMATION pi1 = { sizeof(pi1) }; GetPerformanceInfo(&pi1, sizeof(pi1));
        printf("REAL PHASE_END %s unix_ms=%lld\n", phn[ph], unix_ms());
        printf("REAL PHASE %s sec=%d [2nd half] resident_private_mib=%.1f of tracked %.1f | evicted_per_s=%.0f refaults_per_s=%.0f (hot %.0f) | app_cpu_pct=%.1f agent_cpu_pct=%.1f (of one core) | sys_avail_delta_mib=%lld\n",
            phn[ph], sec, resN ? resSum / resN : 0, tr / 256.0, (ev - ev2) / sec2, (rf - rf2) / sec2, (rh - rh2) / sec2,
            (cpu - cpuH) / (sec2 * 10), (proc_cpu(GetCurrentProcess()) - selfH) / (sec2 * 10),
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
    if (argc >= 5 && !strcmp(argv[1], "realapp")) { g_method = argc > 5 ? atoi(argv[5]) : 1; return run_realapp(argv[2], atoi(argv[3]), atoi(argv[4])); }
    return run_synthetic(argc > 1 ? atoi(argv[1]) : 1024, argc > 2 ? atoi(argv[2]) : 96, argc > 3 ? atoi(argv[3]) : 64, argc > 4 ? atoi(argv[4]) : 40);
}
