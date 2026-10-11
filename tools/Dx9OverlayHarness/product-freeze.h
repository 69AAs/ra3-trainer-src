#pragma once
#include <tlhelp32.h>
// INSTALLATION only. No claim about cached callbacks, COM lifetime or unloading.
// Fixed storage; no heap/CRT/COM/logging while other threads are suspended.
struct ProductFreeze {
    HANDLE handles[1024]={}; DWORD ids[1024]={}; unsigned count=0;
    ~ProductFreeze() { Resume(); }
    void Resume() {
        while (count) {
            // Only drop a thread once it is really running again: closing the handle after a
            // failed ResumeThread would leave that thread suspended and untrackable forever.
            if (ResumeThread(handles[count-1])==static_cast<DWORD>(-1)) return;
            CloseHandle(handles[count-1]);
            handles[count-1]=nullptr;
            --count;
        }
    }
    bool Acquire(const CodeHook& a,const CodeHook& b) {
        for (unsigned round=0;round<4;++round) {
            HANDLE snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD,0);
            if (snapshot==INVALID_HANDLE_VALUE) return false;
            THREADENTRY32 e={}; e.dwSize=sizeof(e); unsigned added=0;
            BOOL more=Thread32First(snapshot,&e);
            if (!more) { CloseHandle(snapshot); return false; }
            do {
                if (e.th32OwnerProcessID!=GetCurrentProcessId() || e.th32ThreadID==GetCurrentThreadId()) continue;
                bool known=false; for (unsigned i=0;i<count;++i) if (ids[i]==e.th32ThreadID) known=true;
                if (known) continue;
                if (count==1024) { CloseHandle(snapshot); return false; }
                HANDLE h=OpenThread(THREAD_SUSPEND_RESUME|THREAD_GET_CONTEXT|THREAD_QUERY_INFORMATION,FALSE,e.th32ThreadID);
                if (!h) { CloseHandle(snapshot); return false; }
                if (SuspendThread(h)==static_cast<DWORD>(-1)) { CloseHandle(h); CloseHandle(snapshot); return false; }
                handles[count]=h; ids[count++]=e.th32ThreadID; ++added;
                CONTEXT c={}; c.ContextFlags=CONTEXT_CONTROL;
                if (!GetThreadContext(h,&c) ||
                    (c.Eip>=reinterpret_cast<uintptr_t>(a.target) && c.Eip<reinterpret_cast<uintptr_t>(a.target)+a.length) ||
                    (c.Eip>=reinterpret_cast<uintptr_t>(b.target) && c.Eip<reinterpret_cast<uintptr_t>(b.target)+b.length)) {
                    CloseHandle(snapshot); return false;
                }
            } while (Thread32Next(snapshot,&e));
            DWORD error=GetLastError(); CloseHandle(snapshot);
            if (error!=ERROR_NO_MORE_FILES) return false;
            if (!added) return true;
        }
        return false;
    }
};
