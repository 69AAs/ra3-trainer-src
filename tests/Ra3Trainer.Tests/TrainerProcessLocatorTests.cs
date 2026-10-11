using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class TrainerProcessLocatorTests
{
    [Fact]
    public void FindMatchesTargetByUserProvidedFullModulePath()
    {
        var targetPath = @"D:\Games\RA3\Data\ra3_1.12.game";
        var locator = new TrainerProcessLocator(() => [
            new TrainerProcessCandidate(
                ProcessId: 42,
                ProcessName: "UnexpectedHostName",
                ModuleName: "ra3_1.12.game",
                ModulePath: targetPath,
                ModuleBase: 0x400000,
                Is32Bit: true,
                FileVersion: "1.12.3444.25830")
        ]);

        var target = locator.Find(targetPath);

        Assert.NotNull(target);
        Assert.Equal(42, target.ProcessId);
        Assert.Equal((nint)0x400000, target.ModuleBase);
        Assert.True(target.VersionSupported);
    }

    [Fact]
    public void FindMatchesTargetByModuleNameWhenProcessNameContainsDifferentSuffix()
    {
        var locator = new TrainerProcessLocator(() => [
            new TrainerProcessCandidate(
                ProcessId: 43,
                ProcessName: "ra3_1",
                ModuleName: "ra3_1.12.game",
                ModulePath: @"D:\Games\RA3\Data\ra3_1.12.game",
                ModuleBase: 0x500000,
                Is32Bit: true,
                FileVersion: "1.12.3444.25830")
        ]);

        var target = locator.Find("ra3_1.12.game");

        Assert.NotNull(target);
        Assert.Equal(43, target.ProcessId);
        Assert.True(target.VersionSupported);
    }

    [Fact]
    public void FindMarksMatchedTargetUnsupportedWhenFileVersionDoesNotMatchRa3112()
    {
        var locator = new TrainerProcessLocator(() => [
            new TrainerProcessCandidate(
                ProcessId: 44,
                ProcessName: "ra3_1.12",
                ModuleName: "ra3_1.12.game",
                ModulePath: @"D:\Games\RA3\Data\ra3_1.12.game",
                ModuleBase: 0x500000,
                Is32Bit: true,
                FileVersion: "1.12.9999.99999")
        ]);

        var target = locator.Find("ra3_1.12.game");

        Assert.NotNull(target);
        Assert.False(target.VersionSupported);
    }

    [Fact]
    public void FindDoesNotTreatUserProvidedFullModulePathAsVersionSupport()
    {
        var targetPath = @"D:\Games\RA3\Data\ra3_1.12.game";
        var locator = new TrainerProcessLocator(() => [
            new TrainerProcessCandidate(
                ProcessId: 45,
                ProcessName: "UnexpectedHostName",
                ModuleName: "ra3_1.12.game",
                ModulePath: targetPath,
                ModuleBase: 0x500000,
                Is32Bit: true,
                FileVersion: "")
        ]);

        var target = locator.Find(targetPath);

        Assert.NotNull(target);
        Assert.False(target.VersionSupported);
    }

    [Fact]
    public void FindAcceptsAny112BuildWhenCompatibilityModeIsEnabled()
    {
        var locator = new TrainerProcessLocator(() => [
            new TrainerProcessCandidate(
                ProcessId: 46,
                ProcessName: "ra3_1.12",
                ModuleName: "ra3_1.12.game",
                ModulePath: @"D:\Games\RA3\Data\ra3_1.12.game",
                ModuleBase: 0x500000,
                Is32Bit: true,
                FileVersion: "1.12.5000.12345")
        ])
        {
            AllowNonStandardVersion = true
        };

        var target = locator.Find("ra3_1.12.game");

        Assert.NotNull(target);
        Assert.True(target.VersionSupported);
        Assert.Equal("1.12.5000.12345", target.FileVersion);
    }

    [Fact]
    public void FindStillRejectsOtherProductLinesInCompatibilityMode()
    {
        var locator = new TrainerProcessLocator(() => [
            new TrainerProcessCandidate(
                ProcessId: 47,
                ProcessName: "ra3_1.12",
                ModuleName: "ra3_1.12.game",
                ModulePath: @"D:\Games\RA3\Data\ra3_1.12.game",
                ModuleBase: 0x500000,
                Is32Bit: true,
                FileVersion: "1.13.0.1")
        ])
        {
            AllowNonStandardVersion = true
        };

        var target = locator.Find("ra3_1.12.game");

        Assert.NotNull(target);
        Assert.False(target.VersionSupported);
    }

    [Fact]
    public void FindReturnsNullWhenNoCandidateIsTheRequestedModule()
    {
        // 全套测试里唯一没覆盖的、也是运行时最常见的一条路径：一个候选都不匹配时返回 null
        // （调用方据此报「没找到游戏进程」）。返回 null 与抛异常要先钉死，否则面板只会显示一句 generic 失败。
        // 注意 Matches 是「去掉 .game/.exe 后缀后全等」，所以候选必须真的与 RA3 无关，
        // 不能拿 ProcessName="ra3_1.12" 当反例（它本来就等于 "ra3_1.12.game" 去后缀的结果）。
        var locator = new TrainerProcessLocator(() => [
            new TrainerProcessCandidate(
                ProcessId: 48,
                ProcessName: "notepad",
                ModuleName: "notepad.exe",
                ModulePath: @"C:\Windows\System32\notepad.exe",
                ModuleBase: 0x700000,
                Is32Bit: true,
                FileVersion: "10.0.22621.1")
        ]);

        Assert.Null(locator.Find(@"D:\Games\RA3\Data\ra3_1.12.game"));
        Assert.Null(locator.Find("ra3_1.12.game"));
    }

    [Theory]
    [InlineData("1.12.3444.2583")]        // 少一段：不是原版那一串
    [InlineData("1.12.3444.25830.extra")] // 多一段
    [InlineData("1.12.3444.258300")]      // 数字多了
    public void FindPinsTheExactVersusSameProductLineBoundary(string version)
    {
        var strict = new TrainerProcessLocator(() => [Candidate(49, version)]);
        Assert.False(strict.Find("ra3_1.12.game")!.VersionSupported);

        var lenient = new TrainerProcessLocator(() => [Candidate(49, version)]) { AllowNonStandardVersion = true };
        Assert.True(lenient.Find("ra3_1.12.game")!.VersionSupported);
    }

    [Fact]
    public void FindPropagatesIs32BitAndModuleBaseFromTheCandidate()
    {
        var narrow = new TrainerProcessLocator(() => [Candidate(50, "1.12.3444.25830", is32Bit: true, moduleBase: 0x400000)])
            .Find("ra3_1.12.game");
        Assert.NotNull(narrow);
        Assert.True(narrow.Is32Bit);
        Assert.Equal((nint)0x400000, narrow.ModuleBase);

        // 64 位宿主（Is32Bit=false）：注入目标完全不同，不能被写死成 32 位。
        var wideBase = unchecked((nint)0x140000000L);
        var wide = new TrainerProcessLocator(() => [Candidate(52, "1.12.3444.25830", is32Bit: false, moduleBase: wideBase)])
            .Find("ra3_1.12.game");
        Assert.NotNull(wide);
        Assert.False(wide.Is32Bit);
        Assert.Equal(wideBase, wide.ModuleBase);
    }

    [Fact]
    public void FindSkipsLeadingUnrelatedCandidates()
    {
        var locator = new TrainerProcessLocator(() => [
            new TrainerProcessCandidate(49, "notepad", "notepad.exe", @"C:\Windows\System32\notepad.exe", 0x700000, true, "10.0.22621.1"),
            Candidate(53, "1.12.3444.25830")
        ]);

        var target = locator.Find("ra3_1.12.game");

        // FirstOrDefault 一旦 Matches 写错就会挑中前面那个无关进程（选错注入目标）。
        Assert.NotNull(target);
        Assert.Equal(53, target.ProcessId);
    }

    [Fact]
    public void FindMatchesModulePathCaseInsensitively()
    {
        var locator = new TrainerProcessLocator(() => [Candidate(54, "1.12.3444.25830")]);

        Assert.NotNull(locator.Find(@"d:\games\ra3\data\RA3_1.12.GAME"));
        Assert.NotNull(locator.Find("RA3_1.12.GAME"));
    }

    private static TrainerProcessCandidate Candidate(int processId, string fileVersion,
        bool is32Bit = true, nint moduleBase = 0x400000) =>
        new(processId, "ra3_1.12", "ra3_1.12.game", @"D:\Games\RA3\Data\ra3_1.12.game", moduleBase, is32Bit, fileVersion);
}
