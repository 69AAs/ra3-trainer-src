using Ra3Trainer.Core.Manifest;
using Ra3Trainer.Core.Memory;
using Ra3Trainer.Core.Patching;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class PatchHookInspectorTests
{
    [Fact]
    public void CaptureReadsExpectedAndActualHookBytes()
    {
        var memory = new FakeProcessMemory();
        memory.WriteBytes(0x70F530, new byte[] { 0xE9, 0x11, 0x22, 0x33, 0x44 });
        var resolver = new AddressResolver(0x400000, new Dictionary<string, nint>());
        var manifest = new PatchManifest([
            new PatchHook(
                Address: "ra3_1.12.game+30F530",
                SectionTitle: "Enemy Can't Build Code",
                PatchAssembly: ["jmp MustCode+3d0"],
                TrampolineTarget: "MustCode+3d0",
                ReturnLabel: "_BackEnemyCantBuild",
                EnableFlags: ["iEnable+15"],
                OriginalAssembly: ["add edx,[eax+04]", "cmp edx,edi"])
        ]);

        var snapshot = Assert.Single(PatchHookInspector.Capture(manifest, memory, resolver));

        Assert.Equal("ra3_1.12.game+30F530", snapshot.Address);
        Assert.Equal("Enemy Can't Build Code", snapshot.SectionTitle);
        Assert.Equal((nint)0x70F530, snapshot.AbsoluteAddress);
        Assert.Equal(new byte[] { 0x03, 0x50, 0x04, 0x3B, 0xD7 }, snapshot.ExpectedBytes);
        Assert.Equal(new byte[] { 0xE9, 0x11, 0x22, 0x33, 0x44 }, snapshot.ActualBytes);
        Assert.False(snapshot.Matches);
    }

    [Fact]
    public void CaptureReportsAMatchWhenMemoryHoldsExactlyTheExpectedBytes()
    {
        var memory = new FakeProcessMemory();
        memory.WriteBytes(0x70F530, new byte[] { 0x03, 0x50, 0x04, 0x3B, 0xD7 });
        var resolver = new AddressResolver(0x400000, new Dictionary<string, nint>());

        var snapshot = Assert.Single(PatchHookInspector.Capture(Manifest(), memory, resolver));

        Assert.True(snapshot.Matches); // 比较逻辑若退化成恒 false，这里会红
        Assert.Equal(snapshot.ExpectedBytes, snapshot.ActualBytes);
    }

    [Fact]
    public void CaptureSkipsHooksWithoutATrampolineTarget()
    {
        var memory = new FakeProcessMemory();
        memory.WriteBytes(0x70F530, new byte[] { 0xE9, 0x11, 0x22, 0x33, 0x44 });
        memory.WriteBytes(0x70F540, new byte[] { 0x03, 0x50, 0x04, 0x3B, 0xD7 });
        var resolver = new AddressResolver(0x400000, new Dictionary<string, nint>());
        var manifest = new PatchManifest([
            Manifest().Hooks[0],
            new PatchHook(
                Address: "ra3_1.12.game+30F540",
                SectionTitle: "Without trampoline",
                PatchAssembly: ["db 90"],
                TrampolineTarget: null,
                ReturnLabel: "_BackNone",
                EnableFlags: [],
                OriginalAssembly: ["add edx,[eax+04]", "cmp edx,edi"])
        ]);

        var snapshots = PatchHookInspector.Capture(manifest, memory, resolver);

        // TrampolineTarget 为 null 的 hook 不能进快照（补丁目标未知，读它没有意义）。
        var snapshot = Assert.Single(snapshots);
        Assert.Equal("ra3_1.12.game+30F530", snapshot.Address);
        Assert.DoesNotContain(snapshots, item => item.Address == "ra3_1.12.game+30F540");
    }

    [Fact]
    public void CaptureResolvesSymbolNamedHookAddressesThroughTheSymbolTable()
    {
        var memory = new FakeProcessMemory();
        memory.WriteBytes(0x7003D0, new byte[] { 0x03, 0x50, 0x04, 0x3B, 0xD7 });
        var resolver = new AddressResolver(0x400000, new Dictionary<string, nint> { ["MustCode"] = 0x700000 });
        var manifest = new PatchManifest([
            Manifest().Hooks[0] with { Address = "MustCode+3d0" }
        ]);

        var snapshot = Assert.Single(PatchHookInspector.Capture(manifest, memory, resolver));

        // 符号名分支：_symbols["MustCode"] + 0x3d0，而不是模块基址 + 偏移。
        Assert.Equal((nint)0x7003D0, snapshot.AbsoluteAddress);
    }

    private static PatchManifest Manifest() => new([
        new PatchHook(
            Address: "ra3_1.12.game+30F530",
            SectionTitle: "Enemy Can't Build Code",
            PatchAssembly: ["jmp MustCode+3d0"],
            TrampolineTarget: "MustCode+3d0",
            ReturnLabel: "_BackEnemyCantBuild",
            EnableFlags: ["iEnable+15"],
            OriginalAssembly: ["add edx,[eax+04]", "cmp edx,edi"])
    ]);
}
