// engine-lua-tests.cpp - proves the call builder pushes the right arguments, restores the stack,
// and refuses to run without a verified build. Uses a fake lua_State and fake engine functions;
// no game and no GPU involved.
#include "engine-lua.h"
#include <stdio.h>
#include <string.h>

static int checks = 0, failures = 0;
static void Check(bool ok, const char* what) {
    ++checks;
    if (!ok) { ++failures; printf("FAIL %s\n", what); } else { printf("PASS %s\n", what); }
}

// A fake lua_State: only the two offsets the call builder touches exist.
struct FakeState { unsigned char* top; unsigned char* pad; unsigned char* base; unsigned char* pad2; unsigned char slot[256]; };
static FakeState fake;
static char pushed[8][64];
static int pushCount = 0;
static int execCount = 0;
static void* lastBase = nullptr;
static void* topAtExec = nullptr;
static double pushedNumbers[8] = {};
static int pushNumberCount = 0;
static char trace[16][64] = {};
static int traceCount = 0;

// 顺序追踪：字符串与数字都记进同一条轨迹，用来证明"动作名在前、参数按写下的顺序"。
static void TraceValue(const char* kind, const char* text) {
    if (traceCount < 16) snprintf(trace[traceCount], sizeof(trace[0]), "%s:%s", kind, text);
    ++traceCount;
}
static void FakePushString(void* L, const char* s) {
    if (pushCount < 8) { snprintf(pushed[pushCount], sizeof(pushed[0]), "%s", s); }
    ++pushCount;
    TraceValue("s", s == nullptr ? "" : s);
    unsigned char* top = *reinterpret_cast<unsigned char**>(static_cast<unsigned char*>(L) + EngineLua::OffTop);
    *reinterpret_cast<unsigned char**>(static_cast<unsigned char*>(L) + EngineLua::OffTop) = top + 16;
}
static void FakePushNumber(void* L, double v) {
    if (pushNumberCount < 8) pushedNumbers[pushNumberCount] = v;
    ++pushNumberCount;
    char text[48] = {};
    snprintf(text, sizeof(text), "%g", v);
    TraceValue("n", text);
    unsigned char* top = *reinterpret_cast<unsigned char**>(static_cast<unsigned char*>(L) + EngineLua::OffTop);
    *reinterpret_cast<unsigned char**>(static_cast<unsigned char*>(L) + EngineLua::OffTop) = top + 16;
}
static int FakeExecuteAction(void* L) {
    ++execCount;
    lastBase = *reinterpret_cast<void**>(static_cast<unsigned char*>(L) + EngineLua::OffBase);
    topAtExec = *reinterpret_cast<void**>(static_cast<unsigned char*>(L) + EngineLua::OffTop);
    return 0;
}
static void Reset() {
    pushCount = 0; execCount = 0; lastBase = nullptr; topAtExec = nullptr;
    pushNumberCount = 0; traceCount = 0;
    memset(pushed, 0, sizeof(pushed));
    memset(pushedNumbers, 0, sizeof(pushedNumbers));
    memset(trace, 0, sizeof(trace));
    EngineLua::nextExecuteAction = nullptr;   // 命令通道要求"已验证的引擎入口"，测试里默认没有
    // The probe section below gates on probeShots/probeReady, so a test that runs after
    // it must not inherit a non-zero shot counter (that would silently skip both branches).
    EngineLua::probeShots = 0; EngineLua::probeReady = 0;
    EngineLua::probeLine[0][0] = 0; EngineLua::probeLine[1][0] = 0;
    EngineLua::log = nullptr;
    fake.top = fake.slot; fake.base = fake.slot + 128;
    EngineLua::state = &fake;
    EngineLua::pushString = &FakePushString;
    EngineLua::executeAction = &FakeExecuteAction;
}

int wmain() {
    setvbuf(stdout, nullptr, _IONBF, 0);

    // Fails closed with nothing bound.
    EngineLua::state = nullptr; EngineLua::pushString = nullptr; EngineLua::executeAction = nullptr;
    Check(!EngineLua::Call(nullptr, 1), "no state: call is refused");
    Check(!EngineLua::Bind(nullptr), "no module base: bind is refused");
    Check(!EngineLua::InstallStateCapture(nullptr), "no module base: capture install is refused");

    Reset();
    Check(!EngineLua::Call(nullptr, 1), "null args with a live state: call is refused");

    Reset();
    const char* args[5] = {"CREATE_NAMED_ON_TEAM_AT_WAYPOINT", "hansummon_1", "AlliedAntiVehicleVehicleTech3",
                           "Player_1/teamPlayer_1", "Player_1_Start"};
    Check(EngineLua::Call(args, 5), "five-argument call succeeds");
    Check(pushCount == 5, "exactly five values were pushed");
    Check(execCount == 1, "the engine entry point ran exactly once");
    for (int i = 0; i < 5; ++i) {
        char what[64]; snprintf(what, sizeof(what), "argument %d matches", i + 1);
        Check(strcmp(pushed[i], args[i]) == 0, what);
    }
    Check(lastBase == reinterpret_cast<void*>(fake.slot), "base pointed at argument 1 during the call");
    Check(topAtExec == fake.slot + 5 * 16, "top was past the five arguments during the call");
    Check(fake.top == fake.slot, "top was restored afterwards");
    Check(fake.base == fake.slot + 128, "base was restored afterwards");

    Reset();
    Check(EngineLua::SummonAtWaypoint("CREATE_NAMED_ON_TEAM_AT_WAYPOINT", "n1", "unit", "team", "wp"),
          "waypoint helper succeeds");
    Check(pushCount == 5 && strcmp(pushed[0], "CREATE_NAMED_ON_TEAM_AT_WAYPOINT") == 0 &&
          strcmp(pushed[4], "wp") == 0, "waypoint helper pushes action name first, waypoint last");

    Reset();
    const char* bad[5] = {"A", "B", "C", "D", nullptr};
    Check(!EngineLua::Call(bad, 5), "a null parameter is refused");
    Check(pushCount == 0, "nothing was pushed for the refused call");
    Check(!EngineLua::Call(args, 0), "zero arguments is refused");

    Reset();
    EngineLua::state = &fake;
    fake.top = nullptr;
    Check(!EngineLua::Call(args, 5), "a state without a stack is refused");
    fake.top = fake.slot;

    // The pattern gate: garbage bytes must never be treated as a bindable build.
    BYTE junk[16] = {0xe9,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0};
    Check(!EngineLua::Matches(junk, EngineLua::PatternPushString, sizeof(EngineLua::PatternPushString)),
          "unrelated bytes fail the push-string pattern gate");
    Check(!EngineLua::Matches(junk, EngineLua::PatternExecuteAction, sizeof(EngineLua::PatternExecuteAction)),
          "unrelated bytes fail the ExecuteAction pattern gate");
    Check(!EngineLua::Matches(nullptr, EngineLua::PatternSetGlobal, sizeof(EngineLua::PatternSetGlobal)),
          "a null target fails the pattern gate");


    // Read-only probe: two snapshots, raw header words only, nothing dereferenced.
    {
        static char logged[512];
        static int loggedCount = 0;
        EngineLua::log = [](const char* s) { snprintf(logged, sizeof(logged), "%s", s); ++loggedCount; };
        unsigned char header[64];
        memset(header, 0xAB, sizeof(header));
        EngineLua::probeShots = 0; EngineLua::probeReady = 0;
        EngineLua::probeLine[0][0] = 0; EngineLua::probeLine[1][0] = 0;
        EngineLua::ProbeState(header);
        Check(EngineLua::probeReady == 1, "the first engine call takes snapshot 1");
        Check(strstr(EngineLua::probeLine[0], "n0=ABABABAB") != nullptr, "snapshot reports raw header words");
        Check(strstr(EngineLua::probeLine[0], "call#=1") != nullptr, "snapshot records which call it was");
        EngineLua::ProbeState(header);
        Check(EngineLua::probeLine[1][0] == 0, "snapshot 2 waits for the later sample");
        Check(EngineLua::probeReady == 1, "a later call does not overwrite snapshot 1");
        EngineLua::DrainProbe();
        Check(loggedCount == 1 && strstr(logged, "probe shot=1") != nullptr, "the captured snapshot reaches the log once");
        EngineLua::DrainProbe();
        Check(loggedCount == 1, "the same snapshot is never logged twice");
        EngineLua::log = nullptr;
    }

    // The second snapshot only exists on call #201, so drive the counter there explicitly:
    // without this the slot-1 branch (and its once-only drain) is never exercised.
    {
        static char logged2[512];
        static int logged2Count = 0;
        EngineLua::log = [](const char* s) { snprintf(logged2, sizeof(logged2), "%s", s); ++logged2Count; };
        unsigned char header2[64];
        memset(header2, 0xCD, sizeof(header2));
        EngineLua::probeShots = 200; EngineLua::probeReady = 0;
        EngineLua::probeLine[0][0] = 0; EngineLua::probeLine[1][0] = 0;
        EngineLua::DrainProbe();
        Check(logged2Count == 0, "a drain before the second sample logs nothing");
        EngineLua::ProbeState(header2);
        Check(EngineLua::probeReady == 2, "call #201 takes the second snapshot");
        Check(strstr(EngineLua::probeLine[1], "shot=2") != nullptr, "the second snapshot is labelled shot 2");
        Check(strstr(EngineLua::probeLine[1], "call#=201") != nullptr, "the second snapshot records call #201");
        Check(strstr(EngineLua::probeLine[1], "n0=CDCDCDCD") != nullptr, "the second snapshot reports raw header words");
        EngineLua::DrainProbe();
        Check(logged2Count == 1 && strstr(logged2, "probe shot=2") != nullptr, "the second snapshot reaches the log once");
        EngineLua::DrainProbe();
        Check(logged2Count == 1, "the second snapshot is never logged twice");
        EngineLua::log = nullptr;
    }

    // ---- 命令文件通道：解析（形状不对一律拒，绝不"顺手修正"）----
    {
        EngineLua::Command parsed = {};
        Check(EngineLua::ParseCommandLine("PLAYER_SET_MONEY Player_1 10000", &parsed),
              "a well-formed command line parses");
        Check(strcmp(parsed.name, "PLAYER_SET_MONEY") == 0, "the action name is taken verbatim");
        Check(parsed.argc == 2, "both arguments are captured");
        Check(strcmp(parsed.args[0], "Player_1") == 0 && strcmp(parsed.args[1], "10000") == 0,
              "argument order matches what was written");
        Check(EngineLua::ParseCommandLine("PLAYER_GIVE_MONEY\tPlayer_2   5000\n", &parsed),
              "tabs and repeated spaces separate fields");
        Check(parsed.argc == 2 && strcmp(parsed.args[1], "5000") == 0, "tab-separated arguments parse");
        Check(EngineLua::ParseCommandLine("CREATE_OBJECT 1 -2.5 3 # 注释", &parsed), "a trailing comment is allowed");
        Check(parsed.argc == 3, "the comment is not taken as an argument");
        Check(!EngineLua::ParseCommandLine("# 整行注释", &parsed), "a comment-only line is refused");
        Check(!EngineLua::ParseCommandLine("", &parsed), "an empty line is refused");
        Check(!EngineLua::ParseCommandLine("player_set_money 1", &parsed), "a lowercase name is refused");
        Check(!EngineLua::ParseCommandLine("9BAD 1", &parsed), "a name not starting with a letter is refused");
        Check(!EngineLua::ParseCommandLine("PLAYER-SET-MONEY 1", &parsed), "a name with punctuation is refused");
        Check(!EngineLua::ParseCommandLine("PLAYER_SET_MONEY 1 2 3 4 5 6 7", &parsed), "too many arguments is refused");
        Check(EngineLua::IsNumericToken("10000") && EngineLua::IsNumericToken("-2.5"),
              "plain numbers are recognised as numbers");
        Check(!EngineLua::IsNumericToken("Player_1") && !EngineLua::IsNumericToken("") &&
              !EngineLua::IsNumericToken("12abc"), "non-numbers stay strings");
    }

    // ---- 命令文件通道：执行时机与失败关闭 ----
    {
        Reset();
        EngineLua::pushNumber = &FakePushNumber;
        EngineLua::stagedToken = 0; EngineLua::consumedToken = 0;
        snprintf(EngineLua::staged.name, sizeof(EngineLua::staged.name), "%s", "PLAYER_SET_MONEY");
        snprintf(EngineLua::staged.args[0], sizeof(EngineLua::staged.args[0]), "%s", "Player_1");
        snprintf(EngineLua::staged.args[1], sizeof(EngineLua::staged.args[1]), "%s", "10000");
        EngineLua::staged.argc = 2;
        EngineLua::stagedToken = 1;
        // 夹具里 lua_State+0x10 就是 base 单元（32 位下正好是 slot 数组的头 4 字节）。
        // 真实运行中的状态它必然非空；夹具初值是 0 —— 先按 0 测"拒绝"，再模拟出活着的状态。
        void** baseCell = reinterpret_cast<void**>(reinterpret_cast<unsigned char*>(&fake) + EngineLua::OffBase);

        EngineLua::RunStagedCommand(nullptr);
        Check(execCount == 0 && pushCount == 0, "no state: the staged command runs nothing");

        Check(*baseCell == nullptr, "the fixture starts with a null base cell, as a dead state would");
        EngineLua::RunStagedCommand(&fake);
        Check(execCount == 0 && EngineLua::consumedToken == 0,
              "a state without a base: nothing runs and nothing is marked consumed");

        EngineLua::nextExecuteAction = &FakeExecuteAction;
        EngineLua::RunStagedCommand(&fake);
        Check(execCount == 0 && traceCount == 0,
              "without a verified engine entry point nothing runs");

        *baseCell = fake.slot + 128;               // 现在这是一个"活着"的状态
        EngineLua::RunStagedCommand(&fake);
        Check(execCount == 1, "the staged command reaches the engine entry point exactly once");
        Check(traceCount == 3 && strcmp(trace[0], "s:PLAYER_SET_MONEY") == 0, "the action name is pushed first");
        Check(traceCount == 3 && strcmp(trace[1], "s:Player_1") == 0, "a player name stays a string");
        Check(traceCount == 3 && strcmp(trace[2], "n:10000") == 0, "a numeric argument is pushed as a number");
        Check(lastBase == reinterpret_cast<void*>(fake.slot), "base pointed at the action name during the call");
        Check(topAtExec == fake.slot + 3 * 16, "top was past all three values during the call");
        Check(fake.top == fake.slot, "top was restored afterwards");
        Check(*baseCell == static_cast<void*>(fake.slot + 128), "the base cell was restored afterwards");
        Check(EngineLua::consumedToken == 1, "the token counts as consumed only after the call returned");

        EngineLua::RunStagedCommand(&fake);
        Check(execCount == 1 && traceCount == 3, "a consumed command never runs a second time");
        EngineLua::pushNumber = nullptr;
    }

    // ---- 命令文件通道：门控与增量消费（默认封闭）----
    {
        char cmdPath[MAX_PATH] = {}; char flagPath[MAX_PATH] = {};
        Check(EngineLua::CommandPath(cmdPath, sizeof(cmdPath), "ra3-engine-cmd.txt") &&
              EngineLua::CommandPath(flagPath, sizeof(flagPath), "ra3-engine-probe.flag"),
              "the command and gate paths resolve under the temp directory");
        DeleteFileA(flagPath);
        DeleteFileA(cmdPath);
        FILE* file = fopen(cmdPath, "wb");
        if (file != nullptr) { fputs("PLAYER_SET_MONEY Player_1 10000\n", file); fclose(file); }
        EngineLua::stagedToken = 0; EngineLua::consumedToken = 0; EngineLua::commandOffset = 0;
        EngineLua::busyLoggedToken = 0; EngineLua::stallLoggedToken = 0;
        EngineLua::staged.name[0] = 0;
        EngineLua::PollCommandFile();
        Check(EngineLua::stagedToken == 0 && EngineLua::staged.name[0] == 0,
              "with the gate file absent nothing is staged even though a command file exists");

        file = fopen(flagPath, "wb");
        if (file != nullptr) { fputs("1\n", file); fclose(file); }
        EngineLua::PollCommandFile();
        Check(EngineLua::stagedToken == 1, "with the gate open the pending line is staged");
        Check(strcmp(EngineLua::staged.name, "PLAYER_SET_MONEY") == 0 && EngineLua::staged.argc == 2,
              "the staged command carries the parsed name and arguments");

        EngineLua::PollCommandFile();
        Check(EngineLua::stagedToken == 1, "an unconsumed command is not replaced");

        EngineLua::consumedToken = 1;
        file = fopen(cmdPath, "ab");
        if (file != nullptr) { fputs("PLAYER_GIVE_MONEY Player_2 -5000\n", file); fclose(file); }
        EngineLua::PollCommandFile();
        Check(EngineLua::stagedToken == 2 && strcmp(EngineLua::staged.name, "PLAYER_GIVE_MONEY") == 0,
              "a line appended later is staged once the previous one is consumed");
        Check(EngineLua::staged.argc == 2 && strcmp(EngineLua::staged.args[1], "-5000") == 0,
              "the appended command keeps its negative argument");

        EngineLua::PollCommandFile();
        Check(EngineLua::stagedToken == 2 && EngineLua::consumedToken == 1, "no new line means no new command");

        DeleteFileA(cmdPath);
        DeleteFileA(flagPath);
        Check(GetFileAttributesA(flagPath) == INVALID_FILE_ATTRIBUTES,
              "the gate file is removed again so a real session is never left with the channel open");
        EngineLua::stagedToken = 0; EngineLua::consumedToken = 0; EngineLua::commandOffset = 0;
    }

    // ---- 特征码扫描路：把入口"搬家"到别的地址，Bind 仍应通过扫描找到它 ----
    {
        // 造一个最小 PE：MZ + e_lfanew=0x80；PE 头在 0x80；1 个段；可选头 0 字节 ⇒ 段表在 0x98
        static unsigned char module[0x4000] = {};
        memset(module, 0x90, sizeof(module));           // 用 nop 填满，避免出现偶发"巧合匹配"
        module[0] = 'M'; module[1] = 'Z';
        unsigned lfanew = 0x80;
        memcpy(module + 0x3C, &lfanew, 4);
        memcpy(module + 0x80, "PE\0\0", 4);
        unsigned short sections = 1, optSize = 0;
        memcpy(module + 0x86, &sections, 2);
        memcpy(module + 0x94, &optSize, 2);
        unsigned char* section = module + 0x98;
        memcpy(section, ".text\0\0\0", 8);
        unsigned virtualSize = 0x2000, virtualAddress = 0x1000;
        memcpy(section + 8, &virtualSize, 4);
        memcpy(section + 12, &virtualAddress, 4);

        // 把这五个特征码"搬家"到 .text 里，彼此隔开；地址与引擎里的 RVA 完全无关
        unsigned char* text = module + virtualAddress;
        unsigned offset = 0x100;
        memcpy(text + offset, EngineLua::PatternPushString, sizeof(EngineLua::PatternPushString)); offset += 0x200;
        memcpy(text + offset, EngineLua::PatternExecuteAction, sizeof(EngineLua::PatternExecuteAction)); offset += 0x200;
        memcpy(text + offset, EngineLua::PatternPushNumber, sizeof(EngineLua::PatternPushNumber)); offset += 0x200;
        memcpy(text + offset, EngineLua::PatternToStringScan, sizeof(EngineLua::PatternToStringScan)); offset += 0x200;
        memcpy(text + offset, EngineLua::PatternSetGlobal, sizeof(EngineLua::PatternSetGlobal));

        const AobScan::Range range = AobScan::TextSection(module);
        Check(range.ok && range.base == text && range.size >= virtualSize,
              "the fixture's .text range is read from its PE header");

        Reset();
        Check(AobScan::FindInText(module, EngineLua::PatternPushString, nullptr, sizeof(EngineLua::PatternPushString)) == text + 0x100,
              "a moved pushString entry point is found by scanning (not by address)");

        // Bind 全过：入口全部靠扫描定出来（合成模块里没有正确的 RVA）
        EngineLua::Bind(module);
        Check(strstr(EngineLua::bindLine, "pushString=scan") != nullptr &&
              strstr(EngineLua::bindLine, "executeAction=scan") != nullptr &&
              strstr(EngineLua::bindLine, "pushNumber=scan") != nullptr &&
              strstr(EngineLua::bindLine, "toString=scan") != nullptr,
              "every entry point is resolved by signature scanning when the addresses moved");
    }

    printf("RESULT engine-lua checks=%d failures=%d\n", checks, failures);
    return failures == 0 ? 0 : 1;
}
