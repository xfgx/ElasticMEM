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
EXP WHvGetCapability(int c, void *b, UINT32 n, UINT32 *w) { if (b && n) memset(b, 0, n); if (w) *w = n; return 0; }
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
  if ((uint8_t *)src != g_hva + (gpa - g_gpa0)) return 0x80070057;
  for (uint64_t i = 0; i < size / PG; i++) if (g_map[(gpa - g_gpa0) / PG + i]) return 0x80070057; // как WHP: уже отображено
  for (uint64_t i = 0; i < size / PG; i++) InterlockedExchange(&g_map[(gpa - g_gpa0) / PG + i], 1);
  return 0;
}
EXP WHvUnmapGpaRange(void *p, uint64_t gpa, uint64_t size) {
  for (uint64_t i = 0; i < size / PG; i++) InterlockedExchange(&g_map[(gpa - g_gpa0) / PG + i], 0);
  return 0;
}
EXP WHvRunVirtualProcessor(void *p, UINT32 vp, void *ctx, UINT32 size) {
  uint8_t *c = ctx;
  memset(c, 0, size);
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
