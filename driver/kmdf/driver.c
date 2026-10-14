#include <ntifs.h>
#include <wdf.h>
#include <wdmsec.h>
#include "../core/em_store.h"
C_ASSERT(sizeof(EM_HEADER)==16);
C_ASSERT(sizeof(EM_PUT_REQUEST)==4112);
C_ASSERT(sizeof(EM_STATS)==104);
C_ASSERT(sizeof(EM_VERSIONED_REQUEST)==24);
C_ASSERT(sizeof(EM_PAGER_RECEIPT)==32);
C_ASSERT(FIELD_OFFSET(EM_NODE,data)==24);
#define EM_TAG 'mMlE'
#define EM_GLOBAL_LIMIT (64ull * 1024 * 1024)
#define EM_SESSION_LIMIT (16ull * 1024 * 1024)
#define EM_MAX_SESSIONS 8u
#define EM_MAX_WORKSPACE (1024u * 1024u)
typedef struct DEVICE_CONTEXT {
    WDFWAITLOCK lock;
    PVOID workspace;
    uint8_t *scratch;
    ULONG workspaceBytes;
    uint64_t allocated;
    ULONG sessions;
} DEVICE_CONTEXT;
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT,EmDevice)
typedef struct FILE_CONTEXT { EM_STORE store; BOOLEAN active; } FILE_CONTEXT;
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(FILE_CONTEXT,EmFile)
DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_UNLOAD EmUnload;
EVT_WDF_OBJECT_CONTEXT_CLEANUP EmDeviceCleanup;
EVT_WDF_DEVICE_FILE_CREATE EmCreate;
EVT_WDF_FILE_CLEANUP EmCleanup;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL EmIoctl;
static void *EmAlloc(void *context,size_t bytes) {
    DEVICE_CONTEXT *d=(DEVICE_CONTEXT *)context;PVOID p;
    if(bytes>EM_GLOBAL_LIMIT || d->allocated>EM_GLOBAL_LIMIT-bytes)return NULL;
    p=ExAllocatePool2(POOL_FLAG_PAGED,bytes,EM_TAG);
    if(p)d->allocated+=bytes;return p;
}
static void EmFree(void *context,void *p,size_t bytes) {
    DEVICE_CONTEXT *d=(DEVICE_CONTEXT *)context;
    if(p){ExFreePoolWithTag(p,EM_TAG);NT_ASSERT(d->allocated>=bytes);d->allocated-=bytes;}
}
static int EmEncode(void *context,const uint8_t *input,uint8_t *out,uint32_t *size) {
    DEVICE_CONTEXT *d=(DEVICE_CONTEXT *)context;ULONG result=0;
    NTSTATUS status=RtlCompressBuffer(COMPRESSION_FORMAT_XPRESS|COMPRESSION_ENGINE_STANDARD,
        (PUCHAR)input,EM_PAGE_BYTES,out,EM_PAGE_BYTES,EM_PAGE_BYTES,&result,d->workspace);
    *size=result;return NT_SUCCESS(status) && result>0 && result<EM_PAGE_BYTES;
}
static int EmDecode(void *context,const uint8_t *input,uint32_t size,uint8_t *out,uint32_t *restored) {
    ULONG result=0;NTSTATUS status;UNREFERENCED_PARAMETER(context);
    status=RtlDecompressBuffer(COMPRESSION_FORMAT_XPRESS,out,EM_PAGE_BYTES,(PUCHAR)input,size,&result);
    *restored=result;return NT_SUCCESS(status);
}
static NTSTATUS EmStatus(EM_RESULT result) {
    switch(result){case EM_OK:return STATUS_SUCCESS;case EM_INVALID:return STATUS_INVALID_PARAMETER;
    case EM_MISSING:return STATUS_NOT_FOUND;case EM_QUOTA:return STATUS_QUOTA_EXCEEDED;
    case EM_NO_MEMORY:return STATUS_INSUFFICIENT_RESOURCES;case EM_STALE:return STATUS_REVISION_MISMATCH;
    case EM_NOT_BENEFICIAL:return STATUS_NOT_SUPPORTED;default:return STATUS_DATA_ERROR;}
}
VOID EmCreate(WDFDEVICE device,WDFREQUEST request,WDFFILEOBJECT file) {
    DEVICE_CONTEXT *d=EmDevice(device);FILE_CONTEXT *f=EmFile(file);NTSTATUS status;
    EM_OPS ops={d,EmAlloc,EmFree,EmEncode,EmDecode};
    status=WdfWaitLockAcquire(d->lock,NULL);
    if(NT_SUCCESS(status)){
        if(d->sessions>=EM_MAX_SESSIONS)status=STATUS_TOO_MANY_SESSIONS;
        else {status=EmStatus(em_init(&f->store,ops,EM_SESSION_LIMIT));if(NT_SUCCESS(status)){f->active=TRUE;d->sessions++;}}
        WdfWaitLockRelease(d->lock);
    }
    WdfRequestComplete(request,status);
}
VOID EmCleanup(WDFFILEOBJECT file) {
    DEVICE_CONTEXT *d=EmDevice(WdfFileObjectGetDevice(file));FILE_CONTEXT *f=EmFile(file);
    NTSTATUS status=WdfWaitLockAcquire(d->lock,NULL);NT_ASSERT(NT_SUCCESS(status));
    if(!NT_SUCCESS(status))return;
    if(f->active){em_close(&f->store);f->active=FALSE;NT_ASSERT(d->sessions>0);d->sessions--;}
    WdfWaitLockRelease(d->lock);
}
VOID EmIoctl(WDFQUEUE queue,WDFREQUEST request,size_t outputLength,size_t inputLength,ULONG code) {
    DEVICE_CONTEXT *d=EmDevice(WdfIoQueueGetDevice(queue));WDFFILEOBJECT file=WdfRequestGetFileObject(request);
    FILE_CONTEXT *f;EM_HEADER header;PVOID input=NULL,output=NULL;NTSTATUS status;ULONG_PTR information=0;
    uint64_t generation=0;EM_PAGER_RECEIPT receipt;
    BOOLEAN versioned=(code==IOCTL_EM_GET_VERSIONED || code==IOCTL_EM_DELETE_VERSIONED);
    BOOLEAN put=(code==IOCTL_EM_PUT || code==IOCTL_EM_PUT_COMPRESSED);
    size_t expected=put?sizeof(EM_PUT_REQUEST):(versioned?sizeof(EM_VERSIONED_REQUEST):sizeof(EM_HEADER));
    if(!file){WdfRequestComplete(request,STATUS_INVALID_HANDLE);return;}f=EmFile(file);
    if(code!=IOCTL_EM_STATS && code!=IOCTL_EM_PUT && code!=IOCTL_EM_GET && code!=IOCTL_EM_DELETE && code!=IOCTL_EM_RESET && code!=IOCTL_EM_CONFIG && code!=IOCTL_EM_PUT_COMPRESSED && !versioned){WdfRequestComplete(request,STATUS_INVALID_DEVICE_REQUEST);return;}
    if(inputLength!=expected){WdfRequestComplete(request,STATUS_INFO_LENGTH_MISMATCH);return;}
    status=WdfRequestRetrieveInputBuffer(request,expected,&input,NULL);if(!NT_SUCCESS(status)){WdfRequestComplete(request,status);return;}
    if(versioned)generation=((EM_VERSIONED_REQUEST *)input)->generation;
    RtlCopyMemory(&header,input,sizeof(header)); /* Capture before touching shared METHOD_BUFFERED output. */
    if(header.version!=EM_ABI_VERSION || header.reserved!=0 || (code!=IOCTL_EM_CONFIG && header.flags!=0) ||
       ((code==IOCTL_EM_STATS || code==IOCTL_EM_RESET || code==IOCTL_EM_CONFIG) && header.key!=0) ||
       (code==IOCTL_EM_CONFIG && header.flags>EM_MODE_XPRESS)) {WdfRequestComplete(request,STATUS_INVALID_PARAMETER);return;}
    if(((code==IOCTL_EM_GET || code==IOCTL_EM_GET_VERSIONED) && outputLength!=EM_PAGE_BYTES) || (code==IOCTL_EM_STATS && outputLength!=sizeof(EM_STATS)) ||
       (code==IOCTL_EM_PUT_COMPRESSED && outputLength!=sizeof(EM_PAGER_RECEIPT)) ||
       (code!=IOCTL_EM_GET && code!=IOCTL_EM_GET_VERSIONED && code!=IOCTL_EM_PUT_COMPRESSED && code!=IOCTL_EM_STATS && outputLength!=0)){WdfRequestComplete(request,STATUS_INFO_LENGTH_MISMATCH);return;}
    if(outputLength){status=WdfRequestRetrieveOutputBuffer(request,outputLength,&output,NULL);if(!NT_SUCCESS(status)){WdfRequestComplete(request,status);return;}}
    status=WdfWaitLockAcquire(d->lock,NULL);if(!NT_SUCCESS(status)){WdfRequestComplete(request,status);return;}
    if(!f->active)status=STATUS_FILE_CLOSED;
    else if(WdfRequestIsCanceled(request))status=STATUS_CANCELLED;
    else {
    switch(code){
    case IOCTL_EM_PUT_COMPRESSED:
        status=EmStatus(em_put_compressed(&f->store,header.key,((EM_PUT_REQUEST *)input)->page,EM_PAGE_BYTES,d->scratch,&receipt));
        /* Buffered output aliases input; copy only AFTER input consumed. */
        if(NT_SUCCESS(status)){RtlCopyMemory(output,&receipt,sizeof(receipt));information=sizeof(receipt);}break;
    case IOCTL_EM_GET_VERSIONED:status=EmStatus(em_get_versioned(&f->store,header.key,generation,(uint8_t *)output,EM_PAGE_BYTES));if(NT_SUCCESS(status))information=EM_PAGE_BYTES;break;
    case IOCTL_EM_DELETE_VERSIONED:status=EmStatus(em_delete_versioned(&f->store,header.key,generation));break;
    case IOCTL_EM_PUT:status=EmStatus(em_put(&f->store,header.key,((EM_PUT_REQUEST *)input)->page,EM_PAGE_BYTES,d->scratch));break;
    case IOCTL_EM_GET:status=EmStatus(em_get(&f->store,header.key,(uint8_t *)output,EM_PAGE_BYTES));if(NT_SUCCESS(status)){information=EM_PAGE_BYTES;}break;
    case IOCTL_EM_DELETE:status=EmStatus(em_delete(&f->store,header.key));break;
    case IOCTL_EM_RESET:em_reset(&f->store);status=STATUS_SUCCESS;break;
    case IOCTL_EM_CONFIG:if(f->store.pages)status=STATUS_DEVICE_BUSY;else{f->store.mode=header.flags;status=STATUS_SUCCESS;}break;
    case IOCTL_EM_STATS:{
        EM_STATS *s=(EM_STATS *)output;RtlZeroMemory(s,sizeof(*s));s->version=EM_ABI_VERSION;s->mode=f->store.mode;
        s->pages=f->store.pages;s->max_pages=EM_MAX_PAGES;s->logical_bytes=(uint64_t)s->pages*EM_PAGE_BYTES;s->payload_bytes=f->store.payload;
        s->session_allocation_bytes=f->store.allocation;s->session_quota_bytes=f->store.quota;s->global_allocation_bytes=d->allocated;s->global_limit_bytes=EM_GLOBAL_LIMIT;
        s->puts=f->store.puts;s->gets=f->store.gets;s->deletes=f->store.deletes;s->compressed_pages=f->store.compressed_pages;s->raw_pages=f->store.raw_pages;
        information=sizeof(*s);status=STATUS_SUCCESS;break;}
    default:status=STATUS_INVALID_DEVICE_REQUEST;break;
    }
    }
    WdfWaitLockRelease(d->lock);WdfRequestCompleteWithInformation(request,status,information);
}
VOID EmDeviceCleanup(WDFOBJECT device) {
    DEVICE_CONTEXT *d=EmDevice(device);
    NT_ASSERT(d->sessions==0);
    if(d->workspace){em_wipe(d->workspace,d->workspaceBytes);EmFree(d,d->workspace,d->workspaceBytes);d->workspace=NULL;}
    if(d->scratch){em_wipe(d->scratch,EM_PAGE_BYTES);EmFree(d,d->scratch,EM_PAGE_BYTES);d->scratch=NULL;}
    NT_ASSERT(d->allocated==0);
}
VOID EmUnload(WDFDRIVER driver) {UNREFERENCED_PARAMETER(driver); /* KMDF deletes non-PnP control device after return. */}
NTSTATUS DriverEntry(PDRIVER_OBJECT driverObject,PUNICODE_STRING registryPath) {
    WDF_DRIVER_CONFIG config;WDFDRIVER driver;PWDFDEVICE_INIT init;WDFDEVICE device;
    WDF_OBJECT_ATTRIBUTES attributes,fileAttributes,lockAttributes;WDF_FILEOBJECT_CONFIG files;WDF_IO_QUEUE_CONFIG queue;
    DEVICE_CONTEXT *d;NTSTATUS status;ULONG fragmentBytes=0;
    DECLARE_CONST_UNICODE_STRING(sddl,L"D:P(A;;GA;;;SY)(A;;GA;;;BA)");
    DECLARE_CONST_UNICODE_STRING(name,L"\\Device\\ElasticMemStore");
    DECLARE_CONST_UNICODE_STRING(link,L"\\DosDevices\\ElasticMemStore");
    WDF_DRIVER_CONFIG_INIT(&config,WDF_NO_EVENT_CALLBACK);config.DriverInitFlags=WdfDriverInitNonPnpDriver;config.EvtDriverUnload=EmUnload;
    status=WdfDriverCreate(driverObject,registryPath,WDF_NO_OBJECT_ATTRIBUTES,&config,&driver);if(!NT_SUCCESS(status))return status;
    init=WdfControlDeviceInitAllocate(driver,&sddl);if(!init)return STATUS_INSUFFICIENT_RESOURCES;
    WdfDeviceInitSetDeviceType(init,EM_DEVICE_TYPE);WdfDeviceInitSetCharacteristics(init,FILE_DEVICE_SECURE_OPEN,FALSE);
    WdfDeviceInitSetIoType(init,WdfDeviceIoBuffered);WdfDeviceInitSetExclusive(init,FALSE);
    status=WdfDeviceInitAssignName(init,&name);if(!NT_SUCCESS(status)){WdfDeviceInitFree(init);return status;}
    WDF_FILEOBJECT_CONFIG_INIT(&files,EmCreate,WDF_NO_EVENT_CALLBACK,EmCleanup);files.FileObjectClass=WdfFileObjectWdfCanUseFsContext;
    files.AutoForwardCleanupClose=WdfFalse;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&fileAttributes,FILE_CONTEXT);
    WdfDeviceInitSetFileObjectConfig(init,&files,&fileAttributes);
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes,DEVICE_CONTEXT);attributes.ExecutionLevel=WdfExecutionLevelPassive;
    attributes.SynchronizationScope=WdfSynchronizationScopeNone;attributes.EvtCleanupCallback=EmDeviceCleanup;
    status=WdfDeviceCreate(&init,&attributes,&device);if(!NT_SUCCESS(status)){if(init)WdfDeviceInitFree(init);return status;}
    d=EmDevice(device);
    WDF_OBJECT_ATTRIBUTES_INIT(&lockAttributes);lockAttributes.ParentObject=device;
    status=WdfWaitLockCreate(&lockAttributes,&d->lock);if(!NT_SUCCESS(status))return status;
    status=RtlGetCompressionWorkSpaceSize(COMPRESSION_FORMAT_XPRESS|COMPRESSION_ENGINE_STANDARD,&d->workspaceBytes,&fragmentBytes);
    if(!NT_SUCCESS(status) || !d->workspaceBytes || d->workspaceBytes>EM_MAX_WORKSPACE)return STATUS_NOT_SUPPORTED;
    d->workspace=EmAlloc(d,d->workspaceBytes);d->scratch=(uint8_t *)EmAlloc(d,EM_PAGE_BYTES);
    if(!d->workspace || !d->scratch)return STATUS_INSUFFICIENT_RESOURCES;
    status=WdfDeviceCreateSymbolicLink(device,&link);if(!NT_SUCCESS(status))return status;
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queue,WdfIoQueueDispatchSequential);queue.EvtIoDeviceControl=EmIoctl;queue.PowerManaged=WdfFalse;
    status=WdfIoQueueCreate(device,&queue,WDF_NO_OBJECT_ATTRIBUTES,WDF_NO_HANDLE);if(!NT_SUCCESS(status))return status;
    WdfControlFinishInitializing(device);return STATUS_SUCCESS;
}
