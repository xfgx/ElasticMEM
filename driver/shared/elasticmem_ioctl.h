#ifndef ELASTICMEM_IOCTL_H
#define ELASTICMEM_IOCTL_H
#include <stdint.h>
#define EM_ABI_VERSION 1u
#define EM_PAGE_BYTES 4096u
#define EM_MAX_PAGES 4096u
#define EM_MODE_RAW 0u
#define EM_MODE_XPRESS 1u
/* Private device type, METHOD_BUFFERED, READ|WRITE access. No user pointers. */
#define EM_DEVICE_TYPE 0x8000u
#define EM_IOCTL(n) ((uint32_t)((EM_DEVICE_TYPE << 16) | (3u << 14) | ((n) << 2)))
#define IOCTL_EM_STATS EM_IOCTL(0x800u)
#define IOCTL_EM_PUT EM_IOCTL(0x801u)
#define IOCTL_EM_GET EM_IOCTL(0x802u)
#define IOCTL_EM_DELETE EM_IOCTL(0x803u)
#define IOCTL_EM_RESET EM_IOCTL(0x804u)
#define IOCTL_EM_CONFIG EM_IOCTL(0x805u)
/* Natural 8-byte alignment on x64. Headers contain only uint32_t. */
typedef struct EM_HEADER { uint32_t version, key, flags, reserved; } EM_HEADER;
typedef struct EM_PUT_REQUEST { EM_HEADER header; uint8_t page[EM_PAGE_BYTES]; } EM_PUT_REQUEST;
typedef struct EM_STATS {
    uint32_t version, mode, pages, max_pages;
    uint64_t logical_bytes, payload_bytes, session_allocation_bytes, session_quota_bytes;
    uint64_t global_allocation_bytes, global_limit_bytes;
    uint64_t puts, gets, deletes, compressed_pages, raw_pages;
} EM_STATS;
#endif
