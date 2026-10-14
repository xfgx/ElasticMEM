#include "../core/em_store.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
typedef struct ALLOCATOR { size_t used, limit; int fail; } ALLOCATOR;
static unsigned checks=0;
#define CHECK(x) do { checks++; if(!(x)){fprintf(stderr,"FAIL line %d: %s\n",__LINE__,#x);exit(1);} }while(0)
static void *alloc(void *v,size_t n){ALLOCATOR *a=v;void *p;if(a->fail || n>a->limit-a->used)return NULL;p=malloc(n);if(p)a->used+=n;return p;}
static void release(void *v,void *p,size_t n){ALLOCATOR *a=v;size_t i;for(i=0;i<n;i++)CHECK(((uint8_t *)p)[i]==0);CHECK(a->used>=n);a->used-=n;free(p);}
/* Test codec deliberately NOT XPRESS: independently tests store integration and fallback. */
static int encode(void *v,const uint8_t *p,uint8_t *o,uint32_t *n){size_t i; (void)v;for(i=1;i<4096;i++)if(p[i]!=p[0])return 0;o[0]=p[0];*n=1;return 1;}
static int decode(void *v,const uint8_t *p,uint32_t n,uint8_t *o,uint32_t *restored){(void)v;if(n!=1)return 0;memset(o,p[0],4096);*restored=4096;return 1;}
static int encode_unprofitable(void *v,const uint8_t *p,uint8_t *o,uint32_t *n){(void)v;(void)p;memset(o,0,4090);*n=4090;return 1;}
static uint32_t seed=24019;
static uint32_t rng(void){seed^=seed<<13;seed^=seed>>17;seed^=seed<<5;return seed;}
static void pager_tests(void){
    ALLOCATOR a={0,2*1024*1024,0};EM_OPS ops={&a,alloc,release,encode,decode};EM_STORE s,other;
    EM_PAGER_RECEIPT first,second,rejected;uint8_t in[4096],out[4096],scratch[4096];uint64_t used,generation;unsigned i;
    CHECK(sizeof(EM_VERSIONED_REQUEST)==24 && sizeof(EM_PAGER_RECEIPT)==32 && offsetof(EM_NODE,data)==24);
    CHECK(em_init(&s,ops,2*1024*1024)==EM_OK);CHECK(em_init(&other,ops,2*1024*1024)==EM_OK);
    memset(in,7,sizeof(in));CHECK(em_put_compressed(&s,0,in,4096,scratch,&first)==EM_OK);
    CHECK(first.version==1 && first.key==0 && first.generation!=0 && first.payload_bytes==1 && first.allocation_bytes==25 && first.crc32==em_crc(in,4096));
    CHECK(em_get_versioned(&s,0,first.generation,out,4096)==EM_OK && memcmp(in,out,4096)==0);
    CHECK(em_get_versioned(&other,0,first.generation,out,4096)==EM_MISSING); /* no cross-session data */
    memset(in,8,sizeof(in));CHECK(em_put_compressed(&s,0,in,4096,scratch,&second)==EM_OK && second.generation>first.generation);
    memset(out,99,sizeof(out));CHECK(em_get_versioned(&s,0,first.generation,out,4096)==EM_STALE);for(i=0;i<4096;i++)CHECK(out[i]==0);
    CHECK(em_delete_versioned(&s,0,first.generation)==EM_STALE);CHECK(em_get_versioned(&s,0,second.generation,out,4096)==EM_OK && out[0]==8);
    used=s.allocation;generation=s.next_generation;for(i=0;i<4096;i++)in[i]=(uint8_t)i;
    CHECK(em_put_compressed(&s,0,in,4096,scratch,&rejected)==EM_NOT_BENEFICIAL);CHECK(rejected.generation==0 && s.allocation==used && s.next_generation==generation);
    CHECK(em_get_versioned(&s,0,second.generation,out,4096)==EM_OK && out[0]==8);
    memset(in,9,4096);a.fail=1;CHECK(em_put_compressed(&s,0,in,4096,scratch,&rejected)==EM_NO_MEMORY);a.fail=0;
    CHECK(s.next_generation==generation && em_get_versioned(&s,0,second.generation,out,4096)==EM_OK && out[0]==8);
    s.quota=s.allocation;CHECK(em_put_compressed(&s,1,in,4096,scratch,&rejected)==EM_QUOTA);s.quota=2*1024*1024;
    CHECK(em_get_versioned(&s,0,0,out,4096)==EM_INVALID);CHECK(em_delete_versioned(&s,EM_MAX_PAGES,1)==EM_INVALID);
    CHECK(em_put(&s,0,in,4096,scratch)==EM_OK);CHECK(em_delete_versioned(&s,0,second.generation)==EM_STALE); /* legacy write invalidates receipt */
    generation=s.next_generation;em_reset(&s);CHECK(em_put_compressed(&s,0,in,4096,scratch,&second)==EM_OK && second.generation>generation);
    CHECK(em_delete_versioned(&s,0,second.generation)==EM_OK && em_delete_versioned(&s,0,second.generation)==EM_MISSING);
    s.ops.encode=encode_unprofitable;CHECK(em_put_compressed(&s,0,in,4096,scratch,&rejected)==EM_NOT_BENEFICIAL && s.pages==0);s.ops.encode=encode;
    s.mode=EM_MODE_RAW;CHECK(em_put_compressed(&s,0,in,4096,scratch,&rejected)==EM_NOT_BENEFICIAL && s.pages==0);
    s.mode=EM_MODE_XPRESS;s.next_generation=UINT64_MAX;CHECK(em_put_compressed(&s,0,in,4096,scratch,&rejected)==EM_QUOTA && s.pages==0);
    em_close(&s);em_close(&other);CHECK(a.used==0);
}
static void resize_tests(void){
    ALLOCATOR a={0,2*1024*1024,0};EM_OPS ops={&a,alloc,release,encode,decode};EM_STORE s;
    EM_PAGER_RECEIPT r,old;uint8_t page[4096],out[4096],scratch[4096];uint64_t allocation,quota,used;EM_NODE **table;
    CHECK(sizeof(EM_STORE_CONFIG)==32 && sizeof(EM_PAGER_CAPABILITIES)==48);
    CHECK(em_init(&s,ops,1024*1024)==EM_OK && s.capacity==4096);
    CHECK(em_resize_empty(&s,8192,1024*1024)==EM_OK && s.capacity==8192 && s.allocation==8192*sizeof(void *));
    memset(page,3,4096);CHECK(em_put_compressed(&s,8191,page,4096,scratch,&old)==EM_OK);
    CHECK(em_get_versioned(&s,8191,old.generation,out,4096)==EM_OK && memcmp(page,out,4096)==0);
    CHECK(em_resize_empty(&s,4096,1024*1024)==EM_INVALID);CHECK(em_get_versioned(&s,8191,old.generation,out,4096)==EM_OK);
    CHECK(em_put_compressed(&s,8192,page,4096,scratch,&r)==EM_INVALID);
    em_reset(&s);table=s.entries;allocation=s.allocation;quota=s.quota;used=a.used;a.fail=1;
    CHECK(em_resize_empty(&s,16384,1024*1024)==EM_NO_MEMORY);a.fail=0;
    CHECK(s.entries==table && s.capacity==8192 && s.allocation==allocation && s.quota==quota && a.used==used);
    CHECK(em_resize_empty(&s,0,1024*1024)==EM_INVALID);CHECK(em_resize_empty(&s,EM_MAX_STORE_PAGES+1,1024*1024)==EM_INVALID);
    CHECK(em_resize_empty(&s,8192,1)==EM_INVALID);CHECK(em_resize_empty(&s,8192,EM_PAGER_MAX_SESSION_QUOTA+1)==EM_INVALID);
    CHECK(em_resize_empty(&s,16384,1024*1024)==EM_OK);CHECK(em_put_compressed(&s,8191,page,4096,scratch,&r)==EM_OK && r.generation>old.generation);
    CHECK(em_get_versioned(&s,8191,old.generation,out,4096)==EM_STALE);
    em_reset(&s);CHECK(em_resize_empty(&s,64,1024*1024)==EM_OK && s.allocation==64*sizeof(void *));
    CHECK(em_get(&s,64,out,4096)==EM_INVALID);CHECK(em_resize_empty(&s,64,2048)==EM_OK && s.quota==2048);
    /* Same-capacity quota change performs no unnecessary allocation. */
    a.fail=1;CHECK(em_resize_empty(&s,64,4096)==EM_OK);a.fail=0;
    CHECK(em_resize_empty(&s,EM_MAX_STORE_PAGES,EM_PAGER_MAX_SESSION_QUOTA)==EM_NO_MEMORY && s.capacity==64);
    em_close(&s);CHECK(a.used==0);
}
int main(void){
    ALLOCATOR a={0,2*1024*1024,0};EM_OPS ops={&a,alloc,release,encode,decode};EM_STORE s,other;
    uint8_t in[4096],out[4096],scratch[4096],expected[32][4096];int present[32]={0};unsigned i,j,k;
    CHECK(sizeof(EM_HEADER)==16 && sizeof(EM_PUT_REQUEST)==4112 && sizeof(EM_STATS)==104);
    CHECK(em_crc((const uint8_t *)"123456789",9)==0xcbf43926u);
    CHECK(em_init(&s,ops,2*1024*1024)==EM_OK);CHECK(em_init(&other,ops,2*1024*1024)==EM_OK);
    for(i=0;i<3000;i++){
        k=rng()%32;
        if(rng()%4==0){EM_RESULT r=em_delete(&s,k);CHECK(r==(present[k]?EM_OK:EM_MISSING));present[k]=0;}
        else {if(rng()%2)memset(in,(int)(rng()%256),4096);else for(j=0;j<4096;j++)in[j]=(uint8_t)rng();
            CHECK(em_put(&s,k,in,4096,scratch)==EM_OK);memcpy(expected[k],in,4096);present[k]=1;in[0]^=255;
            CHECK(em_get(&s,k,out,4096)==EM_OK);CHECK(memcmp(out,expected[k],4096)==0);out[0]^=255;
            CHECK(em_get(&s,k,out,4096)==EM_OK);CHECK(memcmp(out,expected[k],4096)==0);
        }
        CHECK(s.raw_pages+s.compressed_pages==s.pages);
        CHECK(em_get(&other,k,out,4096)==EM_MISSING); /* handle isolation */
    }
    for(k=0;k<32;k++){CHECK(em_get(&s,k,out,4096)==(present[k]?EM_OK:EM_MISSING));if(present[k])CHECK(memcmp(out,expected[k],4096)==0);}
    CHECK(em_put(&s,EM_MAX_PAGES,in,4096,scratch)==EM_INVALID);CHECK(em_put(&s,0,in,4095,scratch)==EM_INVALID);
    CHECK(em_get(&s,0,out,4097)==EM_INVALID);CHECK(em_put(&s,0,in,4096,in)==EM_INVALID);
    memset(in,7,4096);CHECK(em_put(&s,0,in,4096,scratch)==EM_OK);
    a.fail=1;memset(in,8,4096);CHECK(em_put(&s,0,in,4096,scratch)==EM_NO_MEMORY);a.fail=0;
    CHECK(em_get(&s,0,out,4096)==EM_OK && out[0]==7); /* atomic overwrite on failure */
    s.entries[0]->data[0]^=1;memset(out,99,4096);CHECK(em_get(&s,0,out,4096)==EM_CORRUPT);for(j=0;j<4096;j++)CHECK(out[j]==0);
    em_reset(&s);CHECK(s.pages==0 && s.payload==0 && s.allocation==(uint64_t)EM_MAX_PAGES*sizeof(void *));
    s.mode=EM_MODE_RAW;s.quota=s.allocation+offsetof(EM_NODE,data)+4096;
    CHECK(em_put(&s,0,in,4096,scratch)==EM_OK);CHECK(em_put(&s,1,in,4096,scratch)==EM_QUOTA);
    CHECK(em_put(&s,0,in,4096,scratch)==EM_OK); /* replacement at final quota */
    a.limit=a.used;CHECK(em_put(&s,0,in,4096,scratch)==EM_NO_MEMORY);a.limit=2*1024*1024;
    CHECK(em_get(&s,0,out,4096)==EM_OK && out[0]==8);
    em_close(&s);em_close(&other);CHECK(a.used==0);CHECK(em_get(&s,0,out,4096)==EM_INVALID);
    a.fail=1;CHECK(em_init(&s,ops,2*1024*1024)==EM_NO_MEMORY);em_close(&s);CHECK(a.used==0);
    pager_tests();resize_tests();
    printf("PASS %u checks; portable store only; XPRESS/KMDF/Windows NOT executed\n",checks);return 0;
}
