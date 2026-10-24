#ifndef QLS_MINICRT_H
#define QLS_MINICRT_H
#include <windows.h>
#include <stddef.h>
#include <stdint.h>
#include <string.h>
#include <stdlib.h>
#include <stdio.h>
#include <wchar.h>
#include <stdarg.h>
static void *ls_memcpy(void *d, const void *s, size_t n) { unsigned char *p=d; const unsigned char *q=s; for(size_t i=0;i<n;++i)p[i]=q[i]; return d; }
static void *ls_memset(void *d, int v, size_t n) { unsigned char *p=d; for(size_t i=0;i<n;++i)p[i]=(unsigned char)v; return d; }
static int ls_memcmp(const void *a,const void *b,size_t n) { const unsigned char *p=a,*q=b; for(size_t i=0;i<n;++i)if(p[i]!=q[i])return p[i]<q[i]?-1:1; return 0; }
static size_t ls_strlen(const char *s) { size_t n=0;while(s[n])++n;return n; }
static void *ls_memchr(const void *s,int c,size_t n) {const unsigned char *p=s;for(size_t i=0;i<n;++i)if(p[i]==(unsigned char)c)return(void *)(p+i);return NULL;}
static char *ls_strchr(const char *s,int c) {for(;;++s){if(*s==(char)c)return(char *)s;if(!*s)return NULL;}}
static size_t ls_wcslen(const wchar_t *s){size_t n=0;while(s[n])++n;return n;}
static wchar_t *ls_wcsrchr(const wchar_t *s,wchar_t c){const wchar_t *found=NULL;do{if(*s==c)found=s;}while(*s++);return(wchar_t *)found;}
static int ls_wcsicmp(const wchar_t *a,const wchar_t *b){for(;;++a,++b){wchar_t x=*a,y=*b;if(x>=L'A'&&x<=L'Z')x+=32;if(y>=L'A'&&y<=L'Z')y+=32;if(x!=y)return x<y?-1:1;if(!x)return 0;}}
static void *ls_alloc(size_t n){return HeapAlloc(GetProcessHeap(),0,n?n:1);}
static void ls_free(void *p){if(p)HeapFree(GetProcessHeap(),0,p);}
/* The signer has exactly one formatted suffix. No general printf is linked. */
static int ls_snprintf(char *out,size_t cap,const char *fmt,...){
    const char expected[]="\",\"seq\":%lu}\n";
    if(ls_strlen(fmt)!=sizeof(expected)-1||ls_memcmp(fmt,expected,sizeof(expected)-1))return -1;
    va_list ap;va_start(ap,fmt);unsigned long n=va_arg(ap,unsigned long);va_end(ap);
    char digits[10];size_t k=0;do{digits[k++]=(char)('0'+n%10);n/=10;}while(n);
    size_t total=8+k+2;if(cap<=total)return -1;
    ls_memcpy(out,"\",\"seq\":",8);for(size_t i=0;i<k;++i)out[8+i]=digits[k-i-1];
    out[8+k]='}';out[9+k]='\n';out[10+k]=0;return(int)total;
}
#define memcpy ls_memcpy
#define memset ls_memset
#define memcmp ls_memcmp
#define memchr ls_memchr
#define strlen ls_strlen
#define strchr ls_strchr
#define wcslen ls_wcslen
#define wcsrchr ls_wcsrchr
#define _wcsicmp ls_wcsicmp
#define malloc ls_alloc
#define free ls_free
#define snprintf ls_snprintf
#endif
