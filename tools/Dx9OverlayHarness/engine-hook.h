// engine-hook.h - read-only probe for the engine's own script/Lua API registration.
//
// Why: our reinforcement path pokes a feature flag and lets the game do the rest, which
// fails intermittently ("game did not resolve the unit type", possibly a partial spawn).
// The engine has an official path -- the map scripts call ExecuteAction("CREATE_NAMED_ON_TEAM_AT_WAYPOINT", ...)
// -- and static analysis located the single place where the engine binds that name into its
// own Lua state (game module, VA 0x005B078C pushes the string, 0x0040ACA0 is the register helper).
//
// This probe watches that helper: it records the arguments of every call so a single game
// session tells us the exact call convention (state pointer / function pointer / name) before
// the host ever calls the engine. It never changes behaviour: the trampoline always runs, the
// return value is passed through, and Install() fails closed on an unknown build.
#pragma once
#include "code-hook.h"

namespace EngineProbe {
    struct Capture { uintptr_t name, arg0, arg1, arg2, arg3, caller; };
    constexpr int Capacity = 64;

    struct Mailbox {
        volatile LONG head;
        Capture items[Capacity];
    };

    inline Mailbox* box = nullptr;
    inline CodeHook hook;
    inline void* target = nullptr;
    inline int captured = 0;

    // The register helper is cdecl-like (arguments pushed right to left, caller cleans up):
    //   push <name string>; push <state>; call helper
    // We take four machine words so the caller's cleanup stays correct for the arity we expect,
    // capture what we saw, and always continue into the trampoline.
    inline uintptr_t (*next)(uintptr_t, uintptr_t, uintptr_t, uintptr_t) = nullptr;

    inline uintptr_t CaptureCall(uintptr_t a0, uintptr_t a1, uintptr_t a2, uintptr_t a3) {
        if (box) {
            const LONG slot = InterlockedIncrement(&box->head) - 1;
            if (slot >= 0 && slot < Capacity) {
                Capture c{};
                c.name = a0; c.arg0 = a1; c.arg1 = a2; c.arg2 = a3;
                c.caller = reinterpret_cast<uintptr_t>(__builtin_return_address(0));
                box->items[slot] = c;
            }
            ++captured;
        }

        return next ? next(a0, a1, a2, a3) : 0;
    }

    // Fails closed: an unknown byte pattern at the target, a busy mailbox or a rejected hook
    // all leave the game untouched and report false to the caller.
    // 卸载与失败收尾统一走这里：还原原始字节（新调用立刻回到原路），但**不释放跳板、不清 next**。
    // 这是被评审确认过的 use-after-free 窗口：受钩的是游戏自己会调用的引擎助手，
    // 卸载时游戏线程可能正好已经在跳板里（CaptureCall 已进入、正要 next(...)）。
    // 原来的顺序 Restore → Release → next=nullptr 会在那条线程脚下把 64 字节还回去。
    // 我们挂不起游戏线程，所以宁可每次安装漏 64 字节虚拟内存，换"卸载期间绝不踩空"。
    // next 留着指向仍然有效的跳板是安全的：Restore 之后原序言已还原，不再有新调用进来。
    inline void Abandon() {
        hook.Restore();
        hook.Disarm();   // 允许之后重新安装（跳板仍然按上面的理由不释放）
        target = nullptr;
    }

    inline bool Install(void* helper) {
        if (helper == nullptr || target != nullptr) return false;
        if (box == nullptr) box = static_cast<Mailbox*>(VirtualAlloc(nullptr, sizeof(Mailbox), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
        if (box == nullptr) return false;
        box->head = 0;

        BYTE probe[8] = {};
        if (!CodeHook::Read(helper, probe, sizeof(probe))) return false;
        // The register helper starts with a normal prologue; anything else means a build we do
        // not understand, so we refuse instead of patching blind.
        if (probe[0] == 0xe9 || probe[0] == 0xcc || probe[0] == 0x00) return false;

        if (!hook.Prepare(helper, reinterpret_cast<void*>(&CaptureCall))) return false;
        next = reinterpret_cast<uintptr_t (*)(uintptr_t, uintptr_t, uintptr_t, uintptr_t)>(hook.trampoline);
        if (!hook.Install()) {
            Abandon();
            return false;
        }
        if (!hook.Equals(hook.patch)) {
            Abandon();
            return false;
        }

        target = helper;
        return true;
    }

    inline void Uninstall() {
        if (target == nullptr) return;
        Abandon();
    }

    // Host-side read-out. Copies what was captured and clears the mailbox.
    inline int Drain(Capture* out, int max) {
        if (box == nullptr || out == nullptr || max <= 0) return 0;
        LONG taken = box->head;
        if (taken > Capacity) taken = Capacity;
        if (taken > max) taken = max;
        for (LONG i = 0; i < taken; ++i) out[i] = box->items[i];
        box->head = 0;
        return static_cast<int>(taken);
    }
}
