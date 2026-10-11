using Iced.Intel;
using Ra3Trainer.Core.Codegen;
using Ra3Trainer.Core.Memory;
using Ra3Trainer.Core.Patching;
using Ra3Trainer.Core.Features;
using Xunit;

namespace Ra3Trainer.Tests;

/// <summary>执行实际编码字节的有界小型解释器；没有进程 API，也不执行本机/远程机器码。</summary>
public sealed class RemoteComponentGuardTests
{
    [Fact]
    public void EncodedCopyResolvesHeadTemplateCodeWithoutGlobalSelectionOrNext()
    {
        var code = new BootstrapCodeBuilder().Build(TestAssets.ReadBootstrapLines(), BootstrapCodeBuilderTests.FullScriptContext());
        var machine = new Machine(code.MustCode2, 0x720000) { Start = 0x720C00 };
        foreach (var (address, value) in new (uint, uint)[]
        {
            (0x700000,0xE0000), (0xCDB73C,0x10000), (0x10050,0x20000),
            (0x20008,0x30000), (0x30138,0x40000), (0x40004,0xD0000),
            (0xD0008,ReinforcementSettings.DefaultUnitId)
        }) machine.Memory[address] = value;
        var calls = new List<uint>();
        machine.OnCall = (m, address) =>
        {
            calls.Add(address);
            if (address == 0x710A60) Assert.Equal(ReinforcementSettings.DefaultUnitId, m.Registers[Register.EAX]);
            else Assert.Equal(0x710AA0u, address);
        };
        machine.Run();
        Assert.Equal(new uint[] { 0x710A60, 0x710AA0 }, calls);
        Assert.DoesNotContain(0xCE9838u, machine.Reads);
        Assert.DoesNotContain(0x20000u, machine.Reads);
        Assert.DoesNotContain(0x10054u, machine.Reads);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(20, 20)]
    [InlineData(50, 50)]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(51, 50)]
    [InlineData(int.MaxValue, 50)]
    public void EncodedReinforcementLoopUsesConfiguredQuantityAndClamps(int quantity, int expected)
    {
        var code = new BootstrapCodeBuilder().Build(TestAssets.ReadBootstrapLines(), BootstrapCodeBuilderTests.FullScriptContext());
        var machine = new Machine(code.MustCode2, 0x720000) { Start = 0x720B00 };
        machine.Memory[0x700000] = 0xD0000;
        machine.Memory[0x700124] = ReinforcementSettings.DefaultUnitId;
        machine.Memory[0x700128] = unchecked((uint)quantity);
        machine.Memory[0xA03CC] = 0xB0000;
        var creates = 0; var ranks = 0;
        machine.OnCall = (m, address) =>
        {
            switch (address)
            {
                case 0x710A60: m.Registers[Register.EAX] = 0x60000; break;
                case 0x710AA0: creates++; m.Registers[Register.EAX] = 0xA0000; break;
                case 0x710B00: break;
                case 0x710800: ranks++; break;
                default: throw new InvalidOperationException("Unexpected helper");
            }
        };
        machine.Run();
        Assert.Equal(expected, creates);
        Assert.Equal(expected, ranks);
        Assert.Equal(0u, machine.Memory[0x711100]);
        Assert.Equal(0u, machine.Memory[0x70014C]);
        Assert.True(machine.Returned);
        Assert.Equal(ReinforcementSettings.MaxCount, EmpiricalSafetyBounds.MaximumReinforcementCount);
    }

    [Theory]
    [InlineData(0x000)]
    [InlineData(0x100)]
    [InlineData(0x200)]
    [InlineData(0x300)]
    [InlineData(0x400)]
    [InlineData(0x500)]
    [InlineData(0x600)]
    [InlineData(0x700)]
    [InlineData(0x800)]
    [InlineData(0x900)]
    public void EncodedSelectionActionsUseOnlyHeadWithLargeCount(int offset)
    {
        var code = new BootstrapCodeBuilder().Build(TestAssets.ReadBootstrapLines(), BootstrapCodeBuilderTests.FullScriptContext());
        var machine = new Machine(code.MustCode2, 0x720000) { Start = 0x720000u + (uint)offset };
        foreach (var (address, value) in new (uint, uint)[]
        {
            (0xCDB73C,0x10000), (0x1005C,1806), (0x10050,0x20000),
            (0x20008,0x30000), (0x30138,0x40000), (0x40004,0xD0000),
            (0x4033C,0x90000), (0x90010,0x42C80000), (0x403CC,0xA0000),
            (0x40374,0x50000), (0x50200,0x60000), (0x60000,0x70000),
            (0x60004,0x80000), (0x80018,0), (0x70008,0x42C80000),
            (0x70040,0x42C80000), (0xD0008,ReinforcementSettings.DefaultUnitId), (0x700000,0xE0000)
        }) machine.Memory[address] = value;
        var calls = 0;
        machine.OnCall = (_, address) => { Assert.Contains(address, new uint[] { 0x710800, 0x710900 }); calls++; };
        machine.Run(); // Unseeded next/tail reads throw immediately.
        Assert.True(machine.Returned);
        Assert.DoesNotContain(0x20000u, machine.Reads);
        Assert.DoesNotContain(0x10054u, machine.Reads);
        Assert.Equal(offset is 0x700 or 0x900 ? 1 : 0, calls);
        if (offset == 0x300) Assert.Equal(0x42C80000u, machine.Memory[0x70008]);
        // 原来写成 machine.Writes.Concat(machine.Executed.Where(_ => calls > 0))：只要发生过调用就恒非空，
        // 什么都没写也能过。按各入口的真实行为钉死写集合（0x900 是只读候选 + 调用的入口，不写内存）。
        else if (offset == 0x900) Assert.Empty(machine.Writes);
        else Assert.NotEmpty(machine.Writes);
    }

    [Fact]
    public void SignedBranchesUseLiveFlagsRatherThanStaleComparison()
    {
        // cmp eax,1 在 eax=0 时得比较结果 -1（“小于”）；紧接着 test eax,eax 把标志位覆盖成 SF=0/OF=0。
        // 真实 x86：test 之后 jge（SF==OF）成立 ⇒ 跳转、不执行 mov eax,2。
        // 旧夹具用 cmp 留下的陈旧 comparison，jge 会判 false 而跑进 mov eax,2 —— 这条用例把它们区分开。
        var machine = new Machine(Convert.FromHexString("83F80185C07D05B802000000C3"), 0x1000)
        {
            Registers = { [Register.EAX] = 0 }
        };
        machine.Run();
        Assert.True(machine.Returned);
        Assert.Equal(0u, machine.Registers[Register.EAX]);
        Assert.True(machine.Zero); // test eax,eax → ZF=1
    }

    [Fact]
    public void PopadDoesNotRestoreEspAndKeepsPushadSnapshot()
    {
        // pushad(60) ; popad(61) ; ret(C3)：净效果 ESP 不变，寄存器从快照恢复。
        var machine = new Machine(Convert.FromHexString("6061C3"), 0x1000)
        {
            Registers = { [Register.ESP] = 0x40000, [Register.EAX] = 0x1234 }
        };
        machine.Run();
        Assert.True(machine.Returned);
        Assert.Equal(0x40000u, machine.Registers[Register.ESP]);
        Assert.Equal(0x1234u, machine.Registers[Register.EAX]);
    }

    [Fact]
    public void PushBetweenPushadAndPopadLeavesEspUnbalancedByFour()
    {
        // pushad ; push eax ; popad ; ret：真实 x86 里中间多压了一个 dword，而 popad **不恢复 ESP**，
        // 所以最终 ESP 比入栈前低 4。旧夹具会把快照里的 ESP 恢复回去，抹平这个不平衡。
        var machine = new Machine(Convert.FromHexString("605061C3"), 0x1000)
        {
            Registers = { [Register.ESP] = 0x40000, [Register.EAX] = 0x1234 }
        };
        machine.Run();
        Assert.True(machine.Returned);
        Assert.Equal(0x40000u - 4u, machine.Registers[Register.ESP]);
    }

    [Fact]
    public void PopadWithoutPushadFailsWithClearDiagnostic()
    {
        var machine = new Machine(Convert.FromHexString("61C3"), 0x1000);
        machine.Registers[Register.ESP] = 0x40000;
        var error = Assert.Throws<InvalidOperationException>(() => machine.Run());
        Assert.Contains("popad", error.Message);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PowerBothPathsExecutePatchedBytesAndPreserveNativeLoad(bool validPath, bool enabled)
    {
        var machine = Encode("MustCode", 0x6C);
        const uint site = 0xACFD01;
        var native = Convert.FromHexString("8D482039095B740433C0EB038B40048B8EB0030000");
        X86PatchEncoder.EncodeNearJumpWithNops((nint)site, 0x71006C, 6).CopyTo(native, 0);
        machine.OtherCode[site] = native;
        machine.Start = site;
        machine.Registers[Register.EAX] = 0x20000;
        machine.Registers[Register.ESI] = 0x30000;
        machine.Registers[Register.EDX] = 0x270;
        machine.Registers[Register.ESP] = 0x40000;
        machine.Memory[0x40000] = 0x123456;
        machine.Memory[0x20020] = validPath ? 0x20020u : 0;
        machine.Memory[0x20004] = 123;
        machine.Memory[0x303B0] = 0x50000;
        machine.Memory[0x700109] = enabled ? 1u : 0;
        machine.Memory[0x700134] = 999;
        machine.Run();
        Assert.Equal(validPath ? enabled ? 999u : 123u : 0u, machine.Registers[Register.EAX]);
        Assert.Equal(0x50000u, machine.Registers[Register.ECX]);
        Assert.Equal(0x270u, machine.Registers[Register.EDX]);
        Assert.Equal(0x123456u, machine.Registers[Register.EBX]);
        Assert.Equal(0x40004u, machine.Registers[Register.ESP]);
        Assert.True(machine.Zero); // cmp equal or original xor eax,eax
        Assert.Equal(0xACFD16u, machine.StoppedAt);
        Assert.DoesNotContain(machine.Executed, pc => pc > site && pc < site + 6);
        if (!validPath) Assert.DoesNotContain(0x20004u, machine.Reads);
        if (!validPath || !enabled) Assert.Empty(machine.Writes);
        else { Assert.Equal(999u, machine.Memory[0x20004]); Assert.Equal(0u, machine.Memory[0x20008]); }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void ScPointsAllThreePathsPreserveNativeLoad(int path, bool enabled)
    {
        var machine = Encode("MustCode", 0x9F);
        const uint start = 0xACFE56, site = 0xACFE5E;
        var native = Convert.FromHexString("85C0750433FFEB118B802013000085C0750433FFEB038B78348B4E3C");
        X86PatchEncoder.EncodeNearJumpWithNops((nint)site, 0x71009F, 6).CopyTo(native, 8);
        machine.OtherCode[start] = native;
        machine.Start = start;
        machine.Registers[Register.EAX] = path == 0 ? 0u : 0x20000;
        machine.Registers[Register.ESI] = 0x30000;
        machine.Memory[0x21320] = path == 1 ? 0u : 0x40000;
        machine.Memory[0x40034] = 7;
        machine.Memory[0x3003C] = 0x50000;
        machine.Memory[0x70010A] = enabled ? 1u : 0;
        machine.Memory[0x700138] = 15;
        machine.Run();
        Assert.Equal(path == 2 ? enabled ? 15u : 7u : 0u, machine.Registers[Register.EDI]);
        Assert.Equal(0x50000u, machine.Registers[Register.ECX]);
        Assert.Equal(path != 2, machine.Zero);
        Assert.Equal(0xACFE72u, machine.StoppedAt);
        Assert.DoesNotContain(machine.Executed, pc => pc > site && pc < site + 6);
        if (path != 2) Assert.DoesNotContain(0x40034u, machine.Reads);
        if (path != 2 || !enabled) Assert.Empty(machine.Writes);
        else Assert.Equal(15u, machine.Memory[0x40034]);
    }
    [Theory]
    [InlineData(0x11)]
    [InlineData(0x111)]
    [InlineData(0x211)]
    [InlineData(0x311)]
    public void NullMovementComponentReturnsBeforeAnyFieldReadOrWrite(int offset)
    {
        var machine = Movement(offset, 0);
        machine.Run();
        Assert.Empty(machine.Writes);
        Assert.DoesNotContain(8u, machine.Reads);
        Assert.True(machine.Returned);
    }

    [Theory]
    [InlineData(0x11, 0x43FA0000u)]
    [InlineData(0x111, 0x41200000u)]
    [InlineData(0x211, 0u)]
    [InlineData(0x311, 0x42C80000u)]
    public void ValidMovementStillWritesOriginalValuesAndBackup(int offset, uint expected)
    {
        var machine = Movement(offset, 0x70000);
        machine.Memory[0x70008] = offset == 0x311 ? 0x43FA0000u : 0x42C80000u;
        machine.Memory[0x70040] = 0x42C80000;
        machine.Run();
        Assert.Equal(expected, machine.Memory[0x70008]);
        Assert.Equal(0x42C80000u, machine.Memory[0x70040]);
        Assert.Contains(0x70008u, machine.Writes);
    }

    [Theory]
    [InlineData(0u, 1u, 1u)]
    [InlineData(0x70000u, 0u, 1u)]
    [InlineData(0x70000u, 1u, 0u)]
    [InlineData(0x70000u, 1u, 1u)]
    [InlineData(0xFFFFFFFFu, 1u, 1u)]
    public void AmmoInvalidOrUnprovenOwnershipNeverWritesAndPreservesOriginalTestFlags(uint component, uint enabled, uint marker)
    {
        var machine = Encode("MustCode", 0x327);
        machine.Registers[Register.EDX] = 0x20000;
        machine.Registers[Register.EDI] = 2;
        machine.Registers[Register.EBP] = 0x30000;
        machine.Memory[0x20008] = component;
        machine.Memory[0x700112] = enabled;
        machine.Memory[0x303AC] = marker;
        machine.Run();
        Assert.Empty(machine.Writes);
        Assert.Equal(component == 0, machine.Zero);
        Assert.Equal(component, machine.Registers[Register.ECX]);
        Assert.Equal(0x30000u, machine.Registers[Register.EBP]);
        Assert.DoesNotContain(unchecked(component + 0x1C), machine.Reads);
    }

    private static Machine Movement(int offset, uint component)
    {
        var machine = Encode("MustCode2", offset);
        machine.Registers[Register.EDI] = 0x20000;
        foreach (var (address, value) in new (uint, uint)[]
        {
            (0x20008, 0x30000), (0x30138, 0x40000), (0x40374, 0x50000),
            (0x50200, 0x60000), (0x60000, component), (0x60004, 0x80000), (0x80018, 0)
        }) machine.Memory[address] = value;
        return machine;
    }

    public static IEnumerable<object[]> SpeedBranches()
    {
        foreach (var offset in new[] { 0x11, 0x111, 0x211, 0x311 })
        foreach (var initial in new uint[] { 0x43FA0000, 0x41200000, 0, 0x42C80000 })
        foreach (var secondActive in new[] { false, true })
            yield return new object[] { offset, initial, secondActive };
    }

    [Theory]
    [MemberData(nameof(SpeedBranches))]
    public void RelocatedGuardPreservesEverySentinelBranchAndSecondComponentGate(int offset, uint initial, bool secondActive)
    {
        var machine = Movement(offset, 0x70000);
        machine.Memory[0x70008] = machine.Memory[0x80008] = initial;
        machine.Memory[0x70040] = machine.Memory[0x80040] = 0x42480000; // float 50
        machine.Memory[0x80018] = secondActive ? 0x3F800000u : 0;
        machine.Run();
        var sentinel = initial is 0x43FA0000 or 0x41200000 or 0;
        var expected = offset switch
        { 0x11 => 0x43FA0000u, 0x111 => 0x41200000u, 0x211 => 0u, _ => sentinel ? 0x42480000u : initial };
        Assert.Equal(expected, machine.Memory[0x70008]);
        Assert.Equal(secondActive ? expected : initial, machine.Memory[0x80008]);
        Assert.Equal(offset == 0x311 || sentinel ? 0x42480000u : initial, machine.Memory[0x70040]);
        Assert.Equal(!secondActive || offset == 0x311 || sentinel ? 0x42480000u : initial, machine.Memory[0x80040]);
    }

    private static Machine Encode(string symbol, int offset)
    {
        var block = AaScriptReader.Read(TestAssets.ReadBootstrapLines()).Blocks.Single(b => b.Symbol == symbol && b.Offset == offset);
        var resolver = new AddressResolver(0x400000, new Dictionary<string, nint>
        { ["MustCode"] = 0x710000, ["MustCode2"] = 0x720000, ["iEnable"] = 0x700100 });
        var origin = resolver.Resolve($"{symbol}+{offset:X}");
        var context = new BootstrapBuildContext(resolver.Resolve, new Dictionary<string, nint>
        { ["_BackSelectUnitAmmo"] = 0x52873A, ["_BackPlayerPower"] = 0xACFD07, ["_BackPlayerSCPoint"] = 0xACFE64 });
        return new(AaInstructionEmitter.Encode(block.Lines, origin, context), (uint)origin);
    }

    private sealed class Machine(byte[] code, uint origin)
    {
        public Dictionary<Register, uint> Registers { get; } = new();
        public Dictionary<uint, uint> Memory { get; } = new();
        public List<uint> Writes { get; } = [];
        public List<uint> Reads { get; } = [];
        public bool Zero, Returned;
        // 有符号标志位：jge/jle 判的是 SF/OF 而不是“上一次 cmp 的比较结果”这种陈旧状态。
        public bool Sf, Of;
        private bool _pushadSavedStack;
        public uint? Start;
        public uint StoppedAt;
        public Dictionary<uint, byte[]> OtherCode { get; } = new();
        public List<uint> Executed { get; } = [];
        public Action<Machine, uint>? OnCall;

        public void Run()
        {
            var pc = Start ?? origin;
            (bool Zero, bool Sf, bool Of)? savedFlags = null;
            Dictionary<Register, uint>? savedRegisters = null;
            for (var step = 0; step < 10000; step++)
            {
                byte[] activeCode = code; uint activeOrigin = origin;
                if (pc < origin || pc >= origin + code.Length)
                {
                    if (!TryResolveRegion(pc, out activeOrigin, out activeCode)) { StoppedAt = pc; return; }
                }
                Executed.Add(pc);
                var decoder = Decoder.Create(32, new ByteArrayCodeReader(activeCode[(int)(pc - activeOrigin)..]));
                decoder.IP = pc;
                var instruction = decoder.Decode();
                pc = (uint)instruction.NextIP;
                switch (instruction.Mnemonic)
                {
                    case Mnemonic.Nop: break;
                    case Mnemonic.Lea: Registers[instruction.Op0Register] = Address(instruction); break;
                    case Mnemonic.Push:
                        // 真实 push 会写栈内存并让 ESP 减 4；夹具原本没建模，遇到 push 直接抛。
                        var pushed = Read(instruction, 0);
                        Registers[Register.ESP] = Registers.GetValueOrDefault(Register.ESP) - 4;
                        Memory[Registers[Register.ESP]] = pushed;
                        Writes.Add(Registers[Register.ESP]);
                        break;
                    case Mnemonic.Pop:
                        Registers[instruction.Op0Register] = Memory[Registers[Register.ESP]];
                        Registers[Register.ESP] += 4;
                        break;
                    case Mnemonic.Xor:
                        var xorValue = Read(instruction, 0) ^ Read(instruction, 1);
                        Store(instruction, xorValue);
                        SetFlags(xorValue, overflow: false);
                        break;
                    case Mnemonic.Mov:
                    case Mnemonic.Movss:
                        Store(instruction, Read(instruction, 1));
                        break;
                    case Mnemonic.Test:
                        var tested = Read(instruction, 0) & Read(instruction, 1);
                        SetFlags(tested, overflow: false);
                        break;
                    case Mnemonic.Cmp:
                        var cmpLeft = Read(instruction, 0);
                        var cmpRight = Read(instruction, 1);
                        var cmpDiff = unchecked(cmpLeft - cmpRight);
                        // 有符号溢出看结果的**符号位**，不是“非零”。
                        SetFlags(cmpDiff, overflow: unchecked((int)((cmpLeft ^ cmpRight) & (cmpLeft ^ cmpDiff))) < 0);
                        break;
                    case Mnemonic.Sub:
                        var subLeft = Read(instruction, 0);
                        var subRight = Read(instruction, 1);
                        var subDiff = unchecked(subLeft - subRight);
                        Store(instruction, subDiff);
                        SetFlags(subDiff, overflow: unchecked((int)((subLeft ^ subRight) & (subLeft ^ subDiff))) < 0);
                        break;
                    case Mnemonic.Add:
                        var addLeft = Read(instruction, 0);
                        var addRight = Read(instruction, 1);
                        var addSum = unchecked(addLeft + addRight);
                        Store(instruction, addSum);
                        SetFlags(addSum, overflow: unchecked((int)(~(addLeft ^ addRight) & (addLeft ^ addSum))) < 0);
                        break;
                    case Mnemonic.And:
                        var andValue = Read(instruction, 0) & Read(instruction, 1);
                        Store(instruction, andValue);
                        SetFlags(andValue, overflow: false);
                        break;
                    case Mnemonic.Or:
                        var orValue = Read(instruction, 0) | Read(instruction, 1);
                        Store(instruction, orValue);
                        SetFlags(orValue, overflow: false);
                        break;
                    // jge = SF==OF；jle = ZF==1 或 SF!=OF。用真实有符号标志位判定，
                    // 而不是 cmp 留下的陈旧 comparison —— 中间夹一条写标志的指令（test/dec/xor…）
                    // 就不会再让分支按旧的 cmp 结果走（那会把真实回归变成假绿）。
                    case Mnemonic.Jge: if (Sf == Of) pc = (uint)instruction.NearBranchTarget; break;
                    case Mnemonic.Jle: if (Zero || Sf != Of) pc = (uint)instruction.NearBranchTarget; break;
                    case Mnemonic.Inc:
                        var incremented = Read(instruction, 0);
                        var incResult = unchecked(incremented + 1);
                        Store(instruction, incResult);
                        SetFlags(incResult, overflow: incremented == 0x7FFFFFFF);
                        break;
                    case Mnemonic.Dec:
                        var decremented = Read(instruction, 0);
                        var decResult = unchecked(decremented - 1);
                        Store(instruction, decResult);
                        SetFlags(decResult, overflow: decremented == 0x80000000);
                        break;
                    case Mnemonic.Call:
                        if (OnCall is null) throw new InvalidOperationException("Unexpected call");
                        OnCall(this, instruction.NearBranch32); break;
                    case Mnemonic.Je: if (Zero) pc = (uint)instruction.NearBranchTarget; break;
                    case Mnemonic.Jne: if (!Zero) pc = (uint)instruction.NearBranchTarget; break;
                    case Mnemonic.Jmp: pc = (uint)instruction.NearBranchTarget; break;
                    case Mnemonic.Pushfd: savedFlags = (Zero, Sf, Of); break;
                    case Mnemonic.Popfd:
                        if (savedFlags is not { } flags) throw new InvalidOperationException("popfd 没有前置 pushfd：标志位无快照可恢复。");
                        (Zero, Sf, Of) = flags;
                        break;
                    case Mnemonic.Pushad:
                        savedRegisters = new(Registers);
                        // 只有 ESP 本来就在寄存器集合里才调它（有些块的栈指针没被建模，旧夹具也不碰 ESP）。
                        _pushadSavedStack = Registers.ContainsKey(Register.ESP);
                        if (_pushadSavedStack) Registers[Register.ESP] = Registers.GetValueOrDefault(Register.ESP) - 32; // PUSHAD 压入 8 个 dword。
                        break;
                    case Mnemonic.Popad:
                        // 没有前置 pushad 时原来会抛裸的 NullReferenceException，把真实原因盖掉 —— 给明确诊断。
                        if (savedRegisters is null) throw new InvalidOperationException("popad 没有前置 pushad：寄存器快照缺失，解释器无法恢复。");
                        // 真实 x86 的 POPAD **不恢复 ESP**：它只把 pushad 压入的 8 个 dword 弹出，
                        // ESP 净增 32（期间有未配平的 push/pop 时最终 ESP 会相应偏移）。
                        // 原来从快照把 ESP 一起恢复，会把 pushad/popad 之间的 push 抹平。
                        var espAfterPops = Registers.GetValueOrDefault(Register.ESP) + 32;
                        Registers.Clear();
                        foreach (var item in savedRegisters) Registers[item.Key] = item.Value;
                        // 若 pushad 前 ESP 没被建模，就保持不建模（不要凭空造一个 ESP 出来）。
                        if (_pushadSavedStack) Registers[Register.ESP] = espAfterPops;
                        break;
                    case Mnemonic.Ret: Returned = true; return;
                    default: throw new InvalidOperationException("Unexpected instruction: " + instruction);
                }
            }
            throw new InvalidOperationException("Guard did not terminate within the instruction budget.");
        }

        /// <summary>
        /// 解析覆盖 pc 的 OtherCode 区段。Dictionary 不保证枚举顺序，原来的 FirstOrDefault 在两段重叠时
        /// 取到哪段是不确定的（未来加重叠区域会让用例随机变红）。这里按“唯一命中”求解：
        /// 恰有一段覆盖 pc 就确定返回；两段以上覆盖直接炸，而不是静默取一段。
        /// </summary>
        private bool TryResolveRegion(uint pc, out uint baseAddress, out byte[] regionCode)
        {
            baseAddress = 0; regionCode = [];
            uint? found = null;
            foreach (var pair in OtherCode)
            {
                if (pc < pair.Key || pc >= pair.Key + pair.Value.Length) continue;
                if (found is not null && found.Value != pair.Key)
                    throw new InvalidOperationException(
                        $"OtherCode 区段在 0x{pc:X} 处重叠（0x{found.Value:X} 与 0x{pair.Key:X}）：解释器无法确定执行哪一段。");
                found = pair.Key;
                baseAddress = pair.Key; regionCode = pair.Value;
            }
            return found is not null;
        }

        private void Store(Instruction instruction, uint value)
        {
            if (instruction.Op0Kind == OpKind.Register) Registers[instruction.Op0Register] = value;
            else { var at = Address(instruction); Memory[at] = value; Writes.Add(at); }
        }

        private void SetFlags(uint result, bool overflow)
        {
            Zero = result == 0;
            Sf = unchecked((int)result) < 0;
            Of = overflow;
        }

        private uint Address(Instruction instruction) => unchecked((uint)instruction.MemoryDisplacement64 +
            Registers.GetValueOrDefault(instruction.MemoryBase) +
            Registers.GetValueOrDefault(instruction.MemoryIndex) * (uint)instruction.MemoryIndexScale);

        private uint Read(Instruction instruction, int operand)
        {
            var kind = instruction.GetOpKind(operand);
            if (kind == OpKind.Register) return Registers.GetValueOrDefault(instruction.GetOpRegister(operand));
            if (kind != OpKind.Memory) return (uint)instruction.GetImmediate(operand);
            var address = Address(instruction);
            Reads.Add(address);
            return Memory.TryGetValue(address, out var value) ? value :
                throw new InvalidOperationException($"Invalid fake memory read 0x{address:X}; guard ran too late.");
        }
    }
}
