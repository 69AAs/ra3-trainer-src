// Offline experiment only: no PID argument, no process search, no game access.
// Only the exact child created below receives the absolute sibling DLL path.
#include <windows.h>
#include <tlhelp32.h>
#include <stdio.h>
#include <wchar.h>
#include <stdint.h>
static_assert(sizeof(void*)==4,"x86 only");
static uintptr_t RemoteLoadLibrary(DWORD pid) {
    auto address=GetProcAddress(GetModuleHandleW(L"kernel32.dll"),"LoadLibraryW");
    HMODULE owner=nullptr;
    if (!address || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(address),&owner)) return 0;
    wchar_t path[MAX_PATH];
    if (!GetModuleFileNameW(owner,path,MAX_PATH)) return 0;
    const wchar_t* name=wcsrchr(path,L'\\'); if (!name) return 0; ++name;
    HANDLE snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPMODULE,pid);
    if (snapshot==INVALID_HANDLE_VALUE) return 0;
    MODULEENTRY32W entry={}; entry.dwSize=sizeof(entry); uintptr_t result=0;
    for (BOOL more=Module32FirstW(snapshot,&entry);more;more=Module32NextW(snapshot,&entry)) {
        // Require same system module file, not just a matching basename.
        if (!_wcsicmp(entry.szModule,name) && !_wcsicmp(entry.szExePath,path)) {
            uintptr_t rva=reinterpret_cast<uintptr_t>(address)-reinterpret_cast<uintptr_t>(owner);
            if (rva<entry.modBaseSize) result=reinterpret_cast<uintptr_t>(entry.modBaseAddr)+rva;
            break;
        }
    }
    CloseHandle(snapshot); return result;
}
int wmain(int argc,wchar_t**) {
    if (argc!=1) { puts("No arguments accepted; offline child only."); return 2; }
    wchar_t exe[MAX_PATH],dll[MAX_PATH],command[MAX_PATH+32];
    DWORD n=GetModuleFileNameW(nullptr,exe,MAX_PATH); if (!n || n>MAX_PATH-40) return 2;
    wchar_t* slash=wcsrchr(exe,L'\\'); if (!slash) return 2;
    wcscpy(slash+1,L"Dx9OverlayHarness.exe"); wcscpy(dll,exe);
    wcscpy(wcsrchr(dll,L'\\')+1,L"Ra3Overlay.dll");
    if (GetFileAttributesW(dll)==INVALID_FILE_ATTRIBUTES) return 3;
    swprintf(command,MAX_PATH+32,L"\"%ls\" --injected",exe);
    STARTUPINFOW si={}; si.cb=sizeof(si); si.dwFlags=STARTF_USESHOWWINDOW; si.wShowWindow=SW_HIDE;
    PROCESS_INFORMATION pi={};
    if (!CreateProcessW(exe,command,nullptr,nullptr,FALSE,CREATE_NO_WINDOW,nullptr,nullptr,&si,&pi)) {
        printf("CreateProcess error=%lu\n",GetLastError()); return 4;
    }
    CloseHandle(pi.hThread);
    BOOL targetWow=FALSE,selfWow=FALSE;
    bool same=IsWow64Process(pi.hProcess,&targetWow)&&IsWow64Process(GetCurrentProcess(),&selfWow)&&targetWow==selfWow;
    uintptr_t remoteEntry=0;
    for (int i=0;same && i<100 && !remoteEntry;i++) { remoteEntry=RemoteLoadLibrary(pi.dwProcessId); if (!remoteEntry) Sleep(20); }
    SIZE_T bytes=(wcslen(dll)+1)*sizeof(wchar_t),written=0;
    void* remote=remoteEntry?VirtualAllocEx(pi.hProcess,nullptr,bytes,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE):nullptr;
    HANDLE thread=nullptr;
    if (remote && WriteProcessMemory(pi.hProcess,remote,dll,bytes,&written) && written==bytes)
        thread=CreateRemoteThread(pi.hProcess,nullptr,0,reinterpret_cast<LPTHREAD_START_ROUTINE>(remoteEntry),remote,0,nullptr);
    DWORD loaded=0; bool completed=thread && WaitForSingleObject(thread,10000)==WAIT_OBJECT_0;
    if (completed) GetExitCodeThread(thread,&loaded);
    printf("same-bitness=%d remote-thread-completed=%d LoadLibrary-nonzero=%d\n",same,completed,loaded!=0);
    // Timeout: no TerminateThread, no remote free, no remote FreeLibrary retry.
    // Child has a bounded startup wait; inconclusive allocations die with child.
    if (remote && (!thread || completed)) VirtualFreeEx(pi.hProcess,remote,0,MEM_RELEASE);
    if (thread) CloseHandle(thread);
    DWORD wait=WaitForSingleObject(pi.hProcess,30000),result=999;
    if (wait==WAIT_OBJECT_0) GetExitCodeProcess(pi.hProcess,&result);
    printf("owned-child-exited=%d child-exit=%lu\n",wait==WAIT_OBJECT_0,result);
    CloseHandle(pi.hProcess);
    return completed && loaded && result==0?0:1;
}
