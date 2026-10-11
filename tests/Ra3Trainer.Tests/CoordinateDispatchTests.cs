using Iced.Intel;
using Ra3Trainer.Core.Codegen;
using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Manifest;
using Ra3Trainer.Core.Memory;
using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class CoordinateDispatchTests
{
    [Theory]
    [InlineData(4000f, true)]
    [InlineData(0f, false)]
    [InlineData(float.NaN, false)]
    [InlineData(10001f, false)]
    public void CurrentCoordinateHintIsReadOnlyAndBounded(float x, bool valid)
    {
        var memory = new CoordinateMemory();
        memory.Word(RemoteStateLayout.WorldCoordinates, BitConverter.SingleToUInt32Bits(x));
        memory.Word(memory.Coordinates + 4, BitConverter.SingleToUInt32Bits(4500));
        memory.Word(memory.Coordinates + 8, BitConverter.SingleToUInt32Bits(200));
        var controller = new FeatureController(memory, memory.Resolver);
        Assert.Equal(valid, controller.HasValidCurrentCoordinates(Feature("We Need Back"), ReinforcementSettings.Default));
        Assert.Equal(0, memory.WriteCount);
        Assert.Empty(memory.Dispatches);
    }

    [Fact]
    public async Task GoodCurrentHintCannotBypassFreshConversionFailure()
    {
        var memory = new CoordinateMemory();
        foreach (var offset in new[] { 0, 4, 8 }) memory.Word(memory.Coordinates + offset, BitConverter.SingleToUInt32Bits(200));
        var controller = new FeatureController(memory, memory.Resolver);
        Assert.True(controller.HasValidCurrentCoordinates(Feature("We Need Back")));
        memory.Output[0] = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.TriggerActionAndWaitForConsumptionAsync(Feature("We Need Back")));
        Assert.Equal(new byte[] { 0x0E }, memory.Dispatches);
        Assert.Null(memory.GenerationBytes);
    }

    [Fact]
    public void InvalidRootFailsImmediatelyInsteadOfOfferingCountdown()
    {
        var memory = new CoordinateMemory();
        memory.Word("ID", 0);
        var controller = new FeatureController(memory, memory.Resolver);
        Assert.Throws<InvalidOperationException>(() => controller.HasValidCurrentCoordinates(Feature("We Need Back")));
        Assert.Equal(0, memory.WriteCount);
    }

    [Fact]
    public async Task CopyUsesHeadTemplateEvenWhenGlobalSelectedCodeDiffers()
    {
        var memory = new CoordinateMemory();
        memory.Word(0x2005C, 1806);
        memory.Word(RemoteStateLayout.SelectedUnitCode, 123);
        memory.FaultAt = 0x20054;
        memory.ShortAt = 0x30000;
        var result = await new FeatureController(memory, memory.Resolver)
            .TriggerActionAndWaitForConsumptionAsync(Feature("Select Unit Copy For Me"));
        Assert.Equal(ActionDispatchResult.Consumed, result);
        Assert.Equal(new byte[] { 0x0E, 0x0D }, memory.Dispatches);
        Assert.Equal(memory.ConvertedBytes, memory.GenerationBytes);
    }

    public static IEnumerable<object[]> Actions => new[] { "We Need Back", "Get Me Base", "Select Unit Copy For Me" }
        .Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task ValidCoordinatesDispatchConversionThenGenerationWithIdenticalBytes(string name)
    {
        var memory = new CoordinateMemory();
        var controller = new FeatureController(memory, memory.Resolver);
        var callbackCount = 0;
        var result = await controller.TriggerActionAndWaitForConsumptionAsync(Feature(name),
            new ReinforcementSettings(ReinforcementSettings.DefaultUnitId, 8, 2), onDispatched: () => callbackCount++);
        Assert.Equal(ActionDispatchResult.Consumed, result);
        Assert.Equal(new byte[] { 0x0E, Convert.ToByte(Feature(name).ValueHint![2..], 16) }, memory.Dispatches);
        Assert.Equal(1, callbackCount); // Only C is announced as the requested action.
        Assert.Equal(memory.ConvertedBytes, memory.GenerationBytes);
        Assert.Equal(memory.ConvertedBytes, memory.ReadBytes(memory.Coordinates, 12));
        // 直接断言 _inner 的坐标字节 = 游戏侧换算结果（Output），而不是拿 HostWriteAddresses 反推。
        // 模拟的游戏侧是通过 _inner 直写的，HostWriteAddresses 只覆盖“主机写路径”，
        // 单看它并不能证明坐标里就是换算结果。
        Assert.Equal(memory.Output.SelectMany(BitConverter.GetBytes).ToArray(), memory.ReadBytes(memory.Coordinates, 12));
        Assert.Equal(name == "We Need Back" ? 5 : 2, memory.WriteCount);
        Assert.DoesNotContain(memory.HostWriteAddresses, a => a >= memory.Coordinates && a < memory.Coordinates + 12);
        if (name == "We Need Back")
        {
            Assert.Equal(ReinforcementSettings.DefaultUnitId, memory.ReadWord("iEnable+24"));
            Assert.Equal(2u, memory.ReadWord("iEnable+2C"));
        }
    }

    public static IEnumerable<object[]> InvalidCoordinates =>
        from name in Actions.Select(row => (string)row[0])
        from component in new[] { 0, 1, 2 }
        from value in new[] { 0f, -0f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1.01f, 10000.01f }
        select new object[] { name, component, value };

    [Theory]
    [MemberData(nameof(InvalidCoordinates))]
    public async Task EveryInvalidOrUnwrittenComponentRejectsWithZeroStageBWrites(string name, int component, float value)
    {
        var memory = new CoordinateMemory();
        memory.Output[component] = value;
        var controller = new FeatureController(memory, memory.Resolver);
        var messages = new List<string>();
        controller.SafetyMessage += messages.Add;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.TriggerActionAndWaitForConsumptionAsync(Feature(name)));
        Assert.Contains("世界坐标", error.Message);
        Assert.Contains(value == 0 ? "零哨兵" : "有限值范围", error.Message);
        Assert.Equal(error.Message, Assert.Single(messages));
        Assert.Equal(1, memory.WriteCount);
        Assert.Equal(0, memory.WriteCount - memory.WritesAtConversionCompletion);
        Assert.Equal(new byte[] { 0x0E }, memory.Dispatches);
        Assert.Null(memory.GenerationBytes);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(10000f)]
    public async Task EmpiricalBoundsAreInclusive(float boundary)
    {
        var memory = new CoordinateMemory { Output = [boundary, boundary, boundary] };
        Assert.Equal(ActionDispatchResult.Consumed, await new FeatureController(memory, memory.Resolver)
            .TriggerActionAndWaitForConsumptionAsync(Feature("Get Me Base")));
        Assert.Equal(2, memory.WriteCount);
    }

    [Theory]
    [InlineData(9908.259765625f, 487.04016f, 410f)]
    [InlineData(9356.673f, 9839.1435546875f, 397.87915f)]
    [InlineData(13.86456298828125f, 9207.793f, 210f)]
    public async Task Round19ObservedCoordinatesRequireFreshConversionAndPreserveExactBuffer(float x, float y, float z)
    {
        var memory = new CoordinateMemory { Output = [x, y, z] };
        var result = await new FeatureController(memory, memory.Resolver)
            .TriggerActionAndWaitForConsumptionAsync(Feature("Get Me Base"));
        Assert.Equal(ActionDispatchResult.Consumed, result);
        Assert.Equal(new byte[] { 0x0E, 0x0B }, memory.Dispatches);
        Assert.Equal(memory.ConvertedBytes, memory.GenerationBytes);
        Assert.Equal(2, memory.WriteCount);
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task ConversionThatDoesNotWriteCannotReusePlausibleOldOutput(string name)
    {
        var memory = new CoordinateMemory { Output = [0, 0, 0] };
        memory.SetCoordinates([4000, 4000, 200]); // Last invocation's plausible output.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FeatureController(memory, memory.Resolver)
            .TriggerActionAndWaitForConsumptionAsync(Feature(name)));
        Assert.Contains("零哨兵", error.Message);
        Assert.Equal(new byte[12], memory.ReadBytes(memory.Coordinates, 12));
        Assert.Equal(1, memory.WriteCount);
        Assert.Null(memory.GenerationBytes);
    }

    [Theory]
    [InlineData("mouse-null")]
    [InlineData("mouse-unaligned")]
    [InlineData("mouse-page")]
    [InlineData("mouse-field-page")]
    [InlineData("player-null")]
    [InlineData("player-field-page")]
    [InlineData("manager-null")]
    [InlineData("type-zero")]
    [InlineData("type-unknown")]
    [InlineData("owner-null")]
    [InlineData("selection-null")]
    [InlineData("selection-health")]
    public async Task BrokenInputChainTypeOrOwnerHasZeroWrites(string fault)
    {
        var memory = new CoordinateMemory();
        ApplyFault(memory, fault);
        var controller = new FeatureController(memory, memory.Resolver);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.TriggerActionAndWaitForConsumptionAsync(Feature("Select Unit Copy For Me")));
        Assert.Equal(0, memory.WriteCount);
        Assert.Empty(memory.Dispatches);
    }

    [Theory]
    [InlineData("player-null")]
    [InlineData("manager-null")]
    [InlineData("type-zero")]
    [InlineData("type-unknown")]
    [InlineData("owner-null")]
    [InlineData("selection-null")]
    [InlineData("selection-health")]
    public async Task InputsAreRevalidatedAfterConversionWithoutAnyRejectionWrites(string fault)
    {
        var memory = new CoordinateMemory();
        memory.AfterConversion = () => ApplyFault(memory, fault);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FeatureController(memory, memory.Resolver)
            .TriggerActionAndWaitForConsumptionAsync(Feature("Select Unit Copy For Me")));
        Assert.Equal(1, memory.WriteCount);
        Assert.Equal(0, memory.WriteCount - memory.WritesAtConversionCompletion);
        Assert.Null(memory.GenerationBytes);
    }

    [Fact]
    public async Task UnknownReinforcementTypeIsRejectedBeforeConversion()
    {
        var memory = new CoordinateMemory();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FeatureController(memory, memory.Resolver)
            .TriggerActionAndWaitForConsumptionAsync(Feature("We Need Back"), new ReinforcementSettings(123, 8, 3)));
        Assert.Contains("单位类型", error.Message);
        Assert.Equal(0, memory.WriteCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SilentDroppedConversionCannotTreatIdleSlotAsCompletion(bool epochWrong)
    {
        var memory = new CoordinateMemory { DropConversion = !epochWrong, WrongEpoch = epochWrong };
        memory.SetCoordinates([4000, 4000, 200]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FeatureController(memory, memory.Resolver)
            .TriggerActionAndWaitForConsumptionAsync(Feature("Get Me Base")));
        Assert.Contains("完成凭据", error.Message);
        Assert.Equal(1, memory.WriteCount);
        Assert.Null(memory.GenerationBytes);
    }

    [Theory]
    [InlineData("slot-high-byte")]
    [InlineData("lock")]
    [InlineData("slot-short")]
    [InlineData("epoch-short")]
    public async Task IncompleteOrBusyProtocolRefusesBeforeConversion(string fault)
    {
        var memory = new CoordinateMemory();
        if (fault == "slot-high-byte") memory.Word("iEnable+20", 0x100);
        if (fault == "lock") memory.Word("iEnable+40", 1);
        if (fault == "slot-short") memory.ShortAt = memory.Resolver.Resolve("iEnable+20");
        if (fault == "epoch-short") memory.ShortAt = memory.Resolver.Resolve("iEnable+44");
        var controller = new FeatureController(memory, memory.Resolver);
        if (fault == "epoch-short")
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.TriggerActionAndWaitForConsumptionAsync(Feature("Get Me Base")));
        else Assert.Equal(ActionDispatchResult.TimedOut, await controller.TriggerActionAndWaitForConsumptionAsync(Feature("Get Me Base")));
        Assert.Equal(0, memory.WriteCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutCannotBeResetOrOverwrittenBeforeRemoteConsumption(bool generation)
    {
        var memory = new CoordinateMemory { HoldConversion = !generation, HoldGeneration = generation };
        var controller = new FeatureController(memory, memory.Resolver);
        var feature = Feature("Get Me Base");
        Assert.Equal(ActionDispatchResult.TimedOut, await controller.TriggerActionAndWaitForConsumptionAsync(feature, timeout: TimeSpan.Zero));
        var writes = memory.WriteCount;
        Assert.Throws<InvalidOperationException>(() => controller.Reset(feature));
        Assert.Throws<InvalidOperationException>(() => controller.Reset(Feature("Select Unit HP MAX")));
        Assert.Throws<InvalidOperationException>(() => controller.WriteReinforcementSettings(ReinforcementSettings.Default));
        Assert.Throws<InvalidOperationException>(() => controller.TriggerAction(Feature("Select Unit HP MAX")));
        Assert.Equal(ActionDispatchResult.TimedOut, await controller.TriggerActionAndWaitForConsumptionAsync(feature));
        Assert.Equal(writes, memory.WriteCount);
        Assert.Equal(generation ? 0x0Bu : 0x0Eu, memory.ReadWord("iEnable+20"));
    }

    [Fact]
    public async Task RemoteLockMustBeReleasedBeforeHostValidatesOutput()
    {
        var memory = new CoordinateMemory();
        memory.AfterConversion = () => memory.Word("iEnable+40", 1);
        Assert.Equal(ActionDispatchResult.TimedOut, await new FeatureController(memory, memory.Resolver)
            .TriggerActionAndWaitForConsumptionAsync(Feature("Get Me Base"), timeout: TimeSpan.Zero));
        Assert.Equal(1, memory.WriteCount);
        Assert.Null(memory.GenerationBytes);
    }

    [Fact]
    public async Task ZeroSlotWithBusyRemoteLockRejectsAllRecoveryAndDispatchWrites()
    {
        var memory = new CoordinateMemory();
        memory.Word("iEnable+40", 1);
        var controller = new FeatureController(memory, memory.Resolver);
        Assert.Throws<InvalidOperationException>(() => controller.Reset(Feature("Get Me Base")));
        Assert.Throws<InvalidOperationException>(() => controller.Reset(Feature("Select Unit HP MAX")));
        Assert.Throws<InvalidOperationException>(() => controller.WriteReinforcementSettings(ReinforcementSettings.Default));
        Assert.Throws<InvalidOperationException>(() => controller.TriggerAction(Feature("Select Unit HP MAX")));
        Assert.Equal(ActionDispatchResult.TimedOut, await controller.TriggerActionAndWaitForConsumptionAsync(Feature("Get Me Base")));
        Assert.Equal(0, memory.WriteCount);
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task KnownCreationFailureIsReportedWithoutAttemptingRollback(string name)
    {
        var memory = new CoordinateMemory { CreationFails = true };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FeatureController(memory, memory.Resolver)
            .TriggerActionAndWaitForConsumptionAsync(Feature(name)));
        Assert.Contains("可能已部分生成，无法回滚", error.Message);
        Assert.Equal(name == "We Need Back" ? 5 : 2, memory.WriteCount);
        Assert.Equal(2, memory.Dispatches.Count);
    }

    [Fact]
    public async Task SilentlyDroppedGenerationIsNotReportedAsConsumed()
    {
        var memory = new CoordinateMemory { DropGeneration = true };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FeatureController(memory, memory.Resolver)
            .TriggerActionAndWaitForConsumptionAsync(Feature("Get Me Base")));
        Assert.Contains("生成派发未返回完成凭据", error.Message);
        Assert.Equal(2, memory.WriteCount);
        Assert.Null(memory.GenerationBytes);
    }

    [Fact]
    public async Task QueueReportsConsumptionWithoutClaimingVerifiedMaterialization()
    {
        var memory = new CoordinateMemory();
        var results = await ReinforcementQueueRunner.ExecuteAsync(
            [new ReinforcementQueueEntry("测试", "0x6586A5A0", "8", "3")],
            new FeatureController(memory, memory.Resolver), Feature("We Need Back"));
        var result = Assert.Single(results);
        Assert.Equal(ReinforcementQueueItemStatus.Executed, result.Status);
        Assert.Contains("未逐单位确认", result.Message);
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public void SynchronousCoordinateApiRefusesWithoutAnyWrite(string name)
    {
        var memory = new CoordinateMemory();
        Assert.Throws<InvalidOperationException>(() => new FeatureController(memory, memory.Resolver).TriggerAction(Feature(name)));
        Assert.Equal(0, memory.WriteCount);
    }

    [Theory]
    [InlineData("renamed", "MustCode2+B00", "0x0C")]
    [InlineData("renamed", "MustCode2+400", "0x0E")]
    [InlineData("renamed", "MustCode2+400", "0xE")]
    [InlineData("renamed", "MustCode2+400", "0xB")]
    [InlineData("renamed", "MustCode2+400", "0xC")]
    [InlineData("renamed", "MustCode2+400", "0xD")]
    [InlineData("renamed", "MustCode2+400", "0x+E")]
    [InlineData("renamed", "MustCode2+400", "0x0x0E")]
    [InlineData("We Need Back", "MustCode2+400", "0x05")]
    public async Task AliasesCannotBypassCoordinateProtocol(string name, string target, string value)
    {
        var memory = new CoordinateMemory();
        var feature = new TrainerFeature(name, "测试", null, [], target, value);
        var controller = new FeatureController(memory, memory.Resolver);
        Assert.Throws<InvalidOperationException>(() => controller.TriggerAction(feature));
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.TriggerActionAndWaitForConsumptionAsync(feature));
        Assert.Equal(0, memory.WriteCount);
    }

    [Fact]
    public async Task GateCoversConversionValidationGenerationAndCallbackReentry()
    {
        var memory = new CoordinateMemory();
        var controller = new FeatureController(memory, memory.Resolver);
        Task<ActionDispatchResult>? queued = null;
        memory.AfterConversion = () =>
        {
            var writes = memory.WriteCount;
            Assert.Throws<InvalidOperationException>(() => controller.TriggerAction(Feature("Select Unit HP MAX")));
            Assert.Throws<InvalidOperationException>(() => controller.Reset(Feature("Get Me Base")));
            Assert.Throws<InvalidOperationException>(() => controller.WriteReinforcementSettings(ReinforcementSettings.Default));
            queued = controller.TriggerActionAndWaitForConsumptionAsync(Feature("Select Unit Copy For Me"));
            Assert.False(queued.IsCompleted);
            Assert.Equal(writes, memory.WriteCount);
            memory.AfterConversion = null;
        };
        Assert.Equal(ActionDispatchResult.Consumed, await controller.TriggerActionAndWaitForConsumptionAsync(Feature("Get Me Base")));
        Assert.Equal(ActionDispatchResult.Consumed, await queued!);
        Assert.Equal(new byte[] { 0x0E, 0x0B, 0x0E, 0x0D }, memory.Dispatches);
        Assert.Equal(4, memory.WriteCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterConversionNeverWritesGeneration(bool stop)
    {
        var memory = new CoordinateMemory();
        var controller = new FeatureController(memory, memory.Resolver);
        using var cancellation = new CancellationTokenSource();
        memory.AfterConversion = () => { if (stop) controller.Stop(); else cancellation.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.TriggerActionAndWaitForConsumptionAsync(
            Feature("Get Me Base"), cancellationToken: cancellation.Token));
        Assert.Equal(1, memory.WriteCount);
        Assert.Null(memory.GenerationBytes);
    }

    [Fact]
    public void EncodedConversionZerosEveryComponentBeforeTheOnlyConverterCall()
    {
        var code = Build();
        var instructions = Decode(code.MustCode2, 0xD00, code.MustCode2.Length, 0x720000);
        Assert.Equal(3, instructions.Take(3).Count(i => i.Mnemonic == Mnemonic.Mov && i.Immediate32 == 0));
        Assert.Equal(new ulong[] { 0x711080, 0x711084, 0x711088 }, instructions.Take(3).Select(i => i.MemoryDisplacement64));
        // 定位式访问前先钉住长度：解码若因零填充被提前截断，instructions[3] 就会落到错误的指令上。
        Assert.True(instructions.Count >= 4, $"MustCode2+D00 解码过短（{instructions.Count}）");
        Assert.Equal(Mnemonic.Call, instructions[3].Mnemonic);
        Assert.Equal(0x711120u, instructions[3].NearBranch32);
        Assert.Contains(instructions, i => i.Mnemonic == Mnemonic.Inc && i.Op0Register == Register.EAX);
        Assert.Contains(instructions, i => i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Memory && i.MemoryDisplacement64 == 0x700144);
        Assert.Equal(Mnemonic.Ret, instructions[^1].Mnemonic);
    }

    [Fact]
    public void GenerationAndAllReachableTrainerHelpersNeitherConvertNorWriteCoordinateBuffer()
    {
        var code = Build();
        // Exact reachable trainer blocks from the three C entries (external game calls are opaque).
        var ranges = new[] { (code.MustCode2, 0xA00, 0xB00, 0x720000u),
            (code.MustCode2, 0xB00, 0xB60, 0x720000u), (code.MustCode2, 0xB60, 0xC00, 0x720000u),
            (code.MustCode2, 0xC00, 0xD00, 0x720000u), (code.MustCode, 0x800, 0x900, 0x710000u),
            (code.MustCode, 0xA60, 0xAA0, 0x710000u), (code.MustCode, 0xAA0, 0xB00, 0x710000u),
            (code.MustCode, 0xB00, 0xB20, 0x710000u) };
        var all = ranges.SelectMany(r => Decode(r.Item1, r.Item2, r.Item3, r.Item4)).ToArray();
        Assert.DoesNotContain(all, i => i.FlowControl == FlowControl.Call && i.NearBranch32 is 0x711120 or 0x710A00 or 0x5ED4A0);
        Assert.All(all.Where(i => i.FlowControl == FlowControl.Call), i =>
            Assert.Contains(i.NearBranch32, new uint[] { 0x710A60, 0x710AA0, 0x710B00, 0x710800, 0x75C200, 0x7E4230, 0x605240 }));
        Assert.DoesNotContain(all, i => i.Op0Kind == OpKind.Memory && i.MemoryBase == Register.None &&
            i.MemoryDisplacement64 >= 0x711080 && i.MemoryDisplacement64 < 0x71108C);
        var create = Decode(code.MustCode, 0xAA0, 0xB00, 0x710000);
        Assert.Contains(create, i => i.Mnemonic == Mnemonic.Lea && i.MemoryDisplacement64 == 0x711080);
        Assert.Contains(create, i => i.Mnemonic == Mnemonic.Call && i.NearBranch32 == 0x605240);
        var baseCalls = Decode(code.MustCode2, 0xA00, 0xB00, 0x720000);
        Assert.Equal(new uint[] { 0x28DA574E, 0xAF4C0DA5, 0x1C2EF767 }, baseCalls
            .Where(i => i.Mnemonic == Mnemonic.Mov && i.Op0Register == Register.EAX && i.Op1Kind == OpKind.Immediate32).Select(i => i.Immediate32));
        Assert.Equal(3, baseCalls.Count(i => i.Mnemonic == Mnemonic.Call && i.NearBranch32 == 0x710AA0));
        Assert.Contains(all, i => i.Mnemonic == Mnemonic.Mov && i.Op0Register == Register.EAX && i.MemoryDisplacement64 == 0x700128);
        Assert.Contains(all, i => i.Mnemonic == Mnemonic.Mov && i.MemoryDisplacement64 == 0x700124);
        Assert.DoesNotContain(all, i => i.MemoryDisplacement64 == 0xCE9838); // Copy uses head template code.
        Assert.Contains(all, i => i.Mnemonic == Mnemonic.Push && i.MemoryDisplacement64 == 0x70012C);
        var loop = Decode(code.MustCode2, 0xB60, 0xC00, 0x720000);
        var rankLoad = loop.FindIndex(i => i.Mnemonic == Mnemonic.Mov && i.MemoryDisplacement64 == 0x3CC);
        Assert.True(rankLoad >= 0);
        // 定位式访问前先钉住长度：解码被提前截断时 rankLoad+5 会越界或落到错误指令。
        Assert.True(rankLoad + 5 < loop.Count, $"MustCode2+B60 解码过短（{loop.Count}），rankLoad={rankLoad}");
        Assert.Equal(Mnemonic.Test, loop[rankLoad + 1].Mnemonic);
        Assert.Equal(Mnemonic.Jne, loop[rankLoad + 2].Mnemonic);
        Assert.Equal(0x70014Cu, loop[rankLoad + 3].MemoryDisplacement64);
        Assert.Equal(1u, loop[rankLoad + 3].Immediate32);
        Assert.Equal(Mnemonic.Ret, loop[rankLoad + 4].Mnemonic);
        Assert.Equal(loop[rankLoad + 5].IP, loop[rankLoad + 2].NearBranchTarget);
        Assert.Equal(0x710800u, loop[rankLoad + 5].NearBranch32);
    }

    [Fact]
    public void DispatchLockIsAtomicAndOnlyOwnerClearsSlotBeforeReleasingLock()
    {
        var code = Build();
        var instructions = Decode(code.MustCode, 0x600, 0x700, 0x710000);
        var exchange = Assert.Single(instructions, i => i.Mnemonic == Mnemonic.Xchg);
        Assert.True(exchange.Op0Kind == OpKind.Memory || exchange.Op1Kind == OpKind.Memory);
        Assert.Equal(0x700140u, exchange.MemoryDisplacement64); // memory XCHG is implicitly locked.
        var callIndex = instructions.FindIndex(i => i.Mnemonic == Mnemonic.Call);
        var clearIndex = instructions.FindIndex(i => i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Memory && i.MemoryDisplacement64 == 0x700120);
        var unlockIndex = instructions.FindIndex(i => i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Memory && i.MemoryDisplacement64 == 0x700140);
        Assert.True(callIndex < clearIndex && clearIndex < unlockIndex);
        var busyJump = Assert.Single(instructions, i => i.Mnemonic == Mnemonic.Jne);
        Assert.True(busyJump.NearBranch32 > instructions[unlockIndex].IP);
        var emptyJump = Assert.Single(instructions, i => i.Mnemonic == Mnemonic.Je);
        Assert.True(emptyJump.NearBranch32 > instructions[clearIndex].IP && emptyJump.NearBranch32 < instructions[unlockIndex].IP);
        var dispatch = Decode(code.MustCode, 0x700, 0x800, 0x710000);
        Assert.Equal(0x700120u, dispatch[0].MemoryDisplacement64); // reload after lock, never use stale hook EAX.
        Assert.Contains(dispatch, i => i.Mnemonic == Mnemonic.Je && i.NearBranch32 == 0x720D00);
    }

    private static BootstrapCode Build() => new BootstrapCodeBuilder().Build(TestAssets.ReadBootstrapLines(), BootstrapCodeBuilderTests.FullScriptContext());
    private static List<Instruction> Decode(byte[] bytes, int start, int end, uint origin)
    {
        var limit = Math.Min(end, bytes.Length);
        var reader = new ByteArrayCodeReader(bytes[start..limit]);
        var decoder = Decoder.Create(32, reader); decoder.IP = origin + (uint)start;
        var result = new List<Instruction>();
        while (reader.CanReadByte)
        {
            var instruction = decoder.Decode();
            Assert.NotEqual(Code.INVALID, instruction.Code);
            // 零填充判定：只有“从这条指令起到本段末尾全为 0x00”时才截断。
            // 原来只要指令长得像 add [eax],al（00 00）就停 —— 真实代码里恰好出现该字节序列时会提前截断，
            // 之后按位置索引（instructions[3] 等）就落到错误的指令上、静默放行真实回归。
            if (IsZeroPadding(instruction, bytes, start, limit, origin)) break;
            result.Add(instruction);
        }
        return result;
    }

    /// <summary>
    /// 从 instruction 起、到本段末尾是否整段都是零填充（0x00）。真实代码里出现 00 00 时后面还有非零字节，
    /// 那时不算填充 —— 宁可让解码继续（并由各用例先断言 Count），也不静默截断。
    /// </summary>
    private static bool IsZeroPadding(Instruction instruction, byte[] bytes, int start, int limit, uint origin)
    {
        // 只有“全零字节”的 add [eax],al 才可能是零填充解码出来的形态。
        if (instruction.Code != Code.Add_rm8_r8 || instruction.MemoryBase != Register.EAX) return false;
        var offset = (int)(instruction.IP - (origin + (uint)start));
        for (var index = start + offset + instruction.Length; index < limit; index++)
            if (bytes[index] != 0) return false;
        return true;
    }

    private static void ApplyFault(CoordinateMemory memory, string fault)
    {
        switch (fault)
        {
            case "mouse-null": memory.Word("ra3_1.12.game+8DAEFC", 0); break;
            case "mouse-unaligned": memory.Word("ra3_1.12.game+8DAEFC", 0xF0001); break;
            case "mouse-page": memory.FaultAt = 0xF0000; break;
            case "mouse-field-page": memory.FaultAt = 0xF0044; break;
            case "player-null": memory.Word("ID", 0); break;
            case "player-field-page": memory.FaultAt = 0xD0010; break;
            case "manager-null": memory.Word("ra3_1.12.game+8E6C58", 0); break;
            case "type-zero": memory.Word(0x60008, 0); break;
            case "type-unknown": memory.Word(0x60008, 123); break;
            case "owner-null": memory.Word(0x50418, 0); break;
            case "selection-null": memory.Word(0x40138, 0); break;
            case "selection-health": memory.Word(0x70004, 0); break;
            // 未知故障名必须炸：否则故障驱动的用例会跑在健康夹具上，"反正抛了 InvalidOperationException" 依然成立，
            // 一条错别字（如 "mouse-null "）就会变成假绿。
            default: throw new ArgumentOutOfRangeException(nameof(fault), fault, "未知的故障注入名");
        }
    }

    internal static TrainerFeature Feature(string name) => TrainerFeatureCatalog.CreateUiFeatures(TestAssets.LoadManifest().Features).Single(f => f.RawName == name);

    private sealed class CoordinateMemory : IProcessMemory, IProbeMemory
    {
        private readonly FakeProcessMemory _inner = new();
        public AddressResolver Resolver { get; } = new(0x400000,
            new Dictionary<string, nint> { ["iEnable"] = 0x10000, ["ID"] = 0x11000, ["MustCode"] = 0x120000 });
        public nint Coordinates => Resolver.Resolve(RemoteStateLayout.WorldCoordinates);
        public int WriteCount, WritesAtConversionCompletion;
        public List<byte> Dispatches { get; } = [];
        public List<nint> HostWriteAddresses { get; } = [];
        public float[] Output = [4000, 4500, 200];
        public byte[]? ConvertedBytes, GenerationBytes;
        public bool HoldConversion, HoldGeneration, DropConversion, WrongEpoch, DropGeneration, CreationFails;
        public nint? FaultAt, ShortAt;
        public Action? AfterConversion;
        public CoordinateMemory()
        {
            SelectedObjectSafetyTests.Seed(_inner, Resolver);
            Word("ra3_1.12.game+8DAEFC", 0xF0000);
            Word("ID", 0xD0000);
            Word("ra3_1.12.game+8E6C58", 0xE0000);
            Word(RemoteStateLayout.SelectedUnitCode, ReinforcementSettings.DefaultUnitId);
            Word(0x50418, 0xD0000);
        }
        public void Word(string address, uint value) => Word(Resolver.Resolve(address), value);
        public void Word(nint address, uint value) => _inner.WriteBytes(address, BitConverter.GetBytes(value));
        public uint ReadWord(string address) => BitConverter.ToUInt32(_inner.ReadBytes(Resolver.Resolve(address), 4));
        public void SetCoordinates(float[] values)
        {
            for (var i = 0; i < values.Length; i++) Word(Coordinates + i * 4, BitConverter.SingleToUInt32Bits(values[i]));
        }
        public Win32MemoryRegion? Query(nint address) => address == FaultAt ? null : _inner.Query(address);
        public byte[] ReadBytes(nint address, int count) => _inner.ReadBytes(address, address == ShortAt && count == 4 ? 3 : count);
        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
        {
            WriteCount++; HostWriteAddresses.Add(address);
            if (address == Resolver.Resolve(RemoteStateLayout.ActionDispatch) && bytes[0] == 0x0E && DropConversion) return;
            if (address == Resolver.Resolve(RemoteStateLayout.ActionDispatch) && bytes[0] != 0x0E && DropGeneration) return;
            _inner.WriteBytes(address, bytes);
            if (address != Resolver.Resolve(RemoteStateLayout.ActionDispatch)) return;
            Dispatches.Add(bytes[0]);
            if (bytes[0] == 0x0E)
            {
                if (HoldConversion) return;
                SetCoordinates([0, 0, 0]); // Simulate the independently byte-verified A block, never host writes.
                SetCoordinates(Output);
                ConvertedBytes = _inner.ReadBytes(Coordinates, 12);
                Word("iEnable+44", ReadWord("iEnable+44") + (WrongEpoch ? 2u : 1u));
                Word("iEnable+48", ReadWord("iEnable+48") + 1);
                Word("iEnable+20", 0);
                WritesAtConversionCompletion = WriteCount;
                AfterConversion?.Invoke();
            }
            else
            {
                GenerationBytes = _inner.ReadBytes(Coordinates, 12);
                if (!HoldGeneration)
                {
                    Word("iEnable+48", ReadWord("iEnable+48") + 1);
                    Word("iEnable+4C", CreationFails ? 1u : 0u);
                    Word("iEnable+20", 0);
                }
            }
        }
    }
}
