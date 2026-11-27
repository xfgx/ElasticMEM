// Модель WinHvPlatform.dll для проверки прокси без настоящего гипервизора (CI / Wine).
// «Гость» внутри WHvRunVirtualProcessor обращается к своим страницам: неотображённая страница даёт
// memory-access exit (GpaUnmapped=1), отображённая — проверяется (закоммичена? байты верны?) и меняется.
#include <windows.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#define PG 4096
static uint8_t *g_hva; static uint64_t g_gpa0, g_pages; static volatile LONG *g_map; static uint64_t *g_sh;
static uint64_t g_hot[1 << 16]; static int g_nhot; static int64_t g_pending = -1;
static volatile LONG64 g_err_decommitted, g_err_bytes, g_exits, g_access;
static uint64_t r_ = 88172645463325252ULL; static uint64_t g_calls, g_q;
static uint64_t xr(void) { r_ ^= r_ << 13; r_ ^= r_ >> 7; r_ ^= r_ << 17; return r_; }
#define EXP __declspec(dllexport) HRESULT WINAPI
// Режим самопроверки (EM_FAKE_SELFTEST=1): модель выполняет программу whp-selftest (ГПСЧ + inc dword по окну)
// с теми же exit'ами, что дал бы настоящий гипервизор: обращение к неотображённой странице — memory-access exit.
static int selftest_mode(void) { static int m = -1; if (m < 0) { char b[8]; m = GetEnvironmentVariableA("EM_FAKE_SELFTEST", b, 8) > 0 && b[0] == '1'; } return m; }
struct vpst { uint64_t rbx, rcx, rdx, rsi, rdi, rip; uint64_t pending; int has_pending; };
static struct vpst g_vp[8];
EXP WHvGetCapability(int c, void *b, UINT32 n, UINT32 *w) { if (b && n) memset(b, 0, n); if (selftest_mode() && c == 0 && b && n >= 4) *(UINT32 *)b = 1; if (w) *w = n; return 0; }
EXP WHvSetPartitionProperty(void *p, int code, const void *b, UINT32 n) { return 0; }
EXP WHvCreateVirtualProcessor(void *p, UINT32 vp, UINT32 f) { if (vp < 8) memset(&g_vp[vp], 0, sizeof g_vp[vp]); return 0; }
EXP WHvDeleteVirtualProcessor(void *p, UINT32 vp) { return 0; }
EXP WHvSetVirtualProcessorRegisters(void *p, UINT32 vp, const UINT32 *names, UINT32 count, const uint64_t *values) {
  struct vpst *s = &g_vp[vp & 7];
  for (UINT32 i = 0; i < count; i++) {
    uint64_t v = values[i * 2]; // WHV_REGISTER_VALUE — 16 байт, Reg64 в начале
    switch (names[i]) { case 3: s->rbx = v; break; case 1: s->rcx = v; break; case 2: s->rdx = v; break; case 6: s->rsi = v; break; case 7: s->rdi = v; break; case 0x10: s->rip = v; break; }
  }
  return 0;
}
static HRESULT run_selftest(UINT32 vp, uint8_t *c) {
  struct vpst *s = &g_vp[vp & 7];
  if (s->rip != 0x1000) { *(UINT32 *)c = 4; return 0; } // не наша программа — «unrecoverable»
  while ((uint32_t)s->rdi) {
    uint64_t addr;
    if (s->has_pending) addr = s->pending;
    else {
      uint32_t st = (uint32_t)s->rsi * 1664525u + 1013904223u; s->rsi = st;
      addr = (uint32_t)((((st >> 8) & (uint32_t)s->rcx) << 12) + (uint32_t)s->rbx + (uint32_t)s->rdx);
    }
    uint64_t pg = (addr - g_gpa0) / PG;
    if (!g_map[pg]) {
      s->pending = addr; s->has_pending = 1;
      *(UINT32 *)c = 1; *(UINT32 *)(c + 68) = 4 | 1; *(uint64_t *)(c + 72) = addr;
      InterlockedIncrement64(&g_exits);
      return 0;
    }
    s->has_pending = 0;
    if ((++g_q & 63) == 0) {
      MEMORY_BASIC_INFORMATION m; VirtualQuery(g_hva + (addr - g_gpa0), &m, sizeof m);
      if (m.State != MEM_COMMIT || (m.Protect & PAGE_READWRITE) == 0) { InterlockedIncrement64(&g_err_decommitted); }
    }
    InterlockedIncrement((volatile LONG *)(g_hva + (addr - g_gpa0)));
    s->rdi = (uint32_t)s->rdi - 1;
  }
  *(UINT32 *)c = 2; // out 0xE9 -> IO-port exit
  return 0;
}
EXP WHvCreatePartition(void **p) { *p = (void *)0x1234; return 0; }
EXP WHvSetupPartition(void *p) { return 0; }
EXP WHvDeletePartition(void *p) { return 0; }
EXP WHvGetVirtualProcessorRegisters(void *p, UINT32 vp, const void *n, UINT32 c, void *v) { return 0; }
EXP WHvMapGpaRange(void *p, void *src, uint64_t gpa, uint64_t size, int flags) {
  if (!g_hva) {
    g_hva = src; g_gpa0 = gpa; g_pages = size / PG;
    g_map = calloc(g_pages, sizeof(LONG)); g_sh = calloc(g_pages, 8);
    for (uint64_t i = 0; i < g_pages; i++) g_sh[i] = *(uint64_t *)(g_hva + i * PG + 256);
    while (g_nhot < (int)(g_pages / 32) && g_nhot < (1 << 16) - 16) { uint64_t s = (xr() % (g_pages / 16)) * 16; for (int k = 0; k < 16; k++) g_hot[g_nhot++] = s + k; }
  }
  if (gpa < g_gpa0 || gpa >= g_gpa0 + g_pages * PG) return 0; // другие диапазоны (буферы ввода-вывода стенда) гость не трогает
  if ((uint8_t *)src != g_hva + (gpa - g_gpa0)) return 0x80070057;
  for (uint64_t i = 0; i < size / PG; i++) if (g_map[(gpa - g_gpa0) / PG + i]) return 0x80070057; // как WHP: уже отображено
  for (uint64_t i = 0; i < size / PG; i++) InterlockedExchange(&g_map[(gpa - g_gpa0) / PG + i], 1);
  return 0;
}
EXP WHvUnmapGpaRange(void *p, uint64_t gpa, uint64_t size) {
  if (gpa < g_gpa0 || gpa >= g_gpa0 + g_pages * PG) return 0;
  for (uint64_t i = 0; i < size / PG; i++) InterlockedExchange(&g_map[(gpa - g_gpa0) / PG + i], 0);
  return 0;
}
EXP WHvRunVirtualProcessor(void *p, UINT32 vp, void *ctx, UINT32 size) {
  uint8_t *c = ctx;
  memset(c, 0, size);
  if (selftest_mode()) return run_selftest(vp, c);
  g_calls++;
  // 32 обращения к горячему набору (прогоны по 64 КиБ) за вызов; холодная страница — раз в 64 вызова
  int n = (g_calls % 64 == 0) ? 33 : 32;
  for (int k = 0; k < n; k++) {
    uint64_t i;
    if (g_pending >= 0) { i = (uint64_t)g_pending; }
    else { i = (k < 32) ? g_hot[xr() % g_nhot] : xr() % g_pages; }
    if (!g_map[i]) {
      g_pending = (int64_t)i;
      *(UINT32 *)c = 1;                          // WHvRunVpExitReasonMemoryAccess
      *(UINT32 *)(c + 68) = 4 | 1;               // GpaUnmapped | write
      *(uint64_t *)(c + 72) = g_gpa0 + i * PG + 256;
      InterlockedIncrement64(&g_exits);
      return 0;
    }
    g_pending = -1;
    // VirtualQuery на большом регионе дорогая (ОС ищет конец региона с теми же атрибутами) и под нагрузкой
    // конкурирует за адресное пространство с VirtualProtect/VirtualFree пейджера: проверяем каждое 8-е обращение.
    if ((++g_q & 7) == 0) {
      MEMORY_BASIC_INFORMATION m;
      VirtualQuery(g_hva + i * PG, &m, sizeof m);
      if (m.State != MEM_COMMIT || (m.Protect & PAGE_READWRITE) == 0) { InterlockedIncrement64(&g_err_decommitted); continue; }
    }
    uint64_t *w = (uint64_t *)(g_hva + i * PG + 256);
    if (*w != g_sh[i]) InterlockedIncrement64(&g_err_bytes);
    *w = g_sh[i] = *w + 1;
    InterlockedIncrement64(&g_access);
  }
  *(UINT32 *)c = 8; // HLT
  return 0;
}
EXP WHvCancelRunVirtualProcessor(void *p, UINT32 vp, UINT32 f) { return 0; }
__declspec(dllexport) int64_t fake_verify(void) {
  int64_t bad = 0;
  for (uint64_t i = 0; i < g_pages; i++) if (*(uint64_t *)(g_hva + i * PG + 256) != g_sh[i]) bad++;
  return bad;
}
__declspec(dllexport) void fake_stats(int64_t *o) { o[0] = g_access; o[1] = g_exits; o[2] = g_err_decommitted; o[3] = g_err_bytes; }
