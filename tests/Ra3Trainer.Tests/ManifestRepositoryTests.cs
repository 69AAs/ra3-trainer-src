using Ra3Trainer.Core.Patching;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class ManifestRepositoryTests
{
    [Fact]
    public void LoadReadsParsedTrainerArtifacts()
    {
        var manifest = TestAssets.LoadManifest();

        Assert.Equal("ra3_1.12.game", manifest.TargetProcess);
        Assert.Equal(32, manifest.Features.Count);
        Assert.Equal(22, manifest.PatchManifest.Hooks.Count);
        Assert.Equal(13, manifest.ActionDispatch.Count);

        var money = Assert.Single(manifest.Features, feature => feature.RawName == "Moeny");
        Assert.Equal("Money", money.DisplayName);
        Assert.Equal("Ctrl+F1", money.Hotkey);
        Assert.Equal(new[] { "iEnable+8" }, money.EnableFlags);

        var destroy = Assert.Single(manifest.Features, feature => feature.RawName == "Destory Select Unit");
        Assert.Equal("Destroy Select Unit", destroy.DisplayName);
        Assert.Equal("MustCode2+900", destroy.DispatchTarget);

        Assert.Equal(
            new[] { "iEnable+13" },
            Assert.Single(manifest.Features, feature => feature.RawName == "Danger Level MIN").EnableFlags);
        Assert.Equal(
            new[] { "iEnable+14" },
            Assert.Single(manifest.Features, feature => feature.RawName == "Restore Select Ore Mine").EnableFlags);
    }

    /// <summary>22 个 hook 的恢复字节逐条钉死：只断言"长度>=5 且有非零字节"的话，
    /// 解析器退化成"随便回 5 个字节"也照样绿。</summary>
    private static readonly IReadOnlyDictionary<string, string> ExpectedRestoreBytes = new Dictionary<string, string>
    {
        ["ra3_1.12.game+FF95B"] = "8B50288B4220",
        ["ra3_1.12.game+6CFDFE"] = "0378048B11",
        ["ra3_1.12.game+6CFD01"] = "8D482039095B",
        ["ra3_1.12.game+6CFE5E"] = "8B8020130000",
        ["ra3_1.12.game+6CFEB5"] = "F30F10472C",
        ["ra3_1.12.game+30F42E"] = "F30F2C461C",
        ["ra3_1.12.game+30F38C"] = "D9461C89465C",
        ["ra3_1.12.game+2F6CFF"] = "D986BC010000",
        ["ra3_1.12.game+2EBB69"] = "8B9818040000",
        ["ra3_1.12.game+43EC19"] = "8B70508B01",
        ["ra3_1.12.game+43ECA6"] = "8B51503B500C",
        ["ra3_1.12.game+30A5F6"] = "8B48503B4E20",
        ["ra3_1.12.game+1EC7CD"] = "F30F114644",
        ["ra3_1.12.game+3C226C"] = "F30F118560020000",
        ["ra3_1.12.game+128735"] = "8B0CBA85C9",
        ["ra3_1.12.game+438ED0"] = "8B8178120000",
        ["ra3_1.12.game+2D4933"] = "8B40082B4114",
        ["ra3_1.12.game+30F530"] = "0350043BD7",
        ["ra3_1.12.game+12EEDF"] = "8B503C8BCE",
        ["ra3_1.12.game+3651AE"] = "F30F114604",
        ["ra3_1.12.game+3FE714"] = "8B8E3C030000",
        ["ra3_1.12.game+2E24E3"] = "8B893C030000",
    };

    [Fact]
    public void EveryPatchRestoreAssemblyEncodesToItsExactGoldenBytes()
    {
        var manifest = TestAssets.LoadManifest();

        Assert.Equal(ExpectedRestoreBytes.Count, manifest.PatchManifest.Hooks.Count);
        foreach (var hook in manifest.PatchManifest.Hooks)
        {
            Assert.True(ExpectedRestoreBytes.TryGetValue(hook.Address, out var expected), hook.Address + " 不在黄金表里");
            Assert.Equal(expected, Convert.ToHexString(OriginalByteParser.Parse(hook.OriginalAssembly)));
        }
    }

    [Fact]
    public void MalformedRestoreAssemblyIsRejectedInsteadOfSilentlyDegrading()
    {
        Assert.Throws<ArgumentNullException>(() => OriginalByteParser.Parse(null!));
        Assert.Throws<InvalidOperationException>(() => OriginalByteParser.Parse([null!]));
        // "db 1FF" 能过正则但超出单字节范围：必须报出原文，而不是让 byte.Parse 抛 OverflowException。
        Assert.Throws<InvalidOperationException>(() => OriginalByteParser.Parse(["db 1FF"]));
    }

    [Fact]
    public void AllPatchRestoreAssemblyEncodesToConcreteBytes()
    {
        var manifest = TestAssets.LoadManifest();

        foreach (var hook in manifest.PatchManifest.Hooks)
        {
            var bytes = OriginalByteParser.Parse(hook.OriginalAssembly);

            Assert.True(bytes.Length >= 5, $"{hook.Address} restore bytes are too short.");
            Assert.Contains(bytes, value => value != 0);
        }
    }
}
