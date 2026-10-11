#pragma once
#include <windows.h>
#include <stddef.h>
#include <stdio.h>
#include <string.h>
// Independent mapping: host is the sole writer. DLL requests FILE_MAP_READ only.
// Keep the view for process lifetime: disconnect never frees memory under a callback.
struct ProductStatus {
    DWORD magic, size, sequence, hostPid, targetPid, heartbeat, enabled, connected, count;
    WCHAR version[32], action[128];
};
static_assert(sizeof(ProductStatus)==356 && offsetof(ProductStatus,action)==100,"status ABI");
#ifndef PRODUCT_PROXY_ONLY
static const ProductStatus* productView;
static HANDLE productMapping;
static DWORD productRetry;
#endif
static void ProductLog(const char* event) {
    char path[MAX_PATH], folder[MAX_PATH];
    if (event == nullptr) return;
    if (!GetTempPathA(MAX_PATH,folder)) return;
    snprintf(path,sizeof(path),"%sRa3Overlay-%lu.log",folder,GetCurrentProcessId());
    HANDLE f=CreateFileA(path,FILE_APPEND_DATA,FILE_SHARE_READ|FILE_SHARE_WRITE,nullptr,OPEN_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);
    if (f==INVALID_HANDLE_VALUE) return;
    DWORD n; WriteFile(f,event,static_cast<DWORD>(strlen(event)),&n,nullptr); WriteFile(f,"\r\n",2,&n,nullptr); CloseHandle(f);
}
#ifndef PRODUCT_PROXY_ONLY
static bool ReadProductStatus(ProductStatus& s) {
    DWORD now=GetTickCount();
    if (!productView && static_cast<DWORD>(now-productRetry)>=500) {
        productRetry=now;
        FILETIME created,exit,kernel,user;
        if (!GetProcessTimes(GetCurrentProcess(),&created,&exit,&kernel,&user)) return false;
        ULONGLONG identity=(static_cast<ULONGLONG>(created.dwHighDateTime)<<32)|created.dwLowDateTime;
        WCHAR name[96]; swprintf(name,96,L"Local\\Ra3Trainer.Overlay.v1.%lu.%016llX",GetCurrentProcessId(),identity);
        productMapping=OpenFileMappingW(FILE_MAP_READ,FALSE,name);
        if (productMapping) {
            productView=static_cast<const ProductStatus*>(MapViewOfFile(productMapping,FILE_MAP_READ,0,0,sizeof(s)));
            if (!productView) { CloseHandle(productMapping); productMapping=nullptr; }
        }
    }
    if (!productView) return false;
    DWORD a=*reinterpret_cast<const volatile DWORD*>(&productView->sequence);
    if (a&1) return false;
    MemoryBarrier(); memcpy(&s,productView,sizeof(s)); MemoryBarrier();
    DWORD b=*reinterpret_cast<const volatile DWORD*>(&productView->sequence);
    return a==b && a==s.sequence && !(b&1) && s.magic==0x3544534f && s.size==sizeof(s) &&
        s.targetPid==GetCurrentProcessId() && static_cast<DWORD>(now-s.heartbeat)<5000 &&
        s.version[31]==0 && s.action[127]==0;
}
#endif
