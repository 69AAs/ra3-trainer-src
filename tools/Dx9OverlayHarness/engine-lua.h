// engine-lua.h - summon units through the engine's own script action path.
//
// Why: poking the feature flag from outside intermittently fails ("game did not resolve the unit
// type"), while map scripts that call ExecuteAction("CREATE_NAMED_ON_TEAM_AT_WAYPOINT", ...) always
// work. Static analysis of RA3 1.12 located that path inside the game image:
//
//   0x0040AA50  lua_pushstring(lua_State* L, const char* s)   - copies a full TValue onto L->top
//   0x00582C50  ExecuteAction(lua_State* L)                   - reads arg 1 as the action name and
//                                                               matches it against the 573-entry
//                                                               action table at 0x00CDD190
//   0x0040ACA0  lua_setglobal(lua_State* L, const char* name) - called by the launcher's Lua bridge
//                                                               at every map start: our state hook
//
// lua_State layout, from the engine's own instructions: top at +0x00, base at +0x10.
// Everything fails closed: an unknown byte pattern at any target means we never call it.
#pragma once
#include "code-hook.h"
#include "aob-scan.h"

namespace EngineLua {
    constexpr uintptr_t RvaPushString = 0xA980;      // 0x0040A980 - corrected from AUK233/RA3Hook
    constexpr uintptr_t RvaExecuteAction = 0x182C50; // 0x00582C50
    constexpr uintptr_t RvaSetGlobal = 0xACA0;       // 0x0040ACA0
    constexpr int OffTop = 0x00;
    constexpr int OffBase = 0x10;
    // TValue layout proven from the engine's own code (and from AUK233/RA3Hook's helpers):
    // 16 bytes per value, the value word first, the type tag in the low nibble at +8.
    constexpr int TValueSize = 0x10;
    // 引擎自己的 lua_tostring（0x40A790）读法：
    //   cmp dword [esi],3   → 类型标签在 TValue 的 +0
    //   mov eax,[esi+8]     → 值（指针/数字）在 +8
    //   shl eax,4 + lea [base+eax-0x10] → TValue=16 字节，index 1 == base + 0
    // 之前把标签读在 +8、值读在 +0，正好反了，所以永远读不到字符串。
    constexpr int TagOffset = 0x00;
    constexpr int ValueOffset = 0x08;

    // First bytes of each target in the 1.12 build; anything else is a build we do not understand.
    inline const BYTE PatternPushString[12] = {0x8b, 0x54, 0x24, 0x08, 0x85, 0xd2, 0x56, 0x75, 0x0e, 0x8b, 0x74, 0x24};   // lua_pushstring at RVA 0xA980 (verified in-game + against AUK233/RA3Hook)
    inline const BYTE PatternExecuteAction[12] = {0x8b,0x44,0x24,0x04,0x83,0xec,0x18,0x53,0x55,0x56,0x57,0x6a};
    // lua_tostring, also from AUK233/RA3Hook (Steam family) and verified against the game's own bytes.
    constexpr uintptr_t RvaToString = 0xA790;
    inline const BYTE PatternToString[12] = {0x8b,0x44,0x24,0x08,0x85,0xc0,0x8b,0x4c,0x24,0x04,0x56,0x7c};
    inline const BYTE PatternSetGlobal[12] = {0x8b,0x44,0x24,0x08,0x56,0x8b,0x74,0x24,0x08,0x57,0x8b,0x3e};

    inline void* state = nullptr;                       // captured lua_State
    inline void (*pushString)(void*, const char*) = nullptr;
    inline int (*executeAction)(void*) = nullptr;
    inline const char* (*toStringFn)(void*, int) = nullptr;   // lua_tostring
    // lua_pushnumber（RVA 0xA910）：字节 = fld qword [esp+8]; … fstp qword [eax+8]
    // ⇒ 存进 TValue 的 +8、标签置为 number —— 与从 lua_tostring 独立读出的 TValue 布局（标签@+0、值@+8）
    //   完全一致，所以这个地址是**被独立证实**过的，不再是"从偏移表推的"。
    constexpr uintptr_t RvaPushNumber = 0xA910;
    inline const BYTE PatternPushNumber[12] = {0xdd, 0x44, 0x24, 0x08, 0x56, 0x8b, 0x74, 0x24, 0x08, 0x8b, 0x06, 0xdd};
    // toString 的 12 字节前缀在 .text 里出现 3 次（不唯一）⇒ 扫描另用 64 字节版本（离线验证过唯一）。
    inline const BYTE PatternToStringScan[64] = {
        0x8b,0x44,0x24,0x08,0x85,0xc0,0x8b,0x4c,0x24,0x04,0x56,0x7c,0x12,0x8b,0x51,0x10,
        0xc1,0xe0,0x04,0x8d,0x74,0x02,0xf0,0x3b,0x31,0x72,0x0b,0x33,0xc0,0x5e,0xc3,0xc1,
        0xe0,0x04,0x03,0x01,0x8b,0xf0,0x85,0xf6,0x74,0xf1,0x83,0x3e,0x03,0x74,0x0e,0x56,
        0x51,0xe8,0x0a,0xc6,0xff,0xff,0x83,0xc4,0x08,0x85,0xc0,0x75,0xde,0x8b,0x46,0x08};
    inline void (*pushNumber)(void*, double) = nullptr;


    inline BYTE* at(void* moduleBase, uintptr_t rva) {
        return reinterpret_cast<BYTE*>(moduleBase) + rva;
    }

    // Read-only verification: the build must look exactly like the one we analysed.
    inline bool Matches(void* target, const BYTE* pattern, int size) {
        if (target == nullptr || pattern == nullptr || size <= 0) return false;
        BYTE got[64] = {};                       // 特征码最长 64 字节：超长一律拒绝，绝不能截断后"看起来通过"
        if (size > (int)sizeof(got)) return false;
        if (!CodeHook::Read(target, got, size)) return false;
        for (int i = 0; i < size; ++i) if (got[i] != pattern[i]) return false;
        return true;
    }

    // 每个入口是怎么定出来的（scan / rva / none），安装完成后写日志用
    inline char bindLine[320] = {};
    inline char bindNote[5][24] = {};

    // 定一个入口：**先按特征码扫描**（换个版本/加载器还能用），扫不到才回退到写死的 RVA（同样要用特征码校验）。
    // 两条路都要求"字节对得上" ⇒ 失败关闭，绝不把没验证过的地址交出去。
    inline void* ResolveEntry(void* moduleBase, uintptr_t rva, const BYTE* pattern, int size, bool useLongPattern, int slot) {
        const BYTE* scanPattern = pattern;
        int scanSize = size;
        if (useLongPattern) { scanPattern = PatternToStringScan; scanSize = (int)sizeof(PatternToStringScan); }
        void* scanned = AobScan::FindInText(moduleBase, scanPattern, nullptr, (size_t)scanSize);
        if (scanned != nullptr) {
            snprintf(bindNote[slot], sizeof(bindNote[slot]), "scan");
            return scanned;
        }
        void* legacy = at(moduleBase, rva);
        if (Matches(legacy, pattern, size)) {
            snprintf(bindNote[slot], sizeof(bindNote[slot]), "rva");
            return legacy;
        }
        snprintf(bindNote[slot], sizeof(bindNote[slot]), "none");
        return nullptr;
    }

    // Resolve the entry points from a module base. Does not touch any code.
    inline bool Bind(void* moduleBase) {
        if (moduleBase == nullptr) return false;
        void* push  = ResolveEntry(moduleBase, RvaPushString, PatternPushString, sizeof(PatternPushString), false, 0);
        void* exec  = ResolveEntry(moduleBase, RvaExecuteAction, PatternExecuteAction, sizeof(PatternExecuteAction), false, 1);
        void* num   = ResolveEntry(moduleBase, RvaPushNumber, PatternPushNumber, sizeof(PatternPushNumber), false, 2);
        void* tostr = ResolveEntry(moduleBase, RvaToString, PatternToString, sizeof(PatternToString), true, 3);
        snprintf(bindLine, sizeof(bindLine),
                 "osd-engine-lua bind pushString=%s executeAction=%s pushNumber=%s toString=%s setGlobal=%s",
                 bindNote[0], bindNote[1], bindNote[2], bindNote[3], bindNote[4]);
        if (push == nullptr || exec == nullptr || tostr == nullptr) return false;
        // pushNumber 缺失只降级（调用侧自己判空），但绝不能沿用上一次 Bind 留下的指针。
        pushNumber = (num != nullptr) ? reinterpret_cast<void (*)(void*, double)>(num) : nullptr;
        toStringFn = reinterpret_cast<const char* (*)(void*, int)>(tostr);
        pushString = reinterpret_cast<void (*)(void*, const char*)>(push);
        executeAction = reinterpret_cast<int (*)(void*)>(exec);
        return true;
    }

    // The state is delivered to us by the engine itself: the launcher's Lua bridge calls
    // lua_setglobal(L, name) at every map start, so one read-only hook there hands us L.
    inline CodeHook stateHook;
    inline void (*nextSetGlobal)(void*, const char*) = nullptr;
    inline volatile LONG captured = 0;

    inline void CaptureSetGlobal(void* L, const char* name) {
        if (L != nullptr && state == nullptr) {
            state = L;
            InterlockedIncrement(&captured);
        }
        if (nextSetGlobal != nullptr) nextSetGlobal(L, name);
    }

    inline bool InstallStateCapture(void* moduleBase) {
        if (moduleBase == nullptr || stateHook.installed || nextSetGlobal != nullptr) return false;
        void* target = ResolveEntry(moduleBase, RvaSetGlobal, PatternSetGlobal, sizeof(PatternSetGlobal), false, 4);
        if (target == nullptr) return false;
        if (!stateHook.Prepare(target, reinterpret_cast<void*>(&CaptureSetGlobal))) return false;
        nextSetGlobal = reinterpret_cast<void (*)(void*, const char*)>(stateHook.trampoline);
        if (!stateHook.Install() || !stateHook.Equals(stateHook.patch)) {
            stateHook.Restore();
            stateHook.Release();
            nextSetGlobal = nullptr;
            return false;
        }
        return true;
    }

    inline void UninstallStateCapture() {
        if (!stateHook.installed && nextSetGlobal == nullptr) return;
        stateHook.Restore();
        stateHook.Release();
        nextSetGlobal = nullptr;
        state = nullptr;
    }

    // Push the action name plus its parameters and run the engine's own ExecuteAction.
    // The stack is restored afterwards, so the game's Lua state is left exactly as we found it.
    inline bool Call(const char* const* args, int count) {
        if (state == nullptr || pushString == nullptr || executeAction == nullptr) return false;
        if (args == nullptr || count <= 0) return false;
        for (int i = 0; i < count; ++i) if (args[i] == nullptr) return false;

        BYTE* L = static_cast<BYTE*>(state);
        BYTE* savedTop = *reinterpret_cast<BYTE**>(L + OffTop);
        BYTE* savedBase = *reinterpret_cast<BYTE**>(L + OffBase);
        if (savedTop == nullptr) return false;

        for (int i = 0; i < count; ++i) pushString(L, args[i]);

        // A C function reads its arguments from L->base, which must point at argument 1.
        *reinterpret_cast<BYTE**>(L + OffBase) = savedTop;
        executeAction(L);

        // Drop both our arguments and whatever the call left behind.
        *reinterpret_cast<BYTE**>(L + OffBase) = savedBase;
        *reinterpret_cast<BYTE**>(L + OffTop) = savedTop;
        return true;
    }

    // CREATE_NAMED_ON_TEAM_AT_WAYPOINT <name> <unitType> <team> <waypoint>
    inline bool SummonAtWaypoint(const char* actionName, const char* objectName, const char* unitType,
                                 const char* team, const char* waypoint) {
        const char* args[5] = {actionName, objectName, unitType, team, waypoint};
        return Call(args, 5);
    }
}
namespace EngineLua {
    // ---------------------------------------------------------------- hooks, summon API, layout probe
    inline CodeHook actionTap;
    inline int (*nextExecuteAction)(void*) = nullptr;
    inline volatile LONG armed = 0;
    inline volatile LONG fired = 0;
    inline volatile LONG inOurCall = 0;
    inline DWORD captureTick = 0;
    inline DWORD firstTapTick = 0;
    inline char unitType[64] = {};

    inline void (*log)(const char*) = nullptr;   // set by the product overlay; never in tests
    inline void Note(const char* what) { if (log != nullptr) log(what); }

    inline void Arm(const char* unit) {
        if (unit == nullptr) return;
        int i = 0;
        for (; unit[i] != '\0' && i < 63; ++i) unitType[i] = unit[i];
        unitType[i] = '\0';
        InterlockedExchange(&armed, 1);
    }
    inline bool IsArmed() { return InterlockedCompareExchange(&armed, 1, 1) == 1; }
    inline bool HasFired() { return InterlockedCompareExchange(&fired, 1, 1) == 1; }


    // -------------------------------------------------------------- address correction (public source)
    // The open-source project AUK233/RA3Hook documents these Lua API offsets (Steam-family build):
    //   pushvalue 0xA4D0, tostring 0xA790, pushnil 0xA8B0, pushnumber 0xA910, pushstring 0xA980
    //   (Origin build: pushstring 0xA9A0).  This game matches the Steam family: its lua_tostring sits
    //   at 0xA790, which is the project's Steam value.  Our earlier 0xAA50 was a neighbouring
    //   pushfstring, which parses its argument as a format string - that is what crashed the game.
    constexpr uintptr_t RvaPushStringSteam = 0xA980;
    constexpr uintptr_t RvaPushStringOrigin = 0xA9A0;
    constexpr uintptr_t RvaPushStringOld = 0xAA50;
    constexpr uintptr_t RvaToStringSteam = 0xA790;

    inline char codeLine[360] = {};

    // Read-only dump of the four candidates so the correct one can be confirmed from the game itself.
    inline bool DumpCodeCandidates(void* moduleBase) {
        if (moduleBase == nullptr) return false;
        BYTE steam[16] = {}, origin[16] = {}, old[16] = {}, tostr[16] = {};
        if (!CodeHook::Read(at(moduleBase, RvaPushStringSteam), steam, sizeof(steam))) return false;
        CodeHook::Read(at(moduleBase, RvaPushStringOrigin), origin, sizeof(origin));
        CodeHook::Read(at(moduleBase, RvaPushStringOld), old, sizeof(old));
        CodeHook::Read(at(moduleBase, RvaToStringSteam), tostr, sizeof(tostr));
        snprintf(codeLine, sizeof(codeLine),
                 "osd-engine-lua code A980=%02x%02x%02x%02x%02x%02x A9A0=%02x%02x%02x%02x%02x%02x AA50=%02x%02x%02x%02x%02x%02x A790=%02x%02x%02x%02x%02x%02x",
                 steam[0], steam[1], steam[2], steam[3], steam[4], steam[5],
                 origin[0], origin[1], origin[2], origin[3], origin[4], origin[5],
                 old[0], old[1], old[2], old[3], old[4], old[5],
                 tostr[0], tostr[1], tostr[2], tostr[3], tostr[4], tostr[5]);
        return true;
    }


    // -------------------------------------------------------------- observe the actions the game calls
    // Read-only. The frame layout is no longer guessed: the game's own data (and AUK233/RA3Hook's
    // Lua_GetParameterCount) put top at L+0x00 and base at L+0x10, and Lua points base at the
    // *function* slot - so argument 1 is one TValue further on, at base + 0x10. Reading arg1 at base
    // is what made every sample look like "not a string" and is one of the two reasons the summon
    // attempt died inside the action-table walk.
    constexpr int MaxObserved = 60;
    inline volatile LONG callCount = 0;
    inline volatile LONG observedCount = 0;
    inline volatile LONG hotObservedCount = 0;
    inline char observed[MaxObserved][224] = {};
    inline volatile LONG observedReady = 0;

    // One argument of the current frame, formatted. Nothing is converted: lua_tostring is only used
    // after the slot's own type tag already says string, numbers are read out of the value word, and
    // every other type is reported by tag alone.
    // 读一个实参。字符串**不再自己解 TString 头部**（会读到对象头，全是乱码），
    // 而是先确认 tag == 4（LUA_TSTRING）再交给引擎自己的 lua_tostring —— 这种情况下它只取指针、
    // 不做任何类型转换，所以整个读过程依然是只读的。
    inline void FormatArgument(void* L, BYTE* base, long index, char* out, size_t cap) {
        BYTE* slot = base + (index - 1) * TValueSize;          // index 1 == base + 0
        const unsigned tag = *reinterpret_cast<const unsigned*>(slot) & 0xFF;
        if (tag == 4) {
            const char* text = toStringFn != nullptr ? toStringFn(L, (int)index) : nullptr;
            if (text == nullptr) { snprintf(out, cap, "<null>"); return; }
            snprintf(out, cap, "\"%s\"", text);
            return;
        }
        if (tag == 3) {
            const double number = *reinterpret_cast<const double*>(slot + ValueOffset);
            // Engine-controlled numbers can be NaN/Inf or outside long long's range, and
            // converting those is undefined behaviour: only take the integer path for values
            // the cast can represent, otherwise fall back to the floating-point form.
            if (number >= -9007199254740992.0 && number <= 9007199254740992.0) {
                const double whole = (double)(long long)number;
                if (number == whole) { snprintf(out, cap, "%lld", (long long)number); return; }
            }
            snprintf(out, cap, "%.3f", number);
            return;
        }
        snprintf(out, cap, "#t%u", tag);
    }

    inline bool HasStringArgument(BYTE* base, long argc) {
        for (long index = 1; index <= argc && index <= 6; ++index) {
            BYTE* slot = base + (index - 1) * TValueSize;
            if ((*reinterpret_cast<const unsigned*>(slot) & 0xFF) == 4) return true;
        }
        return false;
    }

    inline void ObserveAction(void* L) {
        if (L == nullptr) return;
        const LONG n = InterlockedIncrement(&callCount);
        BYTE* state = static_cast<BYTE*>(L);
        BYTE* base = *reinterpret_cast<BYTE**>(state + OffBase);
        BYTE* top = *reinterpret_cast<BYTE**>(state + OffTop);
        if (base == nullptr || top == nullptr || top <= base) return;
        const long argc = (long)((top - base) / TValueSize);

        // 采样策略：前 20 次全记；之后只记"带字符串参数"的调用（游戏自己的调用基本都是纯数字，
        // 实测一整局 200+ 次里一次带名字的都没有 —— 所以带字符串的才是稀罕、才有参考价值）。
        const bool sample = n <= 20;
        const bool interesting = !sample && HasStringArgument(base, argc);
        if (!sample && !interesting) return;
        const LONG slot = sample ? InterlockedIncrement(&observedCount) : InterlockedIncrement(&hotObservedCount);
        const int limit = sample ? 20 : (MaxObserved - 20);
        if (slot > limit) return;
        const int at = sample ? (int)(slot - 1) : 20 + (int)(slot - 1);

        char args[160] = {}; char one[80] = {};
        const long shown = argc < 5 ? argc : 5;
        for (long index = 1; index <= shown; ++index) {
            one[0] = '\0';
            FormatArgument(L, base, index, one, sizeof(one));
            if (one[0] == '\0') continue;
            if (args[0] != '\0') strncat(args, " ", sizeof(args) - strlen(args) - 1);
            strncat(args, one, sizeof(args) - strlen(args) - 1);
        }
        snprintf(observed[at], sizeof(observed[0]), "osd-engine-lua call#%ld argc=%ld args=(%s)", (long)n, argc, args);
        InterlockedExchange(&observedReady, 1);
    }

    // Called from the render thread.
    inline void DrainObserved() {
        static volatile LONG sent = 0;
        if (observedReady == 0 || sent != 0) return;
        for (int i = 0; i < MaxObserved; ++i)
            if (observed[i][0] != '\0') Note(observed[i]);
        Note("osd-engine-lua sampled-done");
        InterlockedExchange(&sent, 1);
    }


    // ---------------------------------------------------------------- read-only layout probe
    // Never dereferences what it reads, never writes to the state, never calls into the engine.
    // Two samples (the first ExecuteAction call and a much later one): whatever changes between them
    // is a stack pointer, which pins down the real offsets of top/base/ci.
    inline volatile LONG probeShots = 0;
    inline volatile LONG probeReady = 0;
    inline char probeLine[2][256] = {};

    inline void ProbeState(void* L) {
        const LONG shot = InterlockedIncrement(&probeShots);
        int slot = -1;
        if (shot == 1) slot = 0;
        else if (shot == 201) slot = 1;
        if (slot < 0 || L == nullptr) return;
        unsigned long v[8] = {};
        memcpy(v, L, sizeof(v));
        snprintf(probeLine[slot], sizeof(probeLine[slot]),
                 "osd-engine-lua probe shot=%ld call#=%ld L=%08lX n0=%08lX n1=%08lX n2=%08lX n3=%08lX n4=%08lX n5=%08lX n6=%08lX n7=%08lX",
                 (long)slot + 1, (long)shot, (unsigned long)(uintptr_t)L,
                 v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7]);
        InterlockedExchange(&probeReady, slot + 1);
    }

    // Called from the render thread so the engine thread never touches the product log.
    inline void DrainProbe() {
        static volatile LONG sent[2] = {};
        for (int slot = 0; slot < 2; ++slot) {
            if (probeLine[slot][0] != '\0' && sent[slot] == 0 &&
                InterlockedCompareExchange(&probeReady, slot + 1, slot + 1) == slot + 1) {
                InterlockedExchange(&sent[slot], 1);
                Note(probeLine[slot]);
            }
        }
    }


    // 读"引擎路请求"文件：修改器按下 F8 时写入一个递增序号。
    // 序号一变 = 用户按了一次 F8 —— 与 J（他自己的增援）完全解耦。
    inline bool ReadEngineRequest(int* sequence) {
        char path[MAX_PATH] = {};
        if (GetTempPathA(MAX_PATH, path) == 0) return false;
        strncat(path, "ra3-engine-request.txt", sizeof(path) - strlen(path) - 1);
        HANDLE file = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                                  OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return false;
        char buffer[32] = {};
        DWORD read = 0;
        const BOOL ok = ReadFile(file, buffer, sizeof(buffer) - 1, &read, nullptr);
        CloseHandle(file);
        if (!ok || read == 0) return false;
        const int value = atoi(buffer);
        if (value <= 0) return false;
        *sequence = value;
        return true;
    }

    // 读修改器导出的目标数据：x y z 单位类型码 失败槽地址。
    // 纯只读；读不到就返回 false，绝不阻塞或写入任何东西。
    inline bool ReadTargetCoordinate(char* out, size_t cap) {
        char path[MAX_PATH] = {};
        if (GetTempPathA(MAX_PATH, path) == 0) return false;
        strncat(path, "ra3-summon-target.txt", sizeof(path) - strlen(path) - 1);
        HANDLE file = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                                  OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return false;
        char buffer[160] = {};
        DWORD read = 0;
        const BOOL ok = ReadFile(file, buffer, sizeof(buffer) - 1, &read, nullptr);
        CloseHandle(file);
        if (!ok || read == 0) return false;
        double x = 0, y = 0, z = 0, unitType = 0, slot = 0, presetUnit = 0;
        if (sscanf(buffer, "%lf %lf %lf %lf %lf %lf", &x, &y, &z, &unitType, &slot, &presetUnit) < 3) return false;
        if (x == 0 && y == 0 && z == 0) return false;
        // 注意：这里必须把后面几个字段**原样带上**，之前只回传 4 个字段，
        // 导致"面板预设 UnitId"永远读成 0（日志里 unit= 一直是退回来的老值就是这个原因）。
        snprintf(out, cap, "%.3f %.3f %.3f %.0f %.0f %.0f", x, y, z, unitType, slot, presetUnit);
        return true;
    }

    // ---------------------------------------------------------------- 发兵（正式接入）
    // 只在一个"新鲜导出"上触发：修改器每跑一次坐标动作（用户按增援键）就会重写导出文件，
    // 里面是游戏自己算好的世界坐标 + 他当前选中单位的类型码。文件内容一变，就发一次。
    // 走的是已经被实测验证过的动作（数字类型码），不再有固定写死的形态、不会多出无主的兵。
    inline volatile LONG summonArmed = 1;
    inline volatile LONG summonCallsSeen = 0;
    inline const LONG summonFirstAt = 30;          // 等脚本引擎跑起来
    inline char lastTargetLine[160] = {};
    inline DWORD lastTargetTick = 0;
    inline int lastRequestSequence = 0;        // F8 请求序号（触发引擎路的唯一条件）
    inline DWORD lastRequestTick = 0;
    inline const DWORD summonCooldownMs = 5000;
    inline volatile LONG spawnCounter = 0;
    inline char lastAnchorName[40] = {};
    inline double exportedPresetUnit = 0;      // 修改器导出的"当前生效预设的 UnitId"
    inline char summonLine[220] = {};
    inline char followLine[220] = {};

    // 导出文件是不是"刚刚写的"（按一次增援才算新鲜；避免用上一局留下的旧值）
    inline bool TargetFileFresh(DWORD maxAgeMs) {
        char path[MAX_PATH] = {};
        if (GetTempPathA(MAX_PATH, path) == 0) return false;
        strncat(path, "ra3-summon-target.txt", sizeof(path) - strlen(path) - 1);
        WIN32_FILE_ATTRIBUTE_DATA info = {};
        if (!GetFileAttributesExA(path, GetFileExInfoStandard, &info)) return false;
        ULARGE_INTEGER written = {};
        written.LowPart = info.ftLastWriteTime.dwLowDateTime;
        written.HighPart = info.ftLastWriteTime.dwHighDateTime;
        FILETIME nowFile = {};
        GetSystemTimeAsFileTime(&nowFile);
        ULARGE_INTEGER now = {};
        now.LowPart = nowFile.dwLowDateTime;
        now.HighPart = nowFile.dwHighDateTime;
        if (now.QuadPart < written.QuadPart) return false;
        return (now.QuadPart - written.QuadPart) / 10000ULL <= maxAgeMs;   // FILETIME 单位 = 100ns
    }

    inline void TrySummon(void* L) {
        if (summonArmed == 0 || L == nullptr) return;
        if (pushString == nullptr || pushNumber == nullptr || nextExecuteAction == nullptr) return;
        const LONG seen = InterlockedIncrement(&summonCallsSeen);
        if (seen < summonFirstAt) return;

        // 触发 = 用户按了一次 F8（请求文件序号变化）。不再看导出文件是否新鲜、
        // 也不再依赖他按 J —— 那两个是"他的增援"，跟引擎路无关。
        int sequence = 0;
        if (!ReadEngineRequest(&sequence)) return;
        if (sequence == lastRequestSequence) return;
        const DWORD now = GetTickCount();
        if (lastRequestSequence != 0 && (now - lastRequestTick) < 300) return;   // 防抖
        lastRequestSequence = sequence;
        lastRequestTick = now;

        char exported[160] = {};
        const bool haveTarget = ReadTargetCoordinate(exported, sizeof(exported));   // 改钱不需要它

        // 这一发做什么，由请求序号决定（改钱不需要坐标，只有造兵才需要）。
        const LONG step = (lastRequestSequence - 1) % 4;
        double cx = 0, cy = 0, cz = 0, unitType = 0, ignored = 0, presetUnit = 0;
        char xs[24] = {}, ys[24] = {}, zs[24] = {};
        if (haveTarget &&
            sscanf(exported, "%lf %lf %lf %lf %lf %lf", &cx, &cy, &cz, &unitType, &ignored, &presetUnit) >= 4)
        {
            exportedPresetUnit = presetUnit;
            snprintf(xs, sizeof(xs), "%.2f", cx);
            snprintf(ys, sizeof(ys), "%.2f", cy);
            snprintf(zs, sizeof(zs), "%.2f", cz);
        }

        BYTE* state0 = static_cast<BYTE*>(L);
        BYTE** topPtr = reinterpret_cast<BYTE**>(state0 + OffTop);
        BYTE** basePtr = reinterpret_cast<BYTE**>(state0 + OffBase);
        BYTE* savedTop = *topPtr;
        BYTE* savedBase = *basePtr;
        if (savedTop == nullptr || savedBase == nullptr) return;

        double loggedUnit = 77777.0;   // the number actually pushed on this branch
        if (step == 1 || step == 2) {
            // 目标 C：给指定玩家送钱（动作名与参数格式离线从游戏文件读出）
            pushString(L, "PLAYER_SET_MONEY");
            pushString(L, step == 1 ? "Player_1" : "Player_2");
            pushNumber(L, loggedUnit);
        } else {
            // 造兵：已验证的本地坐标路（需要导出文件里的坐标与面板预设单位码）
            if (!haveTarget || (cx == 0 && cy == 0 && cz == 0)) return;
            pushString(L, "CREATE_OBJECT");
            loggedUnit = exportedPresetUnit != 0 ? exportedPresetUnit : 1703323034;
            pushNumber(L, loggedUnit);
            pushNumber(L, atof(xs));
            pushNumber(L, atof(ys));
            pushNumber(L, atof(zs));
        }
        *basePtr = savedTop;
        // 记录这一发**真正推给引擎**的单位码：之前这里写死 77777，造兵那一步的
        // unit= 永远是钱的数值，日志反而掩盖了实际用的预设单位码。
        snprintf(summonLine, sizeof(summonLine),
                 "osd-engine-lua summon-issued #%d mode=%s unit=%.0f target=%.0f coord=%s,%s,%s", lastRequestSequence,
                 step == 1 ? "set-money-self-77777"
                            : (step == 2 ? "set-money-player2-77777" : "local-mouse(preset-unit)"),
                 loggedUnit, 0.0, xs, ys, zs);
        Note(summonLine);
        const int result = nextExecuteAction(L);
        *basePtr = savedBase;
        *topPtr = savedTop;
        snprintf(followLine, sizeof(followLine), "osd-engine-lua summon-completed #%ld result=%d", (long)lastRequestSequence, result);
        Note(followLine);
    }

    // ---------------------------------------------------------------- 命令文件通道（诊断/验证用）
    // 为什么需要它：整条引擎路原来只有一个扳机 —— 请求文件里的序号变化（人工按键）。
    // 这条通道把扳机换成「写一行文件」：用户只要开一局挂着，剩下的全自动，且不碰他的键鼠。
    //
    // 线程纪律（照抄本项目已验证的读法，不自己发明）：
    //   * 读文件 / 解析 / 暂存：只在工作线程（CommandThreadProc）。
    //   * **调引擎：只在 tap 里**（渲染/脚本线程），且发生在游戏自己那次 ExecuteAction
    //     完整返回之后 —— 与已验证的发兵路同一上下文，不会重入它的查表循环。
    //   * 工作线程绝不碰产品日志（日志只在渲染线程写）：诊断行经 PublishCommandNote 发布、
    //     由渲染线程 DrainCommandNote 取走再 Note。
    //   * 工作线程绝不碰 D3D 设备（设备只允许创建线程碰，见 thread-policy.h）。
    // 门控：只有 %TEMP%\ra3-engine-probe.flag 存在时才动作 ⇒ 默认是死代码，产品不开口子。
    inline const int CommandMaxArgs = 6;

    struct Command {
        char name[64];
        char args[CommandMaxArgs][48];
        int argc;
    };

    inline Command staged = {};
    inline volatile LONG stagedToken = 0;      // 工作线程发布
    inline volatile LONG consumedToken = 0;    // tap 线程消费后设置
    inline volatile LONG stagedTick = 0;
    inline volatile LONG commandOffset = 0;    // 已消费到文件里的哪个字节
    inline volatile LONG presentFrames = 0;    // Present 每帧 +1：工作线程靠它判断渲染是否还活着
    inline volatile LONG pollCount = 0;
    inline volatile LONG busyLoggedToken = 0;
    inline volatile LONG stallLoggedToken = 0;
    inline volatile LONG commandThreadStarted = 0;
    inline char commandIssued[200] = {};
    inline char commandCompleted[200] = {};
    inline char commandNotes[2][200] = {};
    inline volatile LONG commandNoteSlot = -1;

    inline void PublishCommandNote(const char* line) {          // 工作线程
        if (line == nullptr) return;
        static volatile LONG flip = 0;
        const LONG slot = (InterlockedIncrement(&flip) - 1) & 1;
        snprintf(commandNotes[slot], sizeof(commandNotes[0]), "%s", line);
        InterlockedExchange(&commandNoteSlot, slot);
    }

    inline void DrainCommandNote() {                            // 渲染线程
        const LONG slot = InterlockedExchange(&commandNoteSlot, -1);
        if (slot >= 0 && slot <= 1) Note(commandNotes[slot]);
    }

    // 合法动作名形状 = 与动作表里的名字同一形状（全大写字母数字下划线）⇒ 形状不对直接拒，不猜。
    inline bool IsActionName(const char* s) {
        if (s == nullptr) return false;
        const size_t n = strlen(s);
        if (n < 3 || n > 47) return false;
        if (!(s[0] >= 'A' && s[0] <= 'Z')) return false;
        for (size_t i = 0; i < n; ++i) {
            const char c = s[i];
            if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')) return false;
        }
        return true;
    }

    // 解析一行：`动作名 参数1 参数2 ...`；'#' 起注释；制表符与连续空格都算分隔符。
    // 空行 / 注释 / 名字非法 / 参数太多或太长 ⇒ false（调用方什么都不做，绝不"顺手修正"）。
    inline bool ParseCommandLine(const char* line, Command* out) {
        if (line == nullptr || out == nullptr) return false;
        out->name[0] = '\0';
        out->argc = 0;
        char buffer[400] = {};
        size_t used = 0;
        for (const char* p = line; *p != '\0' && *p != '\n' && *p != '\r' && *p != '#'; ++p) {
            if (used + 1 >= sizeof(buffer)) return false;
            buffer[used++] = *p;
        }
        buffer[used] = '\0';
        char* cursor = buffer;
        bool first = true;
        while (*cursor != '\0') {
            while (*cursor == ' ' || *cursor == '\t') ++cursor;
            if (*cursor == '\0') break;
            char* start = cursor;
            while (*cursor != '\0' && *cursor != ' ' && *cursor != '\t') ++cursor;
            const char saved = *cursor;
            *cursor = '\0';
            if (first) {
                if (!IsActionName(start)) return false;
                snprintf(out->name, sizeof(out->name), "%s", start);
                first = false;
            } else {
                if (out->argc >= CommandMaxArgs) return false;
                const size_t len = strlen(start);
                if (len == 0 || len >= sizeof(out->args[0])) return false;
                for (size_t i = 0; i < len; ++i) {
                    const char c = start[i];
                    if (c < 0x20 || c == 0x7f) return false;      // 含不可打印字符 ⇒ 拒
                }
                snprintf(out->args[out->argc], sizeof(out->args[0]), "%s", start);
                ++out->argc;
            }
            if (saved == '\0') break;
            *cursor = saved;
            ++cursor;
        }
        return !first;                                             // 至少要有名字
    }

    // 能当数字就当数字：引擎自己的调用就是这么区分的（玩家名是 string，数值是 number）。
    inline bool IsNumericToken(const char* s) {
        if (s == nullptr || *s == '\0') return false;
        char* end = nullptr;
        const double value = strtod(s, &end);
        (void)value;
        return end != nullptr && end != s && *end == '\0';
    }

    inline bool CommandGateOpen() {                                 // 门控文件在不在
        char path[MAX_PATH] = {};
        if (GetTempPathA(MAX_PATH, path) == 0) return false;
        strncat(path, "ra3-engine-probe.flag", sizeof(path) - strlen(path) - 1);
        return GetFileAttributesA(path) != INVALID_FILE_ATTRIBUTES;
    }

    inline bool CommandPath(char* path, size_t cap, const char* leaf) {
        char temp[MAX_PATH] = {};
        if (GetTempPathA(MAX_PATH, temp) == 0) return false;
        snprintf(path, cap, "%s%s", temp, leaf);
        return true;
    }

    // 工作线程入口。只碰文件系统：读一行 → 解析 → 暂存 → 发布诊断行。
    inline void PollCommandFile() {
        InterlockedIncrement(&pollCount);
        if (!CommandGateOpen()) return;
        if (stagedToken != consumedToken) {                          // 上一条还没执行 ⇒ 不消费新行（背压）
            const DWORD now = GetTickCount();
            if (stagedToken != busyLoggedToken) {
                busyLoggedToken = stagedToken;
                PublishCommandNote("osd-engine-cmd busy awaiting-staged-execution");
            }
            if (now - (DWORD)stagedTick > 5000 && stagedToken != stallLoggedToken) {
                stallLoggedToken = stagedToken;
                char line[200] = {};
                snprintf(line, sizeof(line), "osd-engine-cmd stalled token=%ld age=%lums present-frames=%ld",
                         (long)stagedToken, (unsigned long)(now - (DWORD)stagedTick), (long)presentFrames);
                PublishCommandNote(line);
            }
            return;
        }
        char path[MAX_PATH] = {};
        if (!CommandPath(path, sizeof(path), "ra3-engine-cmd.txt")) return;
        HANDLE file = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                                  OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return;
        LARGE_INTEGER size = {};
        if (!GetFileSizeEx(file, &size)) { CloseHandle(file); return; }
        LONG offset = commandOffset;
        if (offset < 0 || (long long)offset > size.QuadPart) offset = 0;   // 文件被重写 ⇒ 从头读
        LARGE_INTEGER start = {};
        start.QuadPart = offset;
        if (!SetFilePointerEx(file, start, nullptr, FILE_BEGIN)) { CloseHandle(file); return; }
        char buffer[1024] = {};
        DWORD read = 0;
        const BOOL ok = ReadFile(file, buffer, sizeof(buffer) - 1, &read, nullptr);
        CloseHandle(file);
        if (!ok || read == 0) return;
        buffer[read] = '\0';
        const char* newline = strchr(buffer, '\n');
        if (newline == nullptr) return;                              // 半行 ⇒ 等写方补换行
        const size_t lineLength = (size_t)(newline - buffer);
        char line[400] = {};
        const size_t copy = lineLength < sizeof(line) - 1 ? lineLength : sizeof(line) - 1;
        memcpy(line, buffer, copy);
        line[copy] = '\0';
        commandOffset = offset + (LONG)lineLength + 1;                // 消费掉这一行（含换行）
        Command parsed = {};
        if (!ParseCommandLine(line, &parsed)) return;
        staged = parsed;
        InterlockedIncrement(&stagedToken);
        stagedTick = (LONG)GetTickCount();
        char note[200] = {};
        snprintf(note, sizeof(note), "osd-engine-cmd staged token=%ld name=%s argc=%d",
                 (long)stagedToken, staged.name, staged.argc);
        PublishCommandNote(note);
    }

    // 渲染/脚本线程：把暂存的命令推给引擎。与发兵路同一套栈处理。
    inline void RunStagedCommand(void* L) {
        const LONG token = stagedToken;
        if (token == 0 || token == consumedToken) return;
        if (L == nullptr || pushString == nullptr || pushNumber == nullptr || nextExecuteAction == nullptr) return;
        BYTE* state0 = static_cast<BYTE*>(L);
        BYTE** topPtr = reinterpret_cast<BYTE**>(state0 + OffTop);
        BYTE** basePtr = reinterpret_cast<BYTE**>(state0 + OffBase);
        BYTE* savedTop = *topPtr;
        BYTE* savedBase = *basePtr;
        if (savedTop == nullptr || savedBase == nullptr) return;

        pushString(L, staged.name);
        for (int i = 0; i < staged.argc; ++i) {
            if (IsNumericToken(staged.args[i])) pushNumber(L, atof(staged.args[i]));
            else pushString(L, staged.args[i]);
        }
        *basePtr = savedTop;
        snprintf(commandIssued, sizeof(commandIssued), "osd-engine-cmd issued token=%ld name=%s argc=%d",
                 (long)token, staged.name, staged.argc);
        Note(commandIssued);
        const int result = nextExecuteAction(L);
        *basePtr = savedBase;
        *topPtr = savedTop;
        snprintf(commandCompleted, sizeof(commandCompleted), "osd-engine-cmd completed token=%ld result=%d",
                 (long)token, result);
        Note(commandCompleted);
        InterlockedExchange(&consumedToken, token);
    }

    // 工作线程本体：150ms 一轮，只做 PollCommandFile。线程只创建一次。
    inline DWORD WINAPI CommandThreadProc(void*) {
        for (;;) {
            PollCommandFile();
            Sleep(150);
        }
    }

    inline void StartCommandThread() {
        if (InterlockedCompareExchange(&commandThreadStarted, 1, 0) != 0) return;
        HANDLE thread = CreateThread(nullptr, 0, CommandThreadProc, nullptr, 0, nullptr);
        if (thread != nullptr) CloseHandle(thread);
    }

    // ---------------------------------------------------------------- the tap
    // Entered by the VM on every ExecuteAction call. The game's own call runs first and completes.
    // This build then takes a read-only snapshot and does nothing else - no summon, no writes.
    inline int ActionTap(void* L) {
        ObserveAction(L);          // entry: base points at argument 1, so the action name is readable
        state = L;
        if (firstTapTick == 0) firstTapTick = GetTickCount();
        const int results = nextExecuteAction != nullptr ? nextExecuteAction(L) : 0;
        ProbeState(L);
        TrySummon(L);                  // 引擎这次调用已经完整返回，此刻发兵不会重入它的查表循环
        RunStagedCommand(L);           // 同上：命令通道走同一个"已经返回"的时刻，线程纪律与发兵路一致
        return results;
    }

    inline bool InstallActionTap(void* moduleBase) {
        if (moduleBase == nullptr || actionTap.installed || nextExecuteAction != nullptr) return false;
        void* target = at(moduleBase, RvaExecuteAction);
        if (!Matches(target, PatternExecuteAction, sizeof(PatternExecuteAction))) return false;
        if (!actionTap.Prepare(target, reinterpret_cast<void*>(&ActionTap))) return false;
        nextExecuteAction = reinterpret_cast<int (*)(void*)>(actionTap.trampoline);
        if (!actionTap.Install() || !actionTap.Equals(actionTap.patch)) {
            actionTap.Restore();
            actionTap.Release();
            nextExecuteAction = nullptr;
            return false;
        }
        return true;
    }

    inline void UninstallActionTap() {
        if (!actionTap.installed && nextExecuteAction == nullptr) return;
        actionTap.Restore();
        actionTap.Release();
        nextExecuteAction = nullptr;
    }
}
