#ifndef EM_STORE_H
#define EM_STORE_H
#include <stddef.h>
#include "../shared/elasticmem_ioctl.h"
typedef enum EM_RESULT { EM_OK, EM_INVALID, EM_MISSING, EM_QUOTA, EM_NO_MEMORY, EM_CORRUPT, EM_STALE, EM_NOT_BENEFICIAL } EM_RESULT;
typedef void *(*EM_ALLOC)(void *, size_t);
typedef void (*EM_FREE)(void *, void *, size_t);
/* encode returns nonzero only for a usable compressed representation; decode must return exact size. */
typedef int (*EM_ENCODE)(void *, const uint8_t *, uint8_t *, uint32_t *);
typedef int (*EM_DECODE)(void *, const uint8_t *, uint32_t, uint8_t *, uint32_t *);
typedef struct EM_OPS { void *context; EM_ALLOC alloc; EM_FREE free; EM_ENCODE encode; EM_DECODE decode; } EM_OPS;
typedef struct EM_NODE { uint64_t generation; uint32_t size, crc, raw, reserved; uint8_t data[1]; } EM_NODE;
typedef struct EM_STORE {
    EM_NODE **entries;
    EM_OPS ops;
    uint64_t allocation, quota, payload, puts, gets, deletes, next_generation;
    uint32_t mode, pages, compressed_pages, raw_pages;
} EM_STORE;
/* Caller serializes ALL access, including cleanup. scratch must be a distinct 4096-byte buffer. */
EM_RESULT em_init(EM_STORE *, EM_OPS, uint64_t quota);
EM_RESULT em_put(EM_STORE *, uint32_t key, const uint8_t *, size_t, uint8_t *scratch);
EM_RESULT em_get(EM_STORE *, uint32_t key, uint8_t *, size_t);
EM_RESULT em_delete(EM_STORE *, uint32_t key);
/* Success receipt commits exact bytes under a session-local non-reused generation.
   Compressed-only rejects raw/overhead-unprofitable data without replacing old value. */
EM_RESULT em_put_compressed(EM_STORE *, uint32_t, const uint8_t *, size_t, uint8_t *, EM_PAGER_RECEIPT *);
EM_RESULT em_get_versioned(EM_STORE *, uint32_t, uint64_t, uint8_t *, size_t);
EM_RESULT em_delete_versioned(EM_STORE *, uint32_t, uint64_t);
void em_reset(EM_STORE *);
void em_close(EM_STORE *);
uint32_t em_crc(const uint8_t *, size_t);
void em_wipe(void *, size_t);
#endif
