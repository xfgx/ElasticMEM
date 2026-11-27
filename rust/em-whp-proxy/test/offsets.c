// Проверка смещений WHV_RUN_VP_EXIT_CONTEXT, которые прокси читает напрямую (по заголовкам Windows SDK/mingw).
#include <windows.h>
#include <winhvplatformdefs.h>
#include <stdio.h>
#include <stddef.h>
int main(void) {
  WHV_MEMORY_ACCESS_INFO ai;
  ai.AsUINT32 = 0;
  ai.GpaUnmapped = 1;
  int ok = offsetof(WHV_RUN_VP_EXIT_CONTEXT, ExitReason) == 0 && offsetof(WHV_RUN_VP_EXIT_CONTEXT, MemoryAccess.AccessInfo) == 68 &&
           offsetof(WHV_RUN_VP_EXIT_CONTEXT, MemoryAccess.Gpa) == 72 && WHvRunVpExitReasonMemoryAccess == 1 && ai.AsUINT32 == 4 &&
           WHvMapGpaRangeFlagWrite == 2;
  printf("OFFSETS size=%zu memaccess=%zu accessinfo=%zu gpa=%zu reason_memaccess=%d gpaunmapped_bit=%u flag_write=%d verdict=%s\n",
         sizeof(WHV_RUN_VP_EXIT_CONTEXT), offsetof(WHV_RUN_VP_EXIT_CONTEXT, MemoryAccess), offsetof(WHV_RUN_VP_EXIT_CONTEXT, MemoryAccess.AccessInfo),
         offsetof(WHV_RUN_VP_EXIT_CONTEXT, MemoryAccess.Gpa), (int)WHvRunVpExitReasonMemoryAccess, ai.AsUINT32, (int)WHvMapGpaRangeFlagWrite, ok ? "PASS" : "FAIL");
  return ok ? 0 : 1;
}
