// Build in an x64 VS Developer Prompt using the Windows SDK; no execution of a guest.
#include <windows.h>
#include <WinHvPlatform.h>
#include <cstddef>
#include <cstdio>
static_assert(sizeof(void*) == 8, "x64 tool required");
static_assert(sizeof(WHV_REGISTER_VALUE) == 16, "register ABI");
static_assert(sizeof(WHV_X64_SEGMENT_REGISTER) == 16, "segment ABI");
static_assert(offsetof(WHV_X64_SEGMENT_REGISTER, Selector) == 12, "selector ABI");
static_assert(sizeof(WHV_RUN_VP_EXIT_CONTEXT) == 224, "exit ABI");
static_assert(offsetof(WHV_RUN_VP_EXIT_CONTEXT, MemoryAccess) == 48, "exit union ABI");
static_assert(offsetof(WHV_MEMORY_ACCESS_CONTEXT, AccessInfo) == 20, "access bits ABI");
static_assert(offsetof(WHV_MEMORY_ACCESS_CONTEXT, Gpa) == 24, "GPA ABI");
static_assert(offsetof(WHV_RUN_VP_EXIT_CONTEXT, VpContext) + offsetof(WHV_VP_EXIT_CONTEXT, Rip) == 32, "RIP ABI");
static_assert(WHvPartitionPropertyCodeProcessorCount == 0x1fff, "property ABI");
static_assert(WHvRegisterInternalActivityState == 0x80000005, "activity ABI");
static_assert(WHvRunVpExitReasonMemoryAccess == 1 && WHvRunVpExitReasonX64Halt == 8, "exit enums");
int main() { std::puts("WHP SDK ABI PASS; NOT guest boot/paging test"); }
