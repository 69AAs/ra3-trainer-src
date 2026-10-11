using System.Text;
using Ra3Trainer.Core.Manifest;
using Ra3Trainer.Core.Memory;
using Ra3Trainer.Core.Patching;
using Xunit;

namespace Ra3Trainer.Tests;

/// <summary>
/// 全仓 OCR 评审（2026-10-07）里"会静默改坏/静默放过"那一类的回归钉。
/// 每条都对应一个真实存在过的行为，注释里写清"改之前会怎样"。
/// </summary>
public sealed class OcrRound38PatchingTests
{
    [Fact]
    public void MapRvaRejectsRawOffsetThatWouldOverflowInt()
    {
        // 节头里的 RawPointer 也是文件内容：0x7FFFFF00 + 0x200 在 int 里回绕成负数，
        // 于是 rawOffset + count > _bytes.Length 判成 false，越界映射被当成合法返回，
        // 接着 ReadRva 的 Array.Copy 从文件外读（或 count 极大时直接分配巨大数组）。
        var bytes = CreatePeImage(rawPointer: 0x7FFFFF00);
        var image = PeImage.FromBytes(bytes);

        var ex = Assert.Throws<InvalidOperationException>(() => image.MapRva(0x1000, 0x200));
        Assert.Contains("outside the PE file", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadImageBaseThrowsDocumentedExceptionOnTruncatedPe32PlusHeader()
    {
        // 0x20B（PE32+）分支原来直接 bytes.AsSpan(...)，没有 EnsureRange：
        // 可选头被截断时抛的是 ArgumentOutOfRangeException，而调用方只按
        // InvalidOperationException("Unexpected end of PE file.") 兜底。
        var bytes = CreatePeImage(magic: 0x20B, optionalHeaderSize: 0, totalLength: 0xA0);

        Assert.Throws<InvalidOperationException>(() => PeImage.FromBytes(bytes));
    }

    [Fact]
    public void ReadHeadersThrowsDocumentedExceptionWhenPeHeaderOffsetWraps()
    {
        // 0x3C 处的 PE 头偏移读进来就是 int：int.MaxValue-1 时 offset + 4 回绕成负数，
        // 绕过 EnsureRange，最后是被 AsSpan 以 ArgumentOutOfRangeException 打回来。
        var bytes = CreatePeImage();
        WriteUInt32(bytes, 0x3C, int.MaxValue - 1);

        Assert.Throws<InvalidOperationException>(() => PeImage.FromBytes(bytes));
    }

    [Fact]
    public void ImageVerifierRefusesToCallAnEmptyComparisonAMatch()
    {
        // original_assembly 为空（JSON 里合法）：期望字节 0 个、镜像字节 0 个，
        // SequenceEqual 空对空返回 true ⇒ 从没比过的东西被标成 "Matches = true"。
        var image = PeImage.FromBytes(CreatePeImage());
        var manifest = new PatchManifest([
            new PatchHook(
                Address: "ra3_1.12.game+1000",
                SectionTitle: "Empty Original",
                PatchAssembly: ["jmp MustCode"],
                TrampolineTarget: "MustCode",
                ReturnLabel: "_Back",
                EnableFlags: [],
                OriginalAssembly: [])
        ]);

        var result = Assert.Single(PatchHookImageVerifier.Verify(manifest, image));

        Assert.Empty(result.ExpectedBytes);
        Assert.False(result.Matches);
    }

    [Fact]
    public void InspectorRefusesToCallAnEmptyComparisonAMatch()
    {
        var memory = new FakeProcessMemory();
        var resolver = new AddressResolver(0x400000, new Dictionary<string, nint>());
        var manifest = new PatchManifest([
            new PatchHook(
                Address: "ra3_1.12.game+1000",
                SectionTitle: "Empty Original",
                PatchAssembly: ["jmp MustCode"],
                TrampolineTarget: "MustCode",
                ReturnLabel: "_Back",
                EnableFlags: [],
                OriginalAssembly: [])
        ]);

        var snapshot = Assert.Single(PatchHookInspector.Capture(manifest, memory, resolver));

        Assert.Empty(snapshot.ExpectedBytes);
        Assert.False(snapshot.Matches);
    }

    [Fact]
    public void PlannerNamesTheHookWhenOriginalBytesCannotHoldAJump()
    {
        // 原来写 Math.Max(5, ...)：不足 5 字节被补成 5，PatchLength 就永远 != OriginalBytes.Length，
        // 而 PatchEngine.Install 校验这两者相等 —— 报错被推到安装阶段，且看不出是哪个 hook。
        var manifest = new PatchManifest([
            new PatchHook(
                Address: "ra3_1.12.game+ABCDEF",
                SectionTitle: "Too Short",
                PatchAssembly: ["jmp MustCode"],
                TrampolineTarget: "MustCode",
                ReturnLabel: "_Back",
                EnableFlags: [],
                OriginalAssembly: ["db 90"])
        ]);

        var ex = Assert.Throws<InvalidOperationException>(() => PatchHookPlanner.CreatePlans(manifest));
        Assert.Contains("ra3_1.12.game+ABCDEF", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryShippedHookPlanKeepsTheLengthInvariantInstallEnforces()
    {
        // 真正清单里所有 hook：计划长度必须等于原始字节数（Install 的校验），且 >= 5（jmp rel32 的落地空间）。
        var manifest = TestAssets.LoadManifest();

        var plans = PatchHookPlanner.CreatePlans(manifest.PatchManifest);

        Assert.NotEmpty(plans);
        Assert.All(plans, plan =>
        {
            Assert.Equal(plan.OriginalBytes.Length, plan.PatchLength);
            Assert.True(plan.PatchLength >= 5, $"{plan.Address} 只有 {plan.PatchLength} 字节");
        });
    }

    [Theory]
    [InlineData("iEnable+FFFFFFFF")] // 超过 int 范围 → Convert.ToInt32 抛 OverflowException
    [InlineData("iEnable+")]
    [InlineData("iEnable+ZZ")]
    public void AddressResolverReportsBadOffsetsAsInvalidOperationException(string expression)
    {
        var resolver = new AddressResolver(0x400000, new Dictionary<string, nint>());

        // 全类只抛 InvalidOperationException，调用方（如 ReadOnlyProbe.ResolveOptional）按这个类型兜底；
        // Format/OverflowException 逃出去就是一次未处理异常。
        Assert.Throws<InvalidOperationException>(() => resolver.Resolve(expression));
    }

    [Fact]
    public void ManifestRepositoryNamesTheMissingHookFieldInsteadOfNullReference()
    {
        const string json = """
            {
              "trainer_metadata": { "target_process": "ra3_1.12.game" },
              "features": [],
              "patch_manifest": {
                "hooks": [
                  {
                    "address": "ra3_1.12.game+1030",
                    "section_title": "Player God Mode Code",
                    "original_assembly": ["db 90 90 90 90 90"]
                  }
                ]
              },
              "action_dispatch": []
            }
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var ex = Assert.Throws<InvalidOperationException>(() => TrainerManifestRepository.Load(stream, "test.json"));

        Assert.Contains("patch_assembly", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ra3_1.12.game+1030", ex.Message, StringComparison.Ordinal);
    }

    // —— 最小 PE 构造（与 PeImageTests 同一套布局，这里额外可控几个字段）——

    private static byte[] CreatePeImage(
        int rawPointer = 0x200,
        ushort magic = 0x10B,
        ushort optionalHeaderSize = 0xE0,
        int totalLength = 0x400)
    {
        var bytes = new byte[totalLength];
        WriteUInt32(bytes, 0x3C, 0x80);
        bytes[0x80] = (byte)'P';
        bytes[0x81] = (byte)'E';
        WriteUInt16(bytes, 0x84, 0x14C);
        WriteUInt16(bytes, 0x86, 1);
        WriteUInt16(bytes, 0x94, optionalHeaderSize);
        var optionalHeader = 0x80 + 24;
        WriteUInt16(bytes, optionalHeader, magic);
        if (optionalHeader + 60 <= bytes.Length)
        {
            // 截断用例（totalLength 故意小于可选头）下这两处不写：让读取那一步去撞边界。
            WriteUInt32(bytes, optionalHeader + 28, 0x400000);
            WriteUInt32(bytes, optionalHeader + 56, 0x900000);
        }

        var section = 0x80 + 24 + optionalHeaderSize;
        if (section + 40 <= bytes.Length)
        {
            var name = ".text"u8.ToArray();
            Array.Copy(name, 0, bytes, section, name.Length);
            WriteUInt32(bytes, section + 8, 0x200);        // VirtualSize
            WriteUInt32(bytes, section + 12, 0x1000);      // VirtualAddress
            WriteUInt32(bytes, section + 16, 0x200);       // RawSize
            WriteUInt32(bytes, section + 20, (uint)rawPointer);
        }

        return bytes;
    }

    private static void WriteUInt16(byte[] bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteUInt32(byte[] bytes, int offset, uint value)
    {
        // int.MaxValue-1 这类值要按无符号位写进去
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16);
        bytes[offset + 3] = (byte)(value >> 24);
    }
}
