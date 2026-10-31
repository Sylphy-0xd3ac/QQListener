#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#ifdef _MSC_VER
#include <intrin.h>
#define _Thread_local __declspec(thread)
#endif

/* Pinned v9.0.6 only. No QQ injection or real hooks (PMHQ_SRDI_TEST). */
static unsigned char *image;
static HANDLE loaded, finish, existing_started, ready[2];
static void *blocks[3];
static volatile LONG failures;
static _Thread_local unsigned char host_tls[512];
extern DWORD _tls_index;

static LONG WINAPI report_fault(EXCEPTION_POINTERS *fault) {
    static LONG reports;
    if(fault->ExceptionRecord->ExceptionCode!=EXCEPTION_ACCESS_VIOLATION
        || InterlockedIncrement(&reports)>4)return EXCEPTION_CONTINUE_SEARCH;
    MEMORY_BASIC_INFORMATION memory={0};
    VirtualQuery(fault->ExceptionRecord->ExceptionAddress,&memory,sizeof(memory));
    CONTEXT *c=fault->ContextRecord;
    fprintf(stderr,"native_fault code=%08lx rva=%llx operation=%llu target=%llx protect=%lx "
        "rax=%llx rcx=%llx rdx=%llx r8=%llx r9=%llx rsp=%llx\n",
        fault->ExceptionRecord->ExceptionCode,
        (unsigned long long)((uintptr_t)fault->ExceptionRecord->ExceptionAddress-(uintptr_t)memory.AllocationBase),
        (unsigned long long)fault->ExceptionRecord->ExceptionInformation[0],
        (unsigned long long)fault->ExceptionRecord->ExceptionInformation[1],memory.Protect,
        c->Rax,c->Rcx,c->Rdx,c->R8,c->R9,c->Rsp);
    fflush(stderr);
    return EXCEPTION_CONTINUE_SEARCH;
}

static void check_tls(unsigned id) {
    unsigned char before[sizeof(host_tls)], output[128]={0};
    memset(host_tls,0x30+id,sizeof(host_tls));
    memcpy(before,host_tls,sizeof(before));
    IMAGE_NT_HEADERS64 *nt=(void *)(image+((IMAGE_DOS_HEADER *)image)->e_lfanew);
    IMAGE_TLS_DIRECTORY64 *tls=(void *)(image+nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_TLS].VirtualAddress);
    DWORD index=*(DWORD *)(uintptr_t)tls->AddressOfIndex;
    void **slots;
#ifdef _MSC_VER
    slots=(void **)(uintptr_t)__readgsqword(0x58);
#else
    __asm__("movq %%gs:0x58, %0" : "=r"(slots));
#endif
    if(tls->EndAddressOfRawData-tls->StartAddressOfRawData!=136
        || tls->AddressOfIndex-(uintptr_t)image!=0xf59c0 || index==_tls_index || !slots[index]) {
        InterlockedIncrement(&failures);return;
    }
    blocks[id]=slots[index];
    /* Rust TLS accessor used by the review's corruption reproducer. */
    ((void (*)(void *,void *))(image+0x7fdb0))(output,NULL);
    unsigned changes=0;
    for(unsigned i=0;i<sizeof(before);i++)if(before[i]!=host_tls[i])changes++;
    if(changes)InterlockedIncrement(&failures);
    printf("thread=%u dll_tls_index=%lu host_tls_index=%lu host_tls_changes=%u\n",id,index,_tls_index,changes);
    fflush(stdout);
}

static DWORD WINAPI worker(void *value) {
    unsigned id=(unsigned)(uintptr_t)value;
    if(id==1){host_tls[0]=0xa5;SetEvent(existing_started);}
    WaitForSingleObject(loaded,INFINITE);
    check_tls(id);
    SetEvent(ready[id-1]);
    WaitForSingleObject(finish,INFINITE);
    return 0;
}
static DWORD WINAPI deadline(void *unused) {
    (void)unused;Sleep(15000);ExitProcess(99);return 0;
}
int wmain(int argc,wchar_t **argv) {
    if(argc!=2)return 2;
    AddVectoredExceptionHandler(1,report_fault);
    SetEnvironmentVariableW(L"PMHQ_SRDI_TEST",L"1");
    loaded=CreateEventW(NULL,TRUE,FALSE,NULL);
    finish=CreateEventW(NULL,TRUE,FALSE,NULL);
    existing_started=CreateEventW(NULL,TRUE,FALSE,NULL);
    for(unsigned i=0;i<2;i++)ready[i]=CreateEventW(NULL,TRUE,FALSE,NULL);
    HANDLE timer=CreateThread(NULL,0,deadline,NULL,0,NULL);
    if(!timer)return 3;
    CloseHandle(timer);
    HANDLE threads[2];
    threads[0]=CreateThread(NULL,0,worker,(void *)(uintptr_t)1,0,NULL);
    if(!threads[0]||WaitForSingleObject(existing_started,5000)!=WAIT_OBJECT_0)return 8;
    image=(unsigned char *)LoadLibraryW(argv[1]);
    if(!image){printf("LoadLibraryW failed=%lu\n",GetLastError());return 4;}
    check_tls(0);
    SetEvent(loaded);
    threads[1]=CreateThread(NULL,0,worker,(void *)(uintptr_t)2,0,NULL);
    if(!threads[0]||!threads[1]||WaitForMultipleObjects(2,ready,TRUE,10000)!=WAIT_OBJECT_0)return 5;
    if(!blocks[0]||!blocks[1]||!blocks[2]||blocks[0]==blocks[1]||blocks[0]==blocks[2]||blocks[1]==blocks[2])
        InterlockedIncrement(&failures);
    SetEvent(finish);
    if(WaitForMultipleObjects(2,threads,TRUE,10000)!=WAIT_OBJECT_0)return 6;
    for(unsigned i=0;i<2;i++){CloseHandle(threads[i]);CloseHandle(ready[i]);}
    CloseHandle(loaded);CloseHandle(finish);CloseHandle(existing_started);
    printf("system_loader_tls=%s (loading thread, existing thread, new thread; isolated fixture only)\n",failures?"FAIL":"PASS");
    return failures?7:0;
}
