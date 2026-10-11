// engine-hook-tests.cpp - proves the probe is read-only, keeps the original working and
// fails closed. Runs entirely in-process against a local helper; no game needed.
#include "engine-hook.h"
#include <stdio.h>

static int checks = 0, failures = 0;
static void Check(bool ok, const char* what) {
    ++checks;
    if (!ok) { ++failures; printf("FAIL %s\n", what); } else { printf("PASS %s\n", what); }
}

// Stand-in for the engine's register helper: same cdecl-ish shape (args pushed, caller cleans).
static int dummyCalls = 0;
static uintptr_t dummyLastB = 0;
static uintptr_t dummyLastC = 0;
extern "C" __attribute__((noinline)) uintptr_t DummyHelper(uintptr_t a, uintptr_t b, uintptr_t c, uintptr_t d) {
    ++dummyCalls;
    dummyLastB = b; dummyLastC = c;
    return a + b + c + d;
}

// A second helper used to pin the probe's first-byte rule. Whether its prologue is
// actually an untouchable shape is compiler-dependent (-O2 emits "mov eax,7; ret" for
// "return 7"), so the test below derives the expected outcome from the real byte.
extern "C" __attribute__((noinline)) uintptr_t OddHelper(uintptr_t, uintptr_t, uintptr_t, uintptr_t) { return 7; }

typedef uintptr_t (*Entry4)(uintptr_t,uintptr_t,uintptr_t,uintptr_t);
// volatile: force a real indirect call through the patched entry instead of letting -O2 inline it
static Entry4 volatile dummyEntry = &DummyHelper;
static Entry4 volatile oddEntry = &OddHelper;

int wmain() {
    setvbuf(stdout,nullptr,_IONBF,0);
    BYTE first = 0;
    Check(CodeHook::Read(reinterpret_cast<void*>(&DummyHelper), &first, 1), "reads the helper's first byte");
    Check(first != 0xe9 && first != 0xcc && first != 0x00, "helper prologue is hookable by the probe's rule");

    Check(EngineProbe::Install(reinterpret_cast<void*>(&DummyHelper)), "Install succeeds on a normal helper");
    Check(EngineProbe::target == reinterpret_cast<void*>(&DummyHelper), "Install records the target");

    const uintptr_t result = dummyEntry(1, 2, 3, 4);
    Check(result == 10, "the original still runs through the trampoline (sum preserved)");
    Check(dummyCalls == 1, "the original ran exactly once");
    Check(dummyLastB == 2 && dummyLastC == 3, "the original saw its own arguments");
    Check(EngineProbe::captured == 1, "the probe captured exactly one call");

    EngineProbe::Capture got[4] = {};
    const int taken = EngineProbe::Drain(got, 4);
    Check(taken == 1, "Drain returns the captured call");
    Check(got[0].name == 1 && got[0].arg0 == 2 && got[0].arg1 == 3 && got[0].arg2 == 4,
          "captured arguments match what the caller passed");
    Check(got[0].caller != 0, "captured the caller address");
    Check(EngineProbe::Drain(got, 4) == 0, "Drain clears the mailbox");

    Check(EngineProbe::Install(reinterpret_cast<void*>(&DummyHelper)) == false, "second Install is refused");

    EngineProbe::Uninstall();
    Check(EngineProbe::target == nullptr, "Uninstall clears the target");
    const uintptr_t after = dummyEntry(5, 6, 7, 8);
    Check(after == 26, "the original works after Uninstall");
    Check(EngineProbe::captured == 1, "no capture happens once uninstalled");

    Check(EngineProbe::Install(nullptr) == false, "Install refuses a null target");
    // Pin the outcome instead of accepting either: the probe's rule is exactly
    // "first byte 0xe9/0xcc/0x00 -> refuse". Read that byte ourselves and require
    // Install to agree with it, then prove the helper still runs when it accepts.
    BYTE oddFirst = 0;
    const bool oddReadable = CodeHook::Read(reinterpret_cast<void*>(&OddHelper), &oddFirst, 1);
    const bool oddUntouchable = oddReadable && (oddFirst == 0xe9 || oddFirst == 0xcc || oddFirst == 0x00);
    const bool oddInstalled = EngineProbe::Install(reinterpret_cast<void*>(oddEntry));
    Check(oddInstalled != oddUntouchable, "Install agrees with the probe's own first-byte rule");
    // 比的是"实际传进去的那个函数地址"：oddEntry 是 volatile 变量（防止 -O2 内联），
    // 直接对它取址得到的是 volatile 指针，reinterpret_cast 到 void* 会因去掉限定符编不过。
    const Entry4 oddTarget = oddEntry;
    Check(oddInstalled ? EngineProbe::target == reinterpret_cast<void*>(oddTarget) : EngineProbe::target == nullptr,
          "Install records the odd helper target exactly when it accepts it");
    if (oddInstalled) Check(oddEntry(1, 1, 1, 1) == 7, "an accepted odd helper still returns its own value");
    EngineProbe::Uninstall();

    printf("RESULT engine-probe checks=%d failures=%d\n", checks, failures);
    return failures == 0 ? 0 : 1;
}
