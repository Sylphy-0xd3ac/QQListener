#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
int wmain(int argc,wchar_t **argv) {
 if(argc!=2)return 2;
 SetEnvironmentVariableW(L"PMHQ_SRDI_TEST",L"1");
 unsigned char *m=(unsigned char *)LoadLibraryW(argv[1]);if(!m)return 3;
 unsigned char seed[32],nonce[12],proof[32],key[32],plain[80];
 for(unsigned i=0;i<12;i++)nonce[i]=(unsigned char)(i*17+3);
 ((void(*)(void *))(m+0x694d8))(seed);
 ((void(*)(const void *,const void *,void *))(m+0x68e55))(seed,nonce,proof);
 ((void(*)(void *,const void *,const void *,const void *))(m+0x69470))(key,seed,nonce,proof);
 unsigned rvas[]={0x94610,0x9465c,0x9468f,0x946d0};
 unsigned lengths[]={76,51,65,35};
 for(unsigned i=0;i<4;i++) {
  memcpy(plain,m+rvas[i],lengths[i]);
  ((void(*)(void *,void *,size_t))(m+0x69272))(key,plain,lengths[i]);
  printf("anchor=%u last_byte=%u prefix=%.8s\n",i,plain[lengths[i]-1],plain);fflush(stdout);
  for(unsigned j=0;j<lengths[i];j++)if((plain[j]<32||plain[j]>=127)&&!(j+1==lengths[i]&&plain[j]==0))return 5;
 }
 SecureZeroMemory(seed,32);SecureZeroMemory(proof,32);SecureZeroMemory(key,32);
 puts("local_initialization=4/4 anchors valid; no manager request; no QQ injection");
 return 0;
}
