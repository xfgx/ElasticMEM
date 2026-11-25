// Имитация QEMU для проверки прокси WinHvPlatform.dll: грузит WHP по имени (как QEMU), отображает RAM,
// крутит vCPU через WHvRunVirtualProcessor и параллельно пишет в память «устройством» (как DMA QEMU).
// harness.exe [ram_mib=256] [secs=30]. Настоящую DLL прокси берёт из EM_WHP_REAL (здесь — fakewhp.dll).
#include <windows.h>
#include <psapi.h>
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#define PG 4096
typedef HRESULT (WINAPI *CreateP)(void **);
typedef HRESULT (WINAPI *SetupP)(void *);
typedef HRESULT (WINAPI *MapP)(void *, void *, uint64_t, uint64_t, int);
typedef HRESULT (WINAPI *UnmapP)(void *, uint64_t, uint64_t);
typedef HRESULT (WINAPI *RunP)(void *, UINT32, void *, UINT32);
static uint8_t *ram; static uint64_t pages; static uint64_t *dev_sh; static volatile LONG stop;
static DWORD WINAPI device(void *a) {
  uint64_t r = 0x1234567;
  while (!stop) { r ^= r << 13; r ^= r >> 7; r ^= r << 17; uint64_t i = r % pages; uint64_t *w = (uint64_t *)(ram + i * PG + 64); *w = dev_sh[i] = *w + 7; Sleep(10); }
  return 0;
}
static double priv_mib(void) { PROCESS_MEMORY_COUNTERS_EX c = { sizeof c }; GetProcessMemoryInfo(GetCurrentProcess(), (PROCESS_MEMORY_COUNTERS *)&c, sizeof c); return c.PrivateUsage / 1048576.0; }
int main(int argc, char **argv) {
  int mib = argc > 1 ? atoi(argv[1]) : 256, secs = argc > 2 ? atoi(argv[2]) : 30;
  HMODULE h = LoadLibraryA("WinHvPlatform.dll");
  if (!h) { printf("load failed %lu\n", GetLastError()); return 2; }
  CreateP cp = (CreateP)GetProcAddress(h, "WHvCreatePartition"); SetupP sp = (SetupP)GetProcAddress(h, "WHvSetupPartition");
  MapP mp = (MapP)GetProcAddress(h, "WHvMapGpaRange"); UnmapP up = (UnmapP)GetProcAddress(h, "WHvUnmapGpaRange"); RunP rp = (RunP)GetProcAddress(h, "WHvRunVirtualProcessor");
  if (!cp || !sp || !mp || !up || !rp || !GetProcAddress(h, "WHvGetVirtualProcessorRegisters")) { printf("missing exports\n"); return 2; }
  pages = (uint64_t)mib * 256;
  ram = VirtualAlloc(NULL, pages * PG, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
  dev_sh = calloc(pages, 8);
  uint64_t r = 0x9E3779B97F4A7C15ULL;
  for (uint64_t i = 0; i < pages; i++) {
    r ^= r << 13; r ^= r >> 7; r ^= r << 17;
    int kind = r % 10;
    uint64_t *w = (uint64_t *)(ram + i * PG);
    if (kind < 3) continue;                                 // свободная (нулевая) память гостя
    if (kind == 3 && i > 0) { memcpy(ram + i * PG, ram + (r % i) * PG, PG); continue; } // копия
    for (int k = 0; k < PG / 8; k++) { r ^= r << 13; r ^= r >> 7; r ^= r << 17; w[k] = (k % 4 == 0) ? (0x7ff610000000ULL + (r & 0xffff) * 16) : (k % 4 == 1 ? (r & 0x3ff) : 0); }
  }
  for (uint64_t i = 0; i < pages; i++) dev_sh[i] = *(uint64_t *)(ram + i * PG + 64);
  uint8_t *init = malloc(pages * PG); memcpy(init, ram, pages * PG);
  double p0 = priv_mib();
  void *part; cp(&part); sp(part);
  uint64_t gpa0 = 0x100000000ULL;
  if (mp(part, ram, gpa0, pages * PG, 7) < 0) { printf("map failed\n"); return 2; }
  HANDLE t = CreateThread(NULL, 0, device, NULL, 0, NULL);
  uint8_t ctx[224];
  DWORD end = GetTickCount() + secs * 1000;
  uint64_t runs = 0, mem_exits_seen = 0;
  double minp = p0;
  while (GetTickCount() < end) {
    if (rp(part, 0, ctx, sizeof ctx) < 0) { printf("run failed\n"); return 2; }
    if (*(UINT32 *)ctx == 1) mem_exits_seen++; // QEMU увидел бы MMIO — для RAM пейджера такого быть не должно
    runs++;
    if (runs % 64 == 0) { Sleep(1); double p = priv_mib(); if (p < minp) minp = p; }
  }
  stop = 1; WaitForSingleObject(t, INFINITE);
  double p1 = priv_mib();
  if (up(part, gpa0, pages * PG) < 0) { printf("unmap failed\n"); return 2; }
  typedef int64_t (*VerifyP)(void); typedef void (*StatsP)(int64_t *);
  HMODULE f = GetModuleHandleA("fakewhp.dll");
  int64_t bad_fake = f ? ((VerifyP)GetProcAddress(f, "fake_verify"))() : -1, st[4] = { 0 };
  if (f) ((StatsP)GetProcAddress(f, "fake_stats"))(st);
  uint64_t bad = 0;
  for (uint64_t i = 0; i < pages; i++) {
    uint8_t *a = ram + i * PG, *b = init + i * PG;
    if (*(uint64_t *)(a + 64) != dev_sh[i]) { bad++; continue; }
    if (memcmp(a, b, 64) || memcmp(a + 72, b + 72, 256 - 72) || memcmp(a + 264, b + 264, PG - 264)) bad++;
  }
  printf("HARNESS ram_mib=%d secs=%d runs=%llu mmio_exits_leaked_to_qemu=%llu guest_accesses=%lld guest_exits=%lld guest_saw_decommitted=%lld guest_wrong_bytes=%lld\n", mib, secs, (unsigned long long)runs, (unsigned long long)mem_exits_seen, st[0], st[1], st[2], st[3]);
  printf("PROC private_mib start=%.1f min=%.1f end=%.1f\n", p0, minp, p1);
  int ok = bad == 0 && bad_fake == 0 && st[2] == 0 && st[3] == 0 && mem_exits_seen == 0 && f != NULL;
  printf("LOSSLESS host_side_bad_pages=%llu guest_side_bad_pages=%lld verdict=%s\n", (unsigned long long)bad, bad_fake, ok ? "PASS" : "FAIL");
  return ok ? 0 : 1;
}
