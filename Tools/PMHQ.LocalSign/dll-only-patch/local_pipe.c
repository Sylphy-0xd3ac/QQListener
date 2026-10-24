#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <bcrypt.h>
#include <stdint.h>
#include "minicrt.h"

/* The PE merger keeps these names for its entry-point configuration record. */
typedef struct { char magic[8]; DWORD original_entry,original_export,shift,reserved; } DllConfig;
__declspec(dllexport) DllConfig PMHQLocalSignConfig={{'Q','L','S','G','9','0','6',0},0,0,0,0};
__declspec(dllexport) volatile LONG PMHQLocalRuntimeStatus=100;
static volatile LONG initialized;
static BOOL unset_env(const wchar_t *name){return SetEnvironmentVariableW(name,NULL)||GetLastError()==ERROR_ENVVAR_NOT_FOUND;}

static BOOL local_environment(unsigned char *base) {
    unsigned char seed[32],nonce[12],proof[32],key[32],anchor[76];
    wchar_t nonce_hex[25],proof_hex[65];
    static const wchar_t hex[]=L"0123456789abcdef";
    if(BCryptGenRandom(NULL,nonce,sizeof(nonce),BCRYPT_USE_SYSTEM_PREFERRED_RNG)<0)return FALSE;
    /* These leaf functions and constants are from the SHA-256-pinned original
     * v9.0.6 DLL. Neither call performs I/O or obtains a server credential. */
    ((void (*)(void *))(base+0x694d8))(seed);
    ((void (*)(const void *,const void *,void *))(base+0x68e55))(seed,nonce,proof);
    ((void (*)(void *,const void *,const void *,const void *))(base+0x69470))(key,seed,nonce,proof);
    memcpy(anchor,base+0x94610,sizeof(anchor));
    ((void (*)(void *,void *,size_t))(base+0x69272))(key,anchor,sizeof(anchor));
    BOOL valid=!memcmp(anchor,"MSF task pushed seq:",20);
    /* Keep a stale runtime file from an unrelated launcher out of this mode. */
    DWORD old=0,ignored=0;
    if(valid) {
        valid=!memcmp(base+0x95221,"pmhq_runtime_",13)
            && VirtualProtect(base+0x95221,13,PAGE_READWRITE,&old);
        if(valid){memcpy(base+0x95221,"qqlp_runtime_",13);valid=VirtualProtect(base+0x95221,13,old,&ignored);}
    }
    for(unsigned i=0;i<12;i++){nonce_hex[2*i]=hex[nonce[i]>>4];nonce_hex[2*i+1]=hex[nonce[i]&15];}
    for(unsigned i=0;i<32;i++){proof_hex[2*i]=hex[proof[i]>>4];proof_hex[2*i+1]=hex[proof[i]&15];}
    nonce_hex[24]=0;proof_hex[64]=0;
    if(valid) valid=SetEnvironmentVariableW(L"PMHQ_CHAL_NONCE",nonce_hex)
        && SetEnvironmentVariableW(L"PMHQ_CHAL_PROOF",proof_hex)
        && SetEnvironmentVariableW(L"PMHQ_ATTACH",L"1")
        && SetEnvironmentVariableW(L"PMHQ_SERVER_HOST",L"127.0.0.1")
        && SetEnvironmentVariableW(L"PMHQ_SERVER_PORT",L"0")
        && unset_env(L"PMHQ_DUMP_SIGN")
        && unset_env(L"PMHQ_DUMP_TEA")
        && unset_env(L"PMHQ_DEBUG_PB")
        && unset_env(L"PMHQ_HOOK_MODE");
    SecureZeroMemory(seed,sizeof(seed));SecureZeroMemory(proof,sizeof(proof));
    SecureZeroMemory(key,sizeof(key));SecureZeroMemory(anchor,sizeof(anchor));
    SecureZeroMemory(nonce_hex,sizeof(nonce_hex));SecureZeroMemory(proof_hex,sizeof(proof_hex));
    return valid;
}

static BOOL entry(HINSTANCE module,DWORD reason,void *reserved,int via_export) {
    if(reason==DLL_PROCESS_ATTACH&&InterlockedCompareExchange(&initialized,1,0)==0) {
        BOOL ok=local_environment((unsigned char *)module);
        InterlockedExchange(&PMHQLocalRuntimeStatus,ok?0:101);
        if(!ok)return FALSE;
    }
    if(PMHQLocalRuntimeStatus!=0)return FALSE;
    DWORD original=via_export?PMHQLocalSignConfig.original_export:PMHQLocalSignConfig.original_entry;
    if(!original)return FALSE;
    return ((BOOL (WINAPI *)(HINSTANCE,DWORD,void *))((unsigned char *)module+original))(module,reason,reserved);
}
__declspec(dllexport) BOOL WINAPI PMHQLocalSignBoot(HINSTANCE m,DWORD r,void *v){return entry(m,r,v,0);}
__declspec(dllexport) BOOL WINAPI PMHQLocalSignExportDllMain(HINSTANCE m,DWORD r,void *v){return entry(m,r,v,1);}
