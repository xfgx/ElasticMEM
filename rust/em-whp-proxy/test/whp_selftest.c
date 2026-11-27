// whp-selftest: проверка пейджера на НАСТОЯЩЕМ гипервизоре до запуска Windows-гостя.
// Маленькая ВМ на Windows Hypervisor Platform: 2 vCPU в 32-битном защищённом режиме гоняют счётчики по
// «окну» страниц, окно ходит по 256 МиБ памяти гостя, остальная память остывает и уходит в хранилище пейджера.
// WinHvPlatform.dll грузится по имени — рядом с этим exe лежит прокси с em-pager, поэтому exit'ы памяти,
// восстановления, prefetch и VEH работают так же, как под QEMU. В конце каждый счётчик сверяется с моделью.
// whp-selftest.exe [ram_mib=512] [secs=60] [vcpus=2]
#include <windows.h>
#include <winhvplatformdefs.h>
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

typedef HRESULT (WINAPI *GetCapP)(WHV_CAPABILITY_CODE, void *, UINT32, UINT32 *);
typedef HRESULT (WINAPI *CreatePartP)(WHV_PARTITION_HANDLE *);
typedef HRESULT (WINAPI *SetupP)(WHV_PARTITION_HANDLE);
typedef HRESULT (WINAPI *DelP)(WHV_PARTITION_HANDLE);
typedef HRESULT (WINAPI *SetPropP)(WHV_PARTITION_HANDLE, WHV_PARTITION_PROPERTY_CODE, const void *, UINT32);
typedef HRESULT (WINAPI *MapP)(WHV_PARTITION_HANDLE, void *, UINT64, UINT64, UINT32);
typedef HRESULT (WINAPI *UnmapP)(WHV_PARTITION_HANDLE, UINT64, UINT64);
typedef HRESULT (WINAPI *CreateVpP)(WHV_PARTITION_HANDLE, UINT32, UINT32);
typedef HRESULT (WINAPI *DelVpP)(WHV_PARTITION_HANDLE, UINT32);
typedef HRESULT (WINAPI *SetRegsP)(WHV_PARTITION_HANDLE, UINT32, const WHV_REGISTER_NAME *, UINT32, const WHV_REGISTER_VALUE *);
typedef HRESULT (WINAPI *RunP)(WHV_PARTITION_HANDLE, UINT32, void *, UINT32);
typedef HRESULT (WINAPI *CancelP)(WHV_PARTITION_HANDLE, UINT32, UINT32);

static GetCapP pGetCap; static CreatePartP pCreate; static SetupP pSetup; static DelP pDel; static SetPropP pSetProp;
static MapP pMap; static UnmapP pUnmap; static CreateVpP pCreateVp; static DelVpP pDelVp; static SetRegsP pSetRegs; static RunP pRun; static CancelP pCancel;

#define CODE_GPA 0x1000ULL
#define REGION_GPA 0x1000000ULL   /* 16 МиБ */
#define REGION_PAGES 65536u       /* 256 МиБ */
#define WIN_PAGES 2048u           /* окно 8 МиБ */
#define ITERS 2000u
#define MAXV 8
#define MAXSEG 400000

static const uint8_t code[] = {
  0x69, 0xF6, 0x0D, 0x66, 0x19, 0x00,       // imul esi, esi, 1664525
  0x81, 0xC6, 0x5F, 0xF3, 0x6E, 0x3C,       // add esi, 1013904223
  0x89, 0xF0,                               // mov eax, esi
  0xC1, 0xE8, 0x08,                         // shr eax, 8
  0x21, 0xC8,                               // and eax, ecx        (маска окна)
  0xC1, 0xE0, 0x0C,                         // shl eax, 12
  0x01, 0xD8,                               // add eax, ebx        (начало окна)
  0x01, 0xD0,                               // add eax, edx        (смещение vCPU)
  0xFF, 0x00,                               // inc dword [eax]
  0x4F,                                     // dec edi
  0x75, 0xE1,                               // jnz loop
  0xE6, 0xE9,                               // out 0xE9, al        -> exit к хосту
  0xEB, 0xDD,                               // jmp loop
};

static WHV_PARTITION_HANDLE part;
static uint8_t *ram;
static volatile LONG stop_flag;
static uint32_t seg_win[MAXV][MAXSEG];
static uint32_t nseg[MAXV];
static uint64_t other_exits[MAXV], bad_hr[MAXV];
static uint32_t last_other_reason[MAXV];
static DWORD t_start;

static HRESULT set_loop_regs(UINT32 vp, uint32_t win) {
  WHV_REGISTER_NAME n[5] = { WHvX64RegisterRip, WHvX64RegisterRbx, WHvX64RegisterRcx, WHvX64RegisterRdi, WHvX64RegisterRflags };
  WHV_REGISTER_VALUE v[5]; memset(v, 0, sizeof v);
  v[0].Reg64 = CODE_GPA;
  v[1].Reg64 = REGION_GPA + (uint64_t)win * WIN_PAGES * 4096;
  v[2].Reg64 = WIN_PAGES - 1;
  v[3].Reg64 = ITERS;
  v[4].Reg64 = 0x2;
  return pSetRegs(part, vp, n, 5, v);
}

static uint32_t window_now(void) { return ((GetTickCount() - t_start) / 2000) % (REGION_PAGES / WIN_PAGES); }

static DWORD WINAPI vcpu(void *arg) {
  UINT32 vp = (UINT32)(uintptr_t)arg;
  WHV_RUN_VP_EXIT_CONTEXT ctx;
  uint32_t win = window_now();
  seg_win[vp][0] = win; nseg[vp] = 1;
  set_loop_regs(vp, win);
  while (!stop_flag) {
    HRESULT hr = pRun(part, vp, &ctx, sizeof ctx);
    if (FAILED(hr)) { bad_hr[vp]++; break; }
    if (ctx.ExitReason == WHvRunVpExitReasonX64IoPortAccess) {
      if (nseg[vp] >= MAXSEG) break;
      win = window_now();
      seg_win[vp][nseg[vp]++] = win;
      set_loop_regs(vp, win);
      Sleep(1);
      continue;
    }
    if (ctx.ExitReason == WHvRunVpExitReasonCanceled) continue;
    other_exits[vp]++; last_other_reason[vp] = ctx.ExitReason;
    if (other_exits[vp] > 100) break;
  }
  // последний сегмент мог не завершиться: он не засчитан, ниже это учитывается
  return 0;
}

int main(int argc, char **argv) {
  int ram_mib = argc > 1 ? atoi(argv[1]) : 512, secs = argc > 2 ? atoi(argv[2]) : 60, nv = argc > 3 ? atoi(argv[3]) : 2;
  if (nv < 1) nv = 1;
  if (nv > MAXV) nv = MAXV;
  if (ram_mib < 320) ram_mib = 320;
  HMODULE h = LoadLibraryA("WinHvPlatform.dll");
  if (!h) { printf("SELFTEST verdict=SKIP reason=no_WinHvPlatform\n"); return 3; }
#define L(v, t, n) v = (t)GetProcAddress(h, n); if (!v) { printf("SELFTEST verdict=SKIP reason=missing_%s\n", n); return 3; }
  L(pGetCap, GetCapP, "WHvGetCapability") L(pCreate, CreatePartP, "WHvCreatePartition") L(pSetup, SetupP, "WHvSetupPartition")
  L(pDel, DelP, "WHvDeletePartition") L(pSetProp, SetPropP, "WHvSetPartitionProperty") L(pMap, MapP, "WHvMapGpaRange")
  L(pUnmap, UnmapP, "WHvUnmapGpaRange") L(pCreateVp, CreateVpP, "WHvCreateVirtualProcessor") L(pDelVp, DelVpP, "WHvDeleteVirtualProcessor")
  L(pSetRegs, SetRegsP, "WHvSetVirtualProcessorRegisters") L(pRun, RunP, "WHvRunVirtualProcessor") L(pCancel, CancelP, "WHvCancelRunVirtualProcessor")
  BOOL present = FALSE; UINT32 w = 0;
  if (FAILED(pGetCap(WHvCapabilityCodeHypervisorPresent, &present, sizeof present, &w)) || !present) { printf("SELFTEST verdict=SKIP reason=hypervisor_not_present\n"); return 3; }
  if (FAILED(pCreate(&part))) { printf("SELFTEST verdict=FAIL reason=create_partition\n"); return 1; }
  UINT32 cnt = nv;
  pSetProp(part, WHvPartitionPropertyCodeProcessorCount, &cnt, sizeof cnt);
  if (FAILED(pSetup(part))) { printf("SELFTEST verdict=FAIL reason=setup_partition\n"); return 1; }
  size_t bytes = (size_t)ram_mib << 20;
  ram = VirtualAlloc(NULL, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
  if (!ram) { printf("SELFTEST verdict=FAIL reason=alloc\n"); return 1; }
  memcpy(ram + CODE_GPA, code, sizeof code);
  if (FAILED(pMap(part, ram, 0, bytes, WHvMapGpaRangeFlagRead | WHvMapGpaRangeFlagWrite | WHvMapGpaRangeFlagExecute))) { printf("SELFTEST verdict=FAIL reason=map\n"); return 1; }
  for (int i = 0; i < nv; i++) {
    if (FAILED(pCreateVp(part, i, 0))) { printf("SELFTEST verdict=FAIL reason=create_vp\n"); return 1; }
    WHV_REGISTER_NAME n[] = { WHvX64RegisterCr0, WHvX64RegisterCs, WHvX64RegisterDs, WHvX64RegisterEs, WHvX64RegisterSs, WHvX64RegisterFs, WHvX64RegisterGs,
                              WHvX64RegisterTr, WHvX64RegisterLdtr, WHvX64RegisterGdtr, WHvX64RegisterIdtr, WHvX64RegisterRdx, WHvX64RegisterRsi };
    WHV_REGISTER_VALUE v[13]; memset(v, 0, sizeof v);
    v[0].Reg64 = 0x11; // PE | ET, без страничной адресации
    v[1].Segment.Base = 0; v[1].Segment.Limit = 0xFFFFFFFF; v[1].Segment.Selector = 0x08; v[1].Segment.Attributes = 0xC09B;
    for (int k = 2; k <= 6; k++) { v[k].Segment.Base = 0; v[k].Segment.Limit = 0xFFFFFFFF; v[k].Segment.Selector = 0x10; v[k].Segment.Attributes = 0xC093; }
    v[7].Segment.Base = 0; v[7].Segment.Limit = 0x67; v[7].Segment.Selector = 0x18; v[7].Segment.Attributes = 0x8B;
    v[8].Segment.Base = 0; v[8].Segment.Limit = 0xFFFF; v[8].Segment.Selector = 0; v[8].Segment.Attributes = 0x82;
    v[9].Table.Base = 0; v[9].Table.Limit = 0xFFFF; v[10].Table.Base = 0; v[10].Table.Limit = 0xFFFF;
    v[11].Reg64 = 4 * i;                      // смещение счётчика этого vCPU на странице
    v[12].Reg64 = 0x12345 + 0x9E3779B9u * i;  // начальное состояние ГПСЧ
    if (FAILED(pSetRegs(part, i, n, 13, v))) { printf("SELFTEST verdict=FAIL reason=set_registers\n"); return 1; }
  }
  printf("SELFTEST start ram_mib=%d secs=%d vcpus=%d window_mib=%u region_mib=%u\n", ram_mib, secs, nv, WIN_PAGES / 256, REGION_PAGES / 256);
  fflush(stdout);
  t_start = GetTickCount();
  HANDLE th[MAXV];
  for (int i = 0; i < nv; i++) th[i] = CreateThread(NULL, 0, vcpu, (void *)(uintptr_t)i, 0, NULL);
  Sleep(secs * 1000);
  stop_flag = 1;
  for (int i = 0; i < nv; i++) pCancel(part, i, 0);
  WaitForMultipleObjects(nv, th, TRUE, INFINITE);
  // модель: тот же ГПСЧ и те же окна, только завершённые сегменты (последний выполнялся, пока хост ждал: не засчитан)
  uint32_t *expect = calloc((size_t)REGION_PAGES * nv, 4);
  uint64_t segs_total = 0, other = 0, badhr = 0;
  for (int i = 0; i < nv; i++) {
    uint32_t s = 0x12345 + 0x9E3779B9u * i;
    uint32_t done = nseg[i] > 0 ? nseg[i] - 1 : 0;
    for (uint32_t g = 0; g < done; g++) {
      uint32_t base = seg_win[i][g] * WIN_PAGES;
      for (uint32_t k = 0; k < ITERS; k++) { s = s * 1664525u + 1013904223u; expect[(size_t)(base + ((s >> 8) & (WIN_PAGES - 1))) * nv + i]++; }
    }
    segs_total += done; other += other_exits[i]; badhr += bad_hr[i];
  }
  // Незавершённый последний сегмент мог успеть записать часть инкрементов: страницы его окна сверяем «не меньше».
  uint64_t mism = 0, under = 0;
  for (uint32_t p = 0; p < REGION_PAGES; p++)
    for (int i = 0; i < nv; i++) {
      uint32_t got = *(uint32_t *)(ram + REGION_GPA + (size_t)p * 4096 + 4 * i), want = expect[(size_t)p * nv + i];
      uint32_t lastwin = nseg[i] ? seg_win[i][nseg[i] - 1] : 0xFFFFFFFF;
      int in_last = (p / WIN_PAGES) == lastwin;
      if (in_last ? got < want || got > want + ITERS : got != want) { mism++; if (got < want) under++; }
    }
  int code_ok = memcmp(ram + CODE_GPA, code, sizeof code) == 0;
  pUnmap(part, 0, bytes);
  for (int i = 0; i < nv; i++) pDelVp(part, i);
  pDel(part);
  int ok = mism == 0 && other == 0 && badhr == 0 && code_ok && segs_total > 0;
  printf("SELFTEST segments=%llu other_exits=%llu last_other_reason=%u run_failures=%llu mismatched_counters=%llu lost_increments=%llu code_intact=%d verdict=%s\n",
         (unsigned long long)segs_total, (unsigned long long)other, last_other_reason[0], (unsigned long long)badhr, (unsigned long long)mism, (unsigned long long)under, code_ok, ok ? "PASS" : "FAIL");
  return ok ? 0 : 1;
}
