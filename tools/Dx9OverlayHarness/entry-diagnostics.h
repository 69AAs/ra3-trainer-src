#pragma once
#include <windows.h>
#include <stdint.h>
#include <string.h>
struct EntryTransfer { const char* kind="none"; uintptr_t target=0; uintptr_t cell=0; bool resolved=false; };
// Bounded snapshot decoder, diagnostics only. Never used to authorize a hook.
static EntryTransfer DecodeEntryTransfer(const BYTE* b,size_t n,uintptr_t pc) {
    EntryTransfer r;
    if (n>=5 && b[0]==0xe9) { int32_t v; memcpy(&v,b+1,4); r.kind="jmp-rel32"; r.target=static_cast<uint32_t>(pc+5+v); r.resolved=true; }
    else if(n>=2 && b[0]==0xeb) { r.kind="jmp-rel8"; r.target=static_cast<uint32_t>(pc+2+static_cast<int8_t>(b[1])); r.resolved=true; }
    else if(n>=6 && b[0]==0xff && b[1]==0x25) { uint32_t v; memcpy(&v,b+2,4); r.kind="jmp-indirect"; r.cell=v; }
    else if(n>=6 && b[0]==0x68 && b[5]==0xc3) { uint32_t v; memcpy(&v,b+1,4); r.kind="push-ret"; r.target=v; r.resolved=true; }
    else if(n>=7 && b[0]==0xb8 && b[5]==0xff && b[6]==0xe0) { uint32_t v; memcpy(&v,b+1,4); r.kind="mov-jmp"; r.target=v; r.resolved=true; }
    return r;
}
static bool ReadEntryBytes(const void* p,void* out,SIZE_T n) {
    SIZE_T got=0; return ReadProcessMemory(GetCurrentProcess(),p,out,n,&got) && got==n;
}
static void EntryHex(const BYTE* b,size_t n,char* out) {
    const char* h="0123456789ABCDEF";
    for(size_t i=0;i<n;++i) { out[2*i]=h[b[i]>>4]; out[2*i+1]=h[b[i]&15]; } out[2*n]=0;
}
static void EntryModuleName(void* address,char* out,size_t capacity) {
    HMODULE m=nullptr; WCHAR path[MAX_PATH]={};
    if(!out || capacity<1) return;
    // Both the default name and every fallback write are bounded by capacity and always
    // NUL-terminated, so the sanitization loop below can never run past the buffer, and a
    // failed/truncated conversion cannot leave it unterminated.
    const char fallback[]="unidentified";
    const size_t keep=(sizeof(fallback)-1)<(capacity-1)?(sizeof(fallback)-1):(capacity-1);
    memcpy(out,fallback,keep); out[keep]=0;
    if(address && GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,reinterpret_cast<LPCWSTR>(address),&m)) {
        DWORD n=GetModuleFileNameW(m,path,MAX_PATH);
        if(n && n<MAX_PATH) {
            const WCHAR* b=wcsrchr(path,L'\\'); b=b?b+1:path;
            if(!WideCharToMultiByte(CP_UTF8,0,b,-1,out,static_cast<int>(capacity),nullptr,nullptr)) { memcpy(out,fallback,keep); out[keep]=0; }
        }
        FreeLibrary(m);
    }
    out[capacity-1]=0;
    for(char* c=out;*c;++c) if(*c=='\r'||*c=='\n'||*c=='|'||*c==' ') *c='_';
}
