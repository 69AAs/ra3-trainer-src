#pragma once
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include "vendor/hde/hde32.h"

// Offline host contract: lifecycle runs on the device owner thread, with ALL
// callers of these shared function addresses stopped/joined (including aliases).
// A callback counter alone never proves this. No SEH, no device/vtable writes.
// HDE decodes boundaries; a deliberately narrow whitelist proves relocation.
struct CodeHook {
#ifdef CODE_HOOK_TESTING
    static inline int failProtectAt=0, failFlushAt=0;
#endif
    static bool Protect(void* p,size_t n,DWORD flags,DWORD* old) {
#ifdef CODE_HOOK_TESTING
        if (failProtectAt && !--failProtectAt) return false;
#endif
        return VirtualProtect(p,n,flags,old)!=FALSE;
    }
    static bool Flush(void* p,size_t n) {
#ifdef CODE_HOOK_TESTING
        if (failFlushAt && !--failFlushAt) return false;
#endif
        return FlushInstructionCache(GetCurrentProcess(),p,n)!=FALSE;
    }
    BYTE* target=nullptr;
    BYTE* trampoline=nullptr;
    BYTE before[32]={}, patch[32]={};
    size_t length=0;
    DWORD protection=0;
    bool installed=false, uncertain=false;

    static bool Read(const void* p,void* out,size_t n) {
        SIZE_T got=0;
        return p && ReadProcessMemory(GetCurrentProcess(),p,out,n,&got) && got==n;
    }
    static bool Jump(BYTE* out,uintptr_t from,uintptr_t to) {
        const int64_t delta=static_cast<int64_t>(to)-static_cast<int64_t>(from)-5;
        if (delta<INT32_MIN || delta>INT32_MAX) return false; // x86 rel32 only: never wrap silently
        out[0]=0xe9; uint32_t rel=static_cast<uint32_t>(static_cast<int32_t>(delta));
        memcpy(out+1,&rel,4);
        return true;
    }
    void Print(const char* phase,const BYTE* bytes) const {
#ifdef OVERLAY_PRODUCT
        (void)phase; (void)bytes; // CRT locks may be held by suspended threads during installation.
#else
        printf("hook %s address=%p length=%u bytes=",phase,target,static_cast<unsigned>(length));
        for (size_t i=0;i<length;i++) printf("%02x",bytes[i]);
        puts("");
#endif
    }
    bool Prepare(void* address,void* replacement) {
        static_assert(sizeof(void*)==4,"x86 relocation only");
        if (target || !address || !replacement) {
#ifndef OVERLAY_PRODUCT
            puts("hook rejected: missing address/replacement or existing plan");
#endif
            return false;
        }
        memset(before,0,sizeof(before)); memset(patch,0,sizeof(patch));
        MEMORY_BASIC_INFORMATION m={};
        if (!VirtualQuery(address,&m,sizeof(m)) || m.State!=MEM_COMMIT ||
            (m.Protect&(PAGE_GUARD|PAGE_NOACCESS)) ||
            !(m.Protect&(PAGE_EXECUTE_READ|PAGE_EXECUTE_READWRITE|PAGE_EXECUTE_WRITECOPY))) {
#ifndef OVERLAY_PRODUCT
            printf("hook rejected: unreadable/non-executable address=%p\n",address);
#endif
            return false;
        }
        target=static_cast<BYTE*>(address); protection=m.Protect;
        const size_t available=static_cast<BYTE*>(m.BaseAddress)+m.RegionSize-target;
        const size_t count=available<sizeof(before)?available:sizeof(before);
        if (!Read(target,before,count)) { target=nullptr; return false; }
        bool jump=false;
        while (length<5) {
            BYTE scratch[32]={};
            if (length>=count) return Reject();
            memcpy(scratch,before+length,count-length);
            hde32s h={}; hde32_disasm(scratch,&h);
            if (!h.len || (h.flags&F_ERROR) || length+h.len>count) return Reject();
            // Restrict to integer, position-independent prologue instructions.
            // Reject calls (including call/pop EIP idioms), Jcc/loop/short jumps,
            // returns, FPU environment/EIP saves, VEX, indirect control flow.
            const BYTE op=h.opcode;
            // 绝对寻址（modrm mod=00,rm=101 的 [disp32]、SIB base=101，以及 0xa0-0xa3 的 moffs32）
            // 不需要重定位：操作数是**绝对**地址，整条指令搬到跳板里仍指向同一处。
            // 需要重定位的只有相对量（EIP 相对的分支/调用），那些由 F_RELATIVE 挡掉。
            const bool ordinary=(op>=0x50&&op<=0x5f) || op==0x90 ||
                (op>=0xb8&&op<=0xbf) || op==0x68 || op==0x6a ||
                op==0x8b || op==0x89 || op==0x8d || op==0x85 ||
                op==0x31 || op==0x33 || op==0x81 || op==0x83 ||
                (op>=0xa0&&op<=0xa3) || op==0xc6 || op==0xc7;
            if (h.flags&F_PREFIX_ANY) return Reject();
            if (op==0xe9 && h.len==5 && length==0) jump=true;
            else if (!ordinary || (h.flags&F_RELATIVE)) return Reject();
            length+=h.len;
        }
        trampoline=static_cast<BYTE*>(VirtualAlloc(nullptr,64,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
        if (!trampoline) return Reject();
        memcpy(trampoline,before,length);
        if (jump) {
            uint32_t rel; memcpy(&rel,before+1,4);
            uintptr_t dest=reinterpret_cast<uintptr_t>(target)+5+rel;
            if (dest>=reinterpret_cast<uintptr_t>(target) && dest<reinterpret_cast<uintptr_t>(target)+length) return Reject();
            if (!Jump(trampoline,reinterpret_cast<uintptr_t>(trampoline),dest)) return Reject();
        }
        if (!Jump(trampoline+length,reinterpret_cast<uintptr_t>(trampoline)+length,reinterpret_cast<uintptr_t>(target)+length)) return Reject();
        DWORD old;
        if (!Protect(trampoline,64,PAGE_EXECUTE_READ,&old) || !Flush(trampoline,64)) return Reject();
        memset(patch,0x90,length);
        if (!Jump(patch,reinterpret_cast<uintptr_t>(target),reinterpret_cast<uintptr_t>(replacement))) return Reject();
        Print("original",before); Print("planned",patch);
        return true;
    }
    bool Reject() {
#ifndef OVERLAY_PRODUCT
        // Same rule as Print(): no CRT/stdout write while another thread may be suspended
        // mid-install (EngineLua's actionTap.Prepare runs inside the frozen window).
        printf("hook rejected address=%p decoded=%u: unsupported/unreadable prologue or allocation\n",target,static_cast<unsigned>(length));
        printf("hook inspected bytes=");
        for (size_t i=0;i<sizeof(before);i++) printf("%02x",before[i]);
        puts(" (unread bytes zero-padded)");
#endif
        Release(); return false;
    }
    bool Overlaps(const CodeHook& other) const {
        uintptr_t a=reinterpret_cast<uintptr_t>(target),b=reinterpret_cast<uintptr_t>(other.target);
        return a<b+other.length && b<a+length;
    }
    bool Equals(const BYTE* expected) const {
        BYTE actual[32]={}; return target && length && Read(target,actual,length) && !memcmp(actual,expected,length);
    }
    bool Write(const BYTE* bytes) {
        DWORD old=0,discard=0;
        if (!Protect(target,length,PAGE_EXECUTE_READWRITE,&old)) return false;
        // Range was validated wholly within one VirtualQuery region.
        memcpy(target,bytes,length);
        bool flush=Flush(target,length);
        bool protect=Protect(target,length,protection,&discard);
        MEMORY_BASIC_INFORMATION verified={};
        bool page=VirtualQuery(target,&verified,sizeof(verified)) && verified.Protect==protection;
        uncertain=!flush || !protect || !page || !Equals(bytes);
#ifndef OVERLAY_PRODUCT
        if (uncertain) printf("hook write unproved address=%p flush=%d protect=%d page=%d\n",target,flush,protect,page);
#endif
        return !uncertain;
    }
    bool Install() {
        if (!trampoline || installed || !Equals(before)) return false;
        installed=true; // Any attempted write must be accounted for by rollback.
        if (Write(patch)) { Print("installed",patch); return true; }
        if (Equals(before)) installed=false;
        return false;
    }
    bool Restore() {
        if (!installed) return !uncertain;
        if (!Equals(patch) && !Equals(before)) {
#ifndef OVERLAY_PRODUCT
            puts("hook foreign bytes: retain disabled");
#endif
            return false;
        }
        if (!Write(before)) return false;
        installed=false; Print("restored byte-equal",before); return true;
    }
    void Release() {
        if (installed || uncertain) return;
        if (trampoline) VirtualFree(trampoline,0,MEM_RELEASE);
        trampoline=nullptr; target=nullptr; length=0;
    }
    // 只解开"已有计划"这道门闩（Prepare 会因 target 非空而拒绝），**不释放跳板**：
    // 卸载那一刻可能有游戏线程正在跳板里执行，VirtualFree 会把它脚下的内存收走
    // （见 engine-hook.h Abandon 的说明）。代价是重复安装每次漏 64 字节虚拟内存，
    // 换来的是"卸载后可以再次安装"这件事成立。
    void Disarm() { target=nullptr; }
};
