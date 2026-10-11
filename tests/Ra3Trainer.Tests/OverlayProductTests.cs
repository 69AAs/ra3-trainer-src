using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class OverlayProductTests
{
    /// <summary>
    /// 映射名 (Local\Ra3Trainer.Overlay.v1.{targetPid}.{creation}) 用的是 targetPid。
    /// 原来用 <c>Random.Shared.Next(10000000, int.MaxValue)</c>：这个区间正是 Windows 分配真实 PID 的区间，
    /// 一旦撞上在线游戏，<c>MemoryMappedFile.CreateOrOpen</c> 会挂到生产映射上并覆盖它的序号/状态字段，
    /// <c>OpenExisting</c> 也会读到生产状态而不是刚建的槽（且会改动真实运行时状态）。
    /// 改用进程自身 PID：任何在线游戏的 PID 都不可能等于本测试进程的 PID（PID 在存活进程间唯一），
    /// 所以绝不会与生产映射冲突；同一进程内多次创建用不同的 creation 时间戳区分。
    /// </summary>
    private static int UniqueSlotTargetPid => Environment.ProcessId;

    [Fact]
    public void StatusLayout_HasSeparateVersionCountActionAndTerminatedUtf16()
    {
        var b = OverlayStatusSlot.Encode(12, 34, 56, true, true, 7, "1.12", "成功");
        Assert.Equal(356, b.Length);
        Assert.Equal(OverlayStatusSlot.Magic, U32(b, 0));
        Assert.Equal(356u, U32(b, 4)); Assert.Equal(0u, U32(b, 8));
        Assert.Equal(12u, U32(b, 12)); Assert.Equal(34u, U32(b, 16));
        Assert.Equal(56u, U32(b, 20)); Assert.Equal(1u, U32(b, 24));
        Assert.Equal(1u, U32(b, 28)); Assert.Equal(7u, U32(b, 32));
        Assert.Equal("1.12", Encoding.Unicode.GetString(b, 36, 64).TrimEnd('\0'));
        Assert.Equal("成功", Encoding.Unicode.GetString(b, 100, 256).TrimEnd('\0'));
    }

    [Fact]
    public void StatusLayout_TruncatesWithoutSplittingSurrogateOrLosingTerminator()
    {
        var b = OverlayStatusSlot.Encode(1, 2, uint.MaxValue, false, false, -1, new string('中', 30) + "😀", new string('文', 200));
        Assert.Equal(0u, U32(b, 24)); Assert.Equal(0u, U32(b, 28)); Assert.Equal(0u, U32(b, 32));
        Assert.Equal(new string('中', 30), Encoding.Unicode.GetString(b, 36, 64).TrimEnd('\0'));
        // TrimEnd('\0') 会把"干脆没写终止符"也掩盖过去：直接断言写入内容之后紧跟一个 0。
        Assert.Equal(0, b[36 + 30 * 2]);
        Assert.Equal(new string('文', 127), Encoding.Unicode.GetString(b, 100, 256).TrimEnd('\0'));
        Assert.Equal(0, b[100 + 127 * 2]);
    }

    [Fact]
    public void StatusDisconnect_PublishesDisabledAndKeepsExistingReaderViewAlive()
    {
        int pid = UniqueSlotTargetPid;
        using var slot = new OverlayStatusSlot(pid); // 任何断言失败也要释放映射，否则后续用例会读到脏序号
        using var reader = MemoryMappedFile.OpenExisting($@"Local\Ra3Trainer.Overlay.v1.{pid}.0000000000000000", MemoryMappedFileRights.Read);
        using var view = reader.CreateViewAccessor(0, OverlayStatusSlot.Size, MemoryMappedFileAccess.Read);
        slot.Publish(true, true, 3, "1.12", "完成");
        uint sequence = view.ReadUInt32(8);
        Assert.Equal(0u, sequence & 1); Assert.Equal(3u, view.ReadUInt32(32));
        slot.Dispose();
        Assert.Equal(0u, view.ReadUInt32(24)); Assert.Equal(0u, view.ReadUInt32(28));
        Assert.True(view.ReadUInt32(8) > sequence);
        Assert.Throws<NotSupportedException>(() => view.Write(24, 1));
    }

    [Fact]
    public void Cleanup_DeletesOnlyMatchingOwnedFileAndJournal()
    {
        WithFiles((root, target, journal, bytes) =>
        {
            File.WriteAllBytes(target, bytes); WriteJournal(target, journal, bytes);
            Assert.True(new OverlayDeployment(journal).Cleanup());
            Assert.False(File.Exists(target)); Assert.False(File.Exists(journal));
            Assert.True(new OverlayDeployment(journal).Cleanup());
        });
    }

    [Fact]
    public void Cleanup_LoadedOrLockedFileRetainsJournalAndRetries()
    {
        WithFiles((root, target, journal, bytes) =>
        {
            File.WriteAllBytes(target, bytes); WriteJournal(target, journal, bytes);
            var deployment = new OverlayDeployment(journal);
            using (var locked = File.Open(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            { Assert.False(deployment.Cleanup()); Assert.True(File.Exists(journal)); }
            Assert.True(deployment.Cleanup());
        });
    }

    [Fact]
    public void Cleanup_ForeignBytesAreNeverDeleted()
    {
        WithFiles((root, target, journal, bytes) =>
        {
            WriteJournal(target, journal, bytes); File.WriteAllText(target, "foreign proxy");
            Assert.Throws<IOException>(() => new OverlayDeployment(journal).Cleanup());
            Assert.Equal("foreign proxy", File.ReadAllText(target)); Assert.True(File.Exists(journal));
        });
    }

    [Fact]
    public void Cleanup_RecoversJournalWrittenBeforePublication()
    {
        WithFiles((root, target, journal, bytes) =>
        {
            WriteJournal(target, journal, bytes);
            Assert.True(new OverlayDeployment(journal).Cleanup());
            Assert.False(File.Exists(journal));
        });
    }

    [Fact]
    public void Cleanup_RecoversFullyWrittenStageBeforeRename()
    {
        WithFiles((root, target, journal, bytes) =>
        {
            WriteJournal(target, journal, bytes); File.WriteAllBytes(target + ".ra3trainer-stage", bytes);
            Assert.True(new OverlayDeployment(journal).Cleanup());
            Assert.False(File.Exists(target + ".ra3trainer-stage"));
            // 清理契约的另一半：把 stage 删掉之后日志也必须一起删干净。
            Assert.False(File.Exists(journal));
        });
    }

    [Fact]
    public void Deploy_RejectsUnsupportedImageWithoutCreatingJournalOrDll()
    {
        WithFiles((root, target, journal, bytes) =>
        {
            string game = Path.Combine(root, "RA3_1.12.game"); File.WriteAllBytes(game, bytes);
            Assert.ThrowsAny<Exception>(() => new OverlayDeployment(journal).Deploy(game, bytes));
            Assert.False(File.Exists(target)); Assert.False(File.Exists(journal));
        });
    }

    [Fact]
    public void Deploy_EmbedsOwnedBytesAndCleanupRestoresAbsence()
    {
        WithFiles((root, target, journal, bytes) =>
        {
            string game = Path.Combine(root, "RA3_1.12.game"); File.WriteAllBytes(game, GameImage());
            var deployment = new OverlayDeployment(journal);
            deployment.Deploy(game, bytes);
            Assert.Equal(bytes, File.ReadAllBytes(target)); Assert.True(File.Exists(journal));
            Assert.True(deployment.Cleanup()); Assert.False(File.Exists(target));
        });
    }

    [Fact]
    public void Deploy_ExistingProxyIsUntouched()
    {
        WithFiles((root, target, journal, bytes) =>
        {
            string game = Path.Combine(root, "RA3_1.12.game"); File.WriteAllBytes(game, GameImage());
            File.WriteAllText(target, "ReShade");
            Assert.Throws<IOException>(() => new OverlayDeployment(journal).Deploy(game, bytes));
            Assert.Equal("ReShade", File.ReadAllText(target)); Assert.False(File.Exists(journal));
        });
    }

    [Theory]
    [InlineData("UnsupportedExport")]
    [InlineData("Direct3DCreate9On12")]
    public void Deploy_UnsupportedImportFailsBeforeFileMutation(string name)
    {
        WithFiles((root, target, journal, bytes) =>
        {
            string game = Path.Combine(root, "RA3_1.12.game"); File.WriteAllBytes(game, GameImage(name));
            Assert.Throws<InvalidDataException>(() => new OverlayDeployment(journal).Deploy(game, bytes));
            Assert.False(File.Exists(target)); Assert.False(File.Exists(journal));
        });
    }

    private static byte[] GameImage(string import = "Direct3DCreate9")
    {
        var b = new byte[1024];
        void W16(int p, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), v);
        void W32(int p, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p), v);
        void Text(int p, string s) => Encoding.ASCII.GetBytes(s).CopyTo(b, p);
        W16(0, 0x5a4d); W32(0x3c, 0x80); W32(0x80, 0x4550);
        W16(0x84, 0x14c); W16(0x86, 1); W16(0x94, 224); W16(0x96, 0x102);
        W16(0x98, 0x10b); W32(0x98 + 28, 0x400000); W32(0x98 + 32, 0x1000);
        W32(0x98 + 36, 0x200); W32(0x98 + 56, 0x2000); W32(0x98 + 60, 0x200);
        W32(0x98 + 92, 16); W32(0x98 + 104, 0x1000); W32(0x98 + 108, 40);
        Text(0x178, ".rdata"); W32(0x180, 0x200); W32(0x184, 0x1000); W32(0x188, 0x200); W32(0x18c, 0x200);
        W32(0x200, 0x1080); W32(0x20c, 0x1060); W32(0x210, 0x1080);
        Text(0x260, "d3d9.dll"); W32(0x280, 0x10a0); Text(0x2a2, import);
        return b;
    }

    [Fact]
    public void Cleanup_PartialStageRequiresMatchingEmbeddedPrefix()
    {
        WithFiles((root, target, journal, bytes) =>
        {
            WriteJournal(target, journal, bytes); File.WriteAllBytes(target + ".ra3trainer-stage", bytes[..2]);
            Assert.True(new OverlayDeployment(journal).Cleanup(bytes));
            Assert.False(File.Exists(target + ".ra3trainer-stage"));
        });
    }

    [Fact]
    public void Cleanup_UnexpectedStagePrefixIsPreserved()
    {
        WithFiles((root, target, journal, bytes) =>
        {
            WriteJournal(target, journal, bytes); File.WriteAllBytes(target + ".ra3trainer-stage", [9, 8]);
            Assert.Throws<IOException>(() => new OverlayDeployment(journal).Cleanup(bytes));
            Assert.Equal(new byte[] { 9, 8 }, File.ReadAllBytes(target + ".ra3trainer-stage"));
        });
    }

    private static uint U32(byte[] b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset));

    [Fact]
    public void StatusReconnect_ContinuesSequenceOnRetainedReaderView()
    {
        int pid = UniqueSlotTargetPid;
        var first = new OverlayStatusSlot(pid, 123);
        using var reader = MemoryMappedFile.OpenExisting($@"Local\Ra3Trainer.Overlay.v1.{pid}.000000000000007B", MemoryMappedFileRights.Read);
        using var view = reader.CreateViewAccessor(0, OverlayStatusSlot.Size, MemoryMappedFileAccess.Read);
        first.Publish(true, true, 1, "v1", "first"); first.Dispose();
        uint old = view.ReadUInt32(8);
        using var second = new OverlayStatusSlot(pid, 123);
        second.Publish(true, true, 2, "v2", "second");
        Assert.True(view.ReadUInt32(8) > old); Assert.Equal(2u, view.ReadUInt32(32));
    }

    [Fact]
    public void StatusPidReuse_UsesDifferentCreationIdentity()
    {
        int pid = UniqueSlotTargetPid;
        using var old = new OverlayStatusSlot(pid, 1);
        using var current = new OverlayStatusSlot(pid, 2);
        old.Publish(true, true, 99, "old", "old"); current.Publish(false, false, 0, "new", "new");
        using var reader = MemoryMappedFile.OpenExisting($@"Local\Ra3Trainer.Overlay.v1.{pid}.0000000000000002", MemoryMappedFileRights.Read);
        using var view = reader.CreateViewAccessor(0, OverlayStatusSlot.Size, MemoryMappedFileAccess.Read);
        Assert.Equal(0u, view.ReadUInt32(32)); Assert.Equal(0u, view.ReadUInt32(24));
    }
    private static void WriteJournal(string target, string journal, byte[] bytes) =>
        File.WriteAllText(journal, JsonSerializer.Serialize(new OverlayDeploymentRecord(target, Convert.ToHexString(SHA256.HashData(bytes)))));
    private static void WithFiles(Action<string, string, string, byte[]> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "ra3-overlay-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { test(root, Path.Combine(root, "d3d9.dll"), Path.Combine(root, "journal.json"), [1, 2, 3, 4]); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
