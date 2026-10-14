#include "em_store.h"
#include <string.h>
#define EM_TABLE_BYTES ((size_t)EM_MAX_PAGES * sizeof(EM_NODE *))
static size_t em_node_bytes(const EM_NODE *p) { return offsetof(EM_NODE,data) + p->size; }
void em_wipe(void *p, size_t n) { volatile uint8_t *b=(volatile uint8_t *)p; while(n--) *b++=0; }
uint32_t em_crc(const uint8_t *p, size_t n) {
    uint32_t c=0xffffffffu; size_t i; unsigned bit;
    for(i=0;i<n;i++){c^=p[i];for(bit=0;bit<8;bit++)c=(c>>1)^(0xedb88320u & (0u-(c&1u)));}
    return ~c;
}
EM_RESULT em_init(EM_STORE *s, EM_OPS ops, uint64_t quota) {
    if(!s || !ops.alloc || !ops.free || !ops.encode || !ops.decode || quota < EM_TABLE_BYTES) return EM_INVALID;
    memset(s,0,sizeof(*s)); s->ops=ops; s->quota=quota; s->mode=EM_MODE_XPRESS;
    s->entries=(EM_NODE **)ops.alloc(ops.context,EM_TABLE_BYTES);
    if(!s->entries)return EM_NO_MEMORY;
    memset(s->entries,0,EM_TABLE_BYTES);s->allocation=EM_TABLE_BYTES;return EM_OK;
}
static EM_RESULT em_put_internal(EM_STORE *s,uint32_t key,const uint8_t *page,size_t len,uint8_t *scratch,int compressed_only,EM_PAGER_RECEIPT *receipt) {
    EM_NODE *old,*node;uint32_t size=EM_PAGE_BYTES,raw=1;size_t bytes,oldBytes;
    if(!s || !s->entries || key>=EM_MAX_PAGES || !page || !scratch || len!=EM_PAGE_BYTES || page==scratch)return EM_INVALID;
    if(s->next_generation==UINT64_MAX)return EM_QUOTA; /* never wrap / re-use stale tokens */
    old=s->entries[key];oldBytes=old?em_node_bytes(old):0;
    if(s->mode==EM_MODE_XPRESS && s->ops.encode(s->ops.context,page,scratch,&size) && size>0 && size<EM_PAGE_BYTES)raw=0;
    if(raw)size=EM_PAGE_BYTES;
    bytes=offsetof(EM_NODE,data)+size;
    if(compressed_only && (raw || bytes>=EM_PAGE_BYTES)){em_wipe(scratch,EM_PAGE_BYTES);return EM_NOT_BENEFICIAL;}
    /* Final session quota plus transient device allocation quota; old value remains on failure. */
    if(s->allocation-oldBytes > s->quota || bytes > s->quota-(s->allocation-oldBytes)) {em_wipe(scratch,EM_PAGE_BYTES);return EM_QUOTA;}
    node=(EM_NODE *)s->ops.alloc(s->ops.context,bytes);
    if(!node){em_wipe(scratch,EM_PAGE_BYTES);return EM_NO_MEMORY;}
    node->generation=++s->next_generation;node->size=size;node->crc=em_crc(page,EM_PAGE_BYTES);node->raw=raw;node->reserved=0;
    memcpy(node->data,raw?page:scratch,size);em_wipe(scratch,EM_PAGE_BYTES);
    s->entries[key]=node;s->allocation+=bytes;s->payload+=size;s->puts++;
    if(raw)s->raw_pages++;else s->compressed_pages++;
    if(old){s->allocation-=oldBytes;s->payload-=old->size;if(old->raw)s->raw_pages--;else s->compressed_pages--;em_wipe(old,oldBytes);s->ops.free(s->ops.context,old,oldBytes);}
    else s->pages++;
    if(receipt){memset(receipt,0,sizeof(*receipt));receipt->version=EM_PAGER_VERSION;receipt->key=key;receipt->generation=node->generation;receipt->payload_bytes=size;receipt->allocation_bytes=(uint32_t)bytes;receipt->crc32=node->crc;}
    return EM_OK;
}
EM_RESULT em_put(EM_STORE *s,uint32_t key,const uint8_t *page,size_t len,uint8_t *scratch) {
    return em_put_internal(s,key,page,len,scratch,0,NULL);
}
EM_RESULT em_put_compressed(EM_STORE *s,uint32_t key,const uint8_t *page,size_t len,uint8_t *scratch,EM_PAGER_RECEIPT *receipt) {
    if(!receipt)return EM_INVALID;
    memset(receipt,0,sizeof(*receipt));
    return em_put_internal(s,key,page,len,scratch,1,receipt);
}
EM_RESULT em_get(EM_STORE *s,uint32_t key,uint8_t *page,size_t len) {
    EM_NODE *node;uint32_t restored=0;
    if(!s || !s->entries || !page || len!=EM_PAGE_BYTES || key>=EM_MAX_PAGES)return EM_INVALID;
    memset(page,0,EM_PAGE_BYTES);node=s->entries[key];if(!node)return EM_MISSING;
    if(node->raw){if(node->size!=EM_PAGE_BYTES)return EM_CORRUPT;memcpy(page,node->data,EM_PAGE_BYTES);}
    else if(node->size==0 || node->size>=EM_PAGE_BYTES || !s->ops.decode(s->ops.context,node->data,node->size,page,&restored) || restored!=EM_PAGE_BYTES){em_wipe(page,EM_PAGE_BYTES);return EM_CORRUPT;}
    if(em_crc(page,EM_PAGE_BYTES)!=node->crc){em_wipe(page,EM_PAGE_BYTES);return EM_CORRUPT;}
    s->gets++;return EM_OK;
}
EM_RESULT em_delete(EM_STORE *s,uint32_t key) {
    EM_NODE *n;size_t bytes;
    if(!s || !s->entries || key>=EM_MAX_PAGES)return EM_INVALID;
    n=s->entries[key];if(!n)return EM_MISSING;
    bytes=em_node_bytes(n);s->entries[key]=NULL;s->pages--;s->allocation-=bytes;s->payload-=n->size;s->deletes++;
    if(n->raw)s->raw_pages--;else s->compressed_pages--;
    em_wipe(n,bytes);s->ops.free(s->ops.context,n,bytes);return EM_OK;
}
static EM_RESULT em_version(EM_STORE *s,uint32_t key,uint64_t generation) {
    if(!s || !s->entries || key>=EM_MAX_PAGES || !generation)return EM_INVALID;
    if(!s->entries[key])return EM_MISSING;
    return s->entries[key]->generation==generation?EM_OK:EM_STALE;
}
EM_RESULT em_get_versioned(EM_STORE *s,uint32_t key,uint64_t generation,uint8_t *page,size_t len) {
    EM_RESULT result;
    if(!page || len!=EM_PAGE_BYTES)return EM_INVALID;
    em_wipe(page,len);result=em_version(s,key,generation);
    return result==EM_OK?em_get(s,key,page,len):result;
}
EM_RESULT em_delete_versioned(EM_STORE *s,uint32_t key,uint64_t generation) {
    EM_RESULT result=em_version(s,key,generation);
    return result==EM_OK?em_delete(s,key):result;
}
void em_reset(EM_STORE *s) {uint32_t i;if(!s || !s->entries)return;for(i=0;i<EM_MAX_PAGES;i++)if(s->entries[i]) (void)em_delete(s,i);}
void em_close(EM_STORE *s) {
    if(!s)return;
    if(s->entries){em_reset(s);em_wipe(s->entries,EM_TABLE_BYTES);s->ops.free(s->ops.context,s->entries,EM_TABLE_BYTES);}
    em_wipe(s,sizeof(*s));
}
