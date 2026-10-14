#ifndef ELASTICMEM_IOCTL_H
#define ELASTICMEM_IOCTL_H
#include <stdint.h>
#define EM_ABI_VERSION 1u
#define EM_PAGE_BYTES 4096u
#define EM_MAX_PAGES 4096u
#define EM_MAX_STORE_PAGES 1048576u
#define EM_PAGER_MAX_SESSION_QUOTA (32ull * 1024 * 1024)
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
/* Additive pager protocol v1; legacy ABI1 stays available. */
#define IOCTL_EM_PUT_COMPRESSED EM_IOCTL(0x806u)
#define IOCTL_EM_GET_VERSIONED EM_IOCTL(0x807u)
#define IOCTL_EM_DELETE_VERSIONED EM_IOCTL(0x808u)
#define EM_PAGER_VERSION 1u
#define IOCTL_EM_PAGER_CAPABILITIES EM_IOCTL(0x809u)
#define IOCTL_EM_CONFIG_STORE EM_IOCTL(0x80au)
#define EM_PAGER_FEATURES 7u /* compressed-only, generation, empty-store resize */

/* Natural 8-byte alignment on x64. Headers contain only uint32_t. */
typedef struct EM_HEADER { uint32_t version, key, flags, reserved; } EM_HEADER;
typedef struct EM_VERSIONED_REQUEST { EM_HEADER header; uint64_t generation; } EM_VERSIONED_REQUEST;
typedef struct EM_STORE_CONFIG { EM_HEADER header; uint32_t capacity, reserved; uint64_t quota_bytes; } EM_STORE_CONFIG;
typedef struct EM_PAGER_CAPABILITIES {
    uint32_t version, features, page_bytes, default_capacity, max_capacity, max_sessions, reserved0, reserved1;
    uint64_t max_session_quota_bytes, global_limit_bytes;
} EM_PAGER_CAPABILITIES;
typedef struct EM_PAGER_RECEIPT {
    uint32_t version, key; uint64_t generation;
    uint32_t payload_bytes, allocation_bytes, crc32, reserved;
} EM_PAGER_RECEIPT;
typedef struct EM_PUT_REQUEST { EM_HEADER header; uint8_t page[EM_PAGE_BYTES]; } EM_PUT_REQUEST;
typedef struct EM_STATS {
    uint32_t version, mode, pages, max_pages;
    uint64_t logical_bytes, payload_bytes, session_allocation_bytes, session_quota_bytes;
    uint64_t global_allocation_bytes, global_limit_bytes;
    uint64_t puts, gets, deletes, compressed_pages, raw_pages;
} EM_STATS;
#endif
