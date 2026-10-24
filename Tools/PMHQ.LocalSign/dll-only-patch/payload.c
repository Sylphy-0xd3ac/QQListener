#define PMHQ_FREESTANDING
#define DllMain SignerInitialize
#include "../local-sign-patch/local_sign.c"
#undef DllMain
#define PMHQ_PATCHER_LIBRARY
#include "../local-sign-patch/patcher.c"

typedef BOOL (WINAPI *NativeDllMain)(HINSTANCE,DWORD,void *);
typedef struct { char magic[8]; DWORD original_entry,original_export,shift,reserved; } DllConfig;
__declspec(dllexport) DllConfig PMHQLocalSignConfig={{'Q','L','S','G','9','0','6',0},0,0,0,0};
__declspec(dllexport) volatile LONG PMHQLocalSignAutoStatus=100;
static unsigned char *combined_base;
static wchar_t own_path[MAX_PATH];
static volatile LONG started;

static char *append_text(char *out,const char *text){size_t n=strlen(text);memcpy(out,text,n);return out+n;}
static char *append_number(char *out,DWORD value){char digits[10];unsigned n=0;do{digits[n++]=(char)('0'+value%10);value/=10;}while(value);while(n)*out++=digits[--n];return out;}
static void write_status(int code,DWORD target){
    wchar_t path[MAX_PATH];const wchar_t *last=wcsrchr(own_path,L'\\');
    if(!last)return;
    size_t prefix=(size_t)(last-own_path)+1;
    const wchar_t name[]=L"pmhq-local-sign.status";
    if(prefix+sizeof(name)/sizeof(name[0])>MAX_PATH)return;
    memcpy(path,own_path,prefix*sizeof(wchar_t));memcpy(path+prefix,name,sizeof(name));
    char record[256],*p=record;SYSTEMTIME time;GetSystemTime(&time);
    p=append_text(p,code==0?"status=installed\ncode=":"status=failed\ncode=");p=append_number(p,(DWORD)code);
    p=append_text(p,"\nqq_process_id=");p=append_number(p,GetCurrentProcessId());
    p=append_text(p,"\npmhq_process_id=");p=append_number(p,target);
    p=append_text(p,"\nutc=");p=append_number(p,time.wYear);*p++='-';p=append_number(p,time.wMonth);*p++='-';p=append_number(p,time.wDay);*p++='T';p=append_number(p,time.wHour);*p++=':';p=append_number(p,time.wMinute);*p++=':';p=append_number(p,time.wSecond);*p++='Z';*p++='\n';
    HANDLE file=CreateFileW(path,GENERIC_WRITE,FILE_SHARE_READ,NULL,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,NULL);
    if(file!=INVALID_HANDLE_VALUE){DWORD n=0;WriteFile(file,record,(DWORD)(p-record),&n,NULL);CloseHandle(file);}
}

static int auto_apply(DWORD pid) {
    HANDLE process=OpenProcess(PROCESS_CREATE_THREAD|PROCESS_QUERY_INFORMATION|PROCESS_VM_OPERATION|PROCESS_VM_WRITE|PROCESS_VM_READ,FALSE,pid);
    if(!process)return 301;
    wchar_t target_path[MAX_PATH];DWORD path_size=MAX_PATH;
    int status=302;
    if(!QueryFullProcessImageNameW(process,0,target_path,&path_size)||!verify_file(target_path))goto done;
    uintptr_t base=module_base(pid,L"pmhq-win-x64.exe");
    if(!base){status=303;goto done;}
    if(!own_path[0]){
        wchar_t *last=wcsrchr(target_path,L'\\');
        if(!last||((size_t)(last-target_path)+10>=MAX_PATH)){status=304;goto done;}
        size_t prefix=(size_t)(last-target_path)+1;
        memcpy(own_path,target_path,prefix*sizeof(wchar_t));
        memcpy(own_path+prefix,L"pmhq.dll",9*sizeof(wchar_t));
    }
    if(GetFileAttributesW(own_path)==INVALID_FILE_ATTRIBUTES){status=305;goto done;}
    SIZE_T bytes=(wcslen(own_path)+1)*sizeof(wchar_t),written=0;
    void *argument=VirtualAllocEx(process,NULL,bytes,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE);
    if(!argument){status=306;goto done;}
    if(!WriteProcessMemory(process,argument,own_path,bytes,&written)||written!=bytes){VirtualFreeEx(process,argument,0,MEM_RELEASE);status=307;goto done;}
    uintptr_t load=remote_system_proc(pid,GetProcAddress(GetModuleHandleW(L"kernel32.dll"),"LoadLibraryW"));
    DWORD value=0;int called=load?remote_call(process,load,argument,&value):0;
    if(called!=-1)VirtualFreeEx(process,argument,0,MEM_RELEASE);
    if(called!=1){status=308;goto done;}
    uintptr_t dll=module_by_path(pid,own_path);
    if(!dll){status=309;goto done;}
    uintptr_t prepare=dll+(uintptr_t)PMHQLocalSignPrepare-(uintptr_t)combined_base;
    called=remote_call(process,prepare,NULL,&value);
    if(called!=1||value){status=310;goto done;}
    FrozenThreads *threads=malloc(sizeof(FrozenThreads));
    if(!threads){status=311;goto done;}
    memset(threads,0,sizeof(*threads));
    if(!freeze_threads(pid,base,threads)){free(threads);status=312;goto done;}
    unsigned char jump[14]={0xff,0x25,0,0,0,0},before[14];
    uintptr_t hook=dll+(uintptr_t)PMHQLocalSignCompute-(uintptr_t)combined_base;
    memcpy(jump+6,&hook,8);SIZE_T got=0;DWORD old=0,ignored=0;BOOL ok=FALSE;
    if(ReadProcessMemory(process,(void *)(base+COMPUTE_RVA),before,14,&got)&&got==14){
        if(!memcmp(before,jump,14))ok=TRUE;
        else if(!memcmp(before,compute_head,14)&&VirtualProtectEx(process,(void *)(base+COMPUTE_RVA),14,PAGE_EXECUTE_READWRITE,&old)){
            ok=WriteProcessMemory(process,(void *)(base+COMPUTE_RVA),jump,14,&written)&&written==14;
            if(!ok)WriteProcessMemory(process,(void *)(base+COMPUTE_RVA),before,14,&written);
            FlushInstructionCache(process,(void *)(base+COMPUTE_RVA),14);
            if(!VirtualProtectEx(process,(void *)(base+COMPUTE_RVA),14,old,&ignored))ok=FALSE;
        }
    }
    resume_threads(threads);free(threads);
    if(!ok){status=313;goto done;}
    got=0;memset(before,0,sizeof(before));
    status=ReadProcessMemory(process,(void *)(base+COMPUTE_RVA),before,14,&got)&&got==14&&!memcmp(before,jump,14)?0:314;
done:
    CloseHandle(process);return status;
}
static DWORD unique_launcher(void){
    HANDLE snap=CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0);
    if(snap==INVALID_HANDLE_VALUE)return 0;
    PROCESSENTRY32W p={.dwSize=sizeof(p)};DWORD pid=0;unsigned count=0;
    if(Process32FirstW(snap,&p))do{if(!_wcsicmp(p.szExeFile,L"pmhq-win-x64.exe")){pid=p.th32ProcessID;++count;}}while(Process32NextW(snap,&p));
    CloseHandle(snap);return count==1?pid:0;
}
static DWORD WINAPI auto_worker(void *unused){
    (void)unused;
    int status=320;DWORD target=0;
    for(unsigned i=0;i<60;++i){
        target=unique_launcher();
        if(target){status=auto_apply(target);if(status==0)break;if(status!=310&&status!=312)break;}
        Sleep(250);
    }
    InterlockedExchange(&PMHQLocalSignAutoStatus,status);
    write_status(status,target);
    OutputDebugStringA(status==0?"[PMHQ local-sign] DLL auto-patch installed":"[PMHQ local-sign] DLL auto-patch failed; inspect PMHQLocalSignAutoStatus");
    return (DWORD)status;
}
static BOOL bootstrap(HINSTANCE module,DWORD reason,void *reserved,int via_export){
    if(reason==DLL_PROCESS_ATTACH){
        combined_base=(unsigned char *)module;
        SignerInitialize(module,reason,reserved);
        DWORD n=GetModuleFileNameW(module,own_path,MAX_PATH);
        if(!n||n>=MAX_PATH)own_path[0]=0;
    }
    wchar_t main_path[MAX_PATH];
    DWORD n=GetModuleFileNameW(NULL,main_path,MAX_PATH);
    const wchar_t *name=n&&n<MAX_PATH?wcsrchr(main_path,L'\\'):NULL;
    name=name?name+1:main_path;
    /* Same merged file acts only as the signer when loaded into PMHQ. Do not
     * initialize QQ hooks inside the launcher process. */
    if(n&&n<MAX_PATH&&!_wcsicmp(name,L"pmhq-win-x64.exe")){
        if(reason==DLL_PROCESS_ATTACH)InterlockedExchange(&PMHQLocalSignAutoStatus,200);
        return TRUE;
    }
    DWORD original=via_export?PMHQLocalSignConfig.original_export:PMHQLocalSignConfig.original_entry;
    if(!original)return FALSE;
    BOOL ok=((NativeDllMain)((unsigned char *)module+original))(module,reason,reserved);
    if(ok&&reason==DLL_PROCESS_ATTACH&&n&&n<MAX_PATH&&!_wcsicmp(name,L"QQ.exe")&&InterlockedCompareExchange(&started,1,0)==0){
        HANDLE thread=CreateThread(NULL,0,auto_worker,NULL,0,NULL);
        if(thread)CloseHandle(thread);else InterlockedExchange(&PMHQLocalSignAutoStatus,321);
    }
    return ok;
}
__declspec(dllexport) BOOL WINAPI PMHQLocalSignBoot(HINSTANCE m,DWORD r,void *v){return bootstrap(m,r,v,0);}
__declspec(dllexport) BOOL WINAPI PMHQLocalSignExportDllMain(HINSTANCE m,DWORD r,void *v){return bootstrap(m,r,v,1);}
