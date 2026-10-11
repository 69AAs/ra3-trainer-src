using System.Text;
using Ra3Trainer.Core.Codegen;
using Ra3Trainer.Core.Manifest;
using Ra3Trainer.Core.Memory;
using Ra3Trainer.Core.Patching;
using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

/// <summary>
/// 全仓 OCR 评审（2026-10-07）第三批（Core 区）的回归钉。
/// 每条注释写清"改之前会怎样"，避免以后被当成可以随手放开的限制。
/// </summary>
public sealed class OcrRound38CoreTests
{
    [Fact]
    public void OriginalByteParserReportsOutOfRangeTokenInsteadOfOverflow()
    {
        // 正则接受任意长度的十六进制串（"db 1FF"），byte.Parse 会抛 OverflowException：
        // 清单写错时应该指出是哪一行哪个 token。
        var ex = Assert.Throws<InvalidOperationException>(() => OriginalByteParser.Parse(["db 1FF"]));

        Assert.Contains("1FF", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AaScriptReaderReportsOutOfRangeDbToken()
    {
        // 同一类问题在 .aa 脚本解析里：一份写坏的脚本原来会让整个 Read 抛 OverflowException。
        // 注意 .aa 里 [ENABLE] 之前的内容一律跳过，用例必须带上这个门。
        var ex = Assert.Throws<InvalidOperationException>(() => AaScriptReader.Read(["[ENABLE]", "iEnable+24:", "db 1FF"]));

        Assert.Contains("1FF", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AaScriptReaderDropsALabelWhoseNextLineIsNotADbLine()
    {
        // 标签后面不是 db 行 ⇒ 该标签作废；否则后面随便一个 db 行会被静默绑到这个标签上，
        // 生成错误的 iEnable 初值（比报错更糟：错了但没人知道）。
        var document = AaScriptReader.Read(["[ENABLE]", "iEnable+24:", "mov eax,1", "db 90 90 90 90"]);

        Assert.Empty(document.IEnableInitializers);
    }

    [Fact]
    public void AaScriptReaderStillBindsTheDbLineThatDirectlyFollowsItsLabel()
    {
        var document = AaScriptReader.Read(["[ENABLE]", "iEnable+24:", "db A0 A5 86 65"]);

        Assert.Equal(new byte[] { 0xA0, 0xA5, 0x86, 0x65 }, document.IEnableInitializers["iEnable+24"]);
    }

    [Fact]
    public void NearJumpEncoderReportsOutOfRangeAsInvalidOperationException()
    {
        // 原来 checked() 抛的是 OverflowException，而调用方只兜 InvalidOperationException。
        // 距离要真的超过 int.MaxValue：0x1000→0x7FFFFF00 与 →0x80000000 其实都还在范围内，别拿它们当反例。
        Assert.Throws<InvalidOperationException>(() =>
            X86PatchEncoder.EncodeNearJumpWithNops(0x1000, unchecked((nint)0x8000_2000L), 5));
    }

    [Fact]
    public void NearJumpEncoderStillEncodesAnOrdinaryJump()
    {
        var bytes = X86PatchEncoder.EncodeNearJumpWithNops(0x1000, 0x2000, 5);

        Assert.Equal(0xE9, bytes[0]);
        Assert.Equal(0x2000 - (0x1000 + 5), BitConverter.ToInt32(bytes, 1));
    }

    [Fact]
    public void SequentialAllocatorRefusesAZeroBaseInsteadOfHandingOutAddressZero()
    {
        // TrainerSession 没给分配器时的默认值就是基址 0：原来会把 0 当合法块地址发出去，
        // 之后写远程内存等于写空页（静默失败）。
        var allocator = new SequentialRemoteAllocator(0);

        Assert.Throws<InvalidOperationException>(() => allocator.Allocate(16));
    }

    [Fact]
    public void SequentialAllocatorStillHandsOutIncreasingBlocks()
    {
        var allocator = new SequentialRemoteAllocator(0x700000);

        Assert.Equal((nint)0x700000, allocator.Allocate(16));
        Assert.Equal((nint)0x700010, allocator.Allocate(16));
    }

    [Fact]
    public void ProcessLocatorTreatsANonPathRequestAsNoMatchInsteadOfThrowing()
    {
        // 设置里的 target_process 常是模块名或空串：GetFullPath 对它们抛 ArgumentException，
        // 逃到 UI 层就是"找游戏进程"按一下崩一次。
        var locator = new TrainerProcessLocator(() => []);

        Assert.Null(locator.Find(string.Empty));
        Assert.Null(locator.Find("ra3_1.12.game"));
    }

    [Fact]
    public void ManifestRepositoryNamesMissingTopLevelMembers()
    {
        // 少一个键原来就是一句 NullReferenceException（看不出少了什么）。
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""{"features":[],"action_dispatch":[]}"""));

        var ex = Assert.Throws<InvalidOperationException>(() => TrainerManifestRepository.Load(stream, "test.json"));

        Assert.Contains("trainer_metadata", ex.Message, StringComparison.Ordinal);
        Assert.Contains("patch_manifest", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HookPlanHandsOutCopiesSoRestoreCannotWriteBackMutatedBytes()
    {
        // 计划里的字节先被 Install 写进游戏内存、再被 RestoreAll 原样写回来：
        // 外部拿到同一个引用改一个字节，恢复写回的就是被改过的字节。
        var original = new byte[] { 1, 2, 3, 4, 5 };
        var plan = new PatchHookPlan("ra3_1.12.game+1000", "MustCode", 5, original);

        original[0] = 0xFF;
        Assert.Equal((byte)1, plan.OriginalBytes[0]);

        var handedOut = plan.OriginalBytes;
        handedOut[0] = 0xEE;
        Assert.Equal((byte)1, plan.OriginalBytes[0]);
    }

    [Fact]
    public void OverlayLogReaderResetsWhenTheFileIsTruncatedAndRegrownToTheSameLength()
    {
        // 只看 stream.Length < _position 判不出"截断后又写回同样长度"：
        // 旧实现会带着旧游标继续读，于是新写进去的那一行永远看不到。
        // （同一会话标记、只改内容、长度不变，正是最难判的那种重写。）
        var reader = new OverlayLogReader();
        var stream = new MemoryStream();

        static void Rewrite(Stream target, string text)
        {
            target.SetLength(0);
            var bytes = Encoding.UTF8.GetBytes(text);
            target.Position = 0;
            target.Write(bytes, 0, bytes.Length);
            target.Flush();
        }

        const string marker = "osd-session 0000000000000001\n";
        Rewrite(stream, marker + "A\n");
        Assert.Contains("A", reader.Read(stream, 1), StringComparison.Ordinal);

        Rewrite(stream, marker + "B\n");
        Assert.Contains("B", reader.Read(stream, 1), StringComparison.Ordinal);
    }

    [Fact]
    public void CrashEvidenceRootMatchingRequiresADirectoryBoundary()
    {
        // "C:\game" 曾会吞掉 "C:\games\x.log"：GetRelativePath 出来的是 "..\games\x.log"，
        // 打包时能把根目录之外的文件塞进归档（路径穿越）。
        Assert.True(CrashEvidenceCollector.IsUnderRoot(@"C:\game\RA3\logs\a.log", @"C:\game"));
        Assert.True(CrashEvidenceCollector.IsUnderRoot(@"C:\game", @"C:\game"));
        Assert.False(CrashEvidenceCollector.IsUnderRoot(@"C:\games\a.log", @"C:\game"));
    }
}
