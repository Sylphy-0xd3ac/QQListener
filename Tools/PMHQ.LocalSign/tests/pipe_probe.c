#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <string.h>
#include <stdint.h>
static HANDLE captured;
static void capture_packet(const size_t *s){printf("native_response=%.*s\n",(int)s[2],(const char *)s[1]);fflush(stdout);SetEvent(captured);}
static int io(HANDLE p,void *buf,DWORD n,DWORD *done,int write){
 OVERLAPPED ov={0};ov.hEvent=CreateEventW(NULL,TRUE,FALSE,NULL);
 BOOL ok=write?WriteFile(p,buf,n,done,&ov):ReadFile(p,buf,n,done,&ov);
 if(!ok&&GetLastError()==ERROR_IO_PENDING){DWORD r=WaitForSingleObject(ov.hEvent,6000);if(r==WAIT_OBJECT_0)ok=GetOverlappedResult(p,&ov,done,FALSE);else{CancelIoEx(p,&ov);GetOverlappedResult(p,&ov,done,TRUE);}}
 CloseHandle(ov.hEvent);return ok;
}
int wmain(int argc,wchar_t **argv){
 if(argc!=2)return 2;
 SetEnvironmentVariableW(L"PMHQ_SRDI_TEST",L"1");
 wchar_t name[100];swprintf(name,100,L"\\\\.\\pipe\\pmhq_cmd_%lu",GetCurrentProcessId());
 HANDLE pipe=CreateNamedPipeW(name,PIPE_ACCESS_DUPLEX|FILE_FLAG_OVERLAPPED,PIPE_TYPE_BYTE|PIPE_READMODE_BYTE|PIPE_WAIT,1,65536,65536,0,NULL);if(pipe==INVALID_HANDLE_VALUE)return 3;
 unsigned char *m=(unsigned char *)LoadLibraryW(argv[1]);if(!m)return 4;
 LONG *status=(LONG *)GetProcAddress((HMODULE)m,"PMHQLocalRuntimeStatus");
 if(status){printf("local_runtime_status=%ld\n",*status);fflush(stdout);if(*status)return 10;}
 void *account=((void *(*)(void *))(m+0x2c8ec))(m+0xf31d8);
 if(account!=m+0xf31e8||*(uint64_t *)(m+0xf31d8)!=2||*(uint64_t *)(m+0xf3200)!=0)return 11;
 puts("native_account_layout=1");fflush(stdout);
 captured=CreateEventW(NULL,TRUE,FALSE,NULL);
 unsigned char jump[14]={0xff,0x25,0,0,0,0};void *fn=(void *)capture_packet;memcpy(jump+6,&fn,8);DWORD old;
 if(!VirtualProtect(m+0x23f7f,14,PAGE_EXECUTE_READWRITE,&old))return 9;
 memcpy(m+0x23f7f,jump,14);FlushInstructionCache(GetCurrentProcess(),m+0x23f7f,14);VirtualProtect(m+0x23f7f,14,old,&old);
 CreateThread(NULL,0,(LPTHREAD_START_ROUTINE)(m+0x558f),NULL,0,NULL);
 OVERLAPPED ov={0};ov.hEvent=CreateEventW(NULL,TRUE,FALSE,NULL);
 BOOL ok=ConnectNamedPipe(pipe,&ov);if(!ok&&GetLastError()==ERROR_IO_PENDING){if(WaitForSingleObject(ov.hEvent,6000)!=WAIT_OBJECT_0)return 5;DWORD n;GetOverlappedResult(pipe,&ov,&n,FALSE);}else if(!ok&&GetLastError()!=ERROR_PIPE_CONNECTED)return 6;
 puts("native_pipe_connected=1");fflush(stdout);
 const char *req="{\"id\":1,\"cmd\":\"UnitTest.LocalOnly\",\"body\":\"00\"}\n";DWORD n=0;
 if(!io(pipe,(void *)req,(DWORD)strlen(req),&n,1))return 7;
 if(WaitForSingleObject(captured,6000)!=WAIT_OBJECT_0){puts("no_native_response");return 8;}
 return 0;
}
