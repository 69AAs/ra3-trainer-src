using System.ComponentModel;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Ra3Trainer.Core.Runtime;

public sealed record OverlayDeploymentRecord(string Target, string Sha256);

/// <summary>Ownership journal precedes atomic publication. Existing files are never replaced.</summary>
public sealed class OverlayDeployment
{
    private readonly string _journal;
    public OverlayDeployment(string journal) => _journal = Path.GetFullPath(journal);
    public static string JournalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ra3Trainer", "overlay-deployment.json");

    public void Deploy(string gameImage, byte[] payload)
    {
        gameImage = Path.GetFullPath(gameImage);
        ValidateImports(gameImage);
        if (File.Exists(_journal)) throw new IOException("OSD 尚有清理记录，保留原文件并拒绝再次部署。");
        var target = Path.Combine(Path.GetDirectoryName(gameImage)!, "d3d9.dll");
        if (File.Exists(target)) throw new IOException("已有 d3d9.dll，拒绝覆盖或串接。");
        var stage = target + ".ra3trainer-stage";
        if (File.Exists(stage)) throw new IOException("已有未知部署暂存文件，拒绝覆盖。");
        var record = new OverlayDeploymentRecord(target, Convert.ToHexString(SHA256.HashData(payload)));
        Directory.CreateDirectory(Path.GetDirectoryName(_journal)!);
        // No gap in crash recovery: journal first, then exclusive creation and rename.
        using (var journal = new FileStream(_journal, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            JsonSerializer.Serialize(journal, record);
            journal.Flush(true);
        }
        using (var file = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.Write(payload); file.Flush(true);
        }
        File.Move(stage, target, overwrite: false);
    }

    public bool Cleanup(byte[]? expectedPayload = null)
    {
        if (!File.Exists(_journal)) return true;
        var journalText = File.ReadAllText(_journal);
        if (string.IsNullOrWhiteSpace(journalText))
        {
            // 崩在写日志中间：日志里没记下任何已落地的文件（日志先写、File.Move 在后），
            // 所以可以当成"没部署过"直接丢掉。不丢的话这个空文件会让 Cleanup 永久拒绝、
            // Deploy 一直报"尚有清理记录"，覆盖层再也装不上。
            File.Delete(_journal);
            return true;
        }

        OverlayDeploymentRecord record;
        try
        {
            record = JsonSerializer.Deserialize<OverlayDeploymentRecord>(journalText)
                ?? throw new JsonException("journal body is null");
        }
        catch (JsonException ex)
        {
            // 有内容但读不出来 ⇒ 不知道到底落地没有，不猜：按声明的 InvalidDataException 报出去，
            // 让调用方去问人（原来这里抛的是 JsonException，逃出 Cleanup 后连日志文件都不会删）。
            throw new InvalidDataException($"OSD 部署日志损坏，无法判断是否已落地 d3d9.dll；请人工确认后删掉 {_journal}", ex);
        }

        if (!Path.IsPathFullyQualified(record.Target) || Path.GetFileName(record.Target) != "d3d9.dll"
            || record.Sha256.Length != 64 || !IsHex(record.Sha256))
            throw new InvalidDataException("Invalid OSD cleanup target.");
        // Delete by the verified handle, never by a path reopened after hash validation.
        if (!DeleteOwned(record.Target, record.Sha256) || !DeleteOwned(record.Target + ".ra3trainer-stage", record.Sha256, expectedPayload)) return false;
        File.Delete(_journal);
        return true;
    }

    /// <summary>
    /// 日志文件谁都能写（在 %LocalAppData% 下，同一个用户的低权限进程也能放），
    /// 而 Cleanup 会按这里的路径去删文件：Sha256 只校验了长度，不校验是不是十六进制，
    /// 那就等于"照抄一份目标文件的真实哈希"就能让修改器替你去删任意 d3d9.dll。
    /// 这里先按十六进制卡死（真正的收紧是别信日志里的路径，见交接文档"待办"）。
    /// </summary>
    private static bool IsHex(string text)
    {
        foreach (var c in text)
        {
            if (!Uri.IsHexDigit(c)) return false;
        }
        return true;
    }

    private static bool DeleteOwned(string path, string hash, byte[]? partialPayload = null)
    {
        using var handle = CreateFileW(path, 0x80000000 | 0x00010000, 1, 0, 3, 0x80, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 3) return true;
            if (error is 5 or 32 or 33) return false; // loaded/locked: journal retained for retry
            throw new Win32Exception(error);
        }
        using var stream = new FileStream(handle, FileAccess.Read);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase))
        {
            // Interrupted stage writes are recoverable only against the same embedded
            // payload and its exact byte prefix. This never applies to d3d9.dll itself.
            bool partial = partialPayload is not null && stream.Length < partialPayload.Length &&
                Convert.ToHexString(SHA256.HashData(partialPayload)).Equals(hash, StringComparison.OrdinalIgnoreCase);
            if (partial)
            {
                stream.Position = 0;
                var prefix = new byte[checked((int)stream.Length)]; stream.ReadExactly(prefix);
                partial = prefix.AsSpan().SequenceEqual(partialPayload.AsSpan(0, prefix.Length));
            }
            if (!partial) throw new IOException("OSD 文件所有权不符，拒绝删除；保留清理记录。");
        }
        var delete = 1;
        if (!SetFileInformationByHandle(handle, 4, ref delete, 4)) return false;
        return true;
    }

    public static void ValidateImports(string gameImage)
    {
        using var stream = File.OpenRead(gameImage);
        using var pe = new PEReader(stream);
        if (pe.PEHeaders.CoffHeader.Machine != Machine.I386 || pe.PEHeaders.PEHeader is not { } header)
            throw new InvalidDataException("OSD 仅支持 x86 游戏。");
        if (header.DelayImportTableDirectory.Size != 0) throw new InvalidDataException("OSD 尚未验证延迟导入，拒绝部署。");
        var imports = header.ImportTableDirectory;
        bool found = false;
        string ReadName(int rva)
        {
            var reader = pe.GetSectionData(rva).GetReader();
            var data = new List<byte>();
            for (var i = 0; i < 256; i++) { var b = reader.ReadByte(); if (b == 0) return System.Text.Encoding.ASCII.GetString(data.ToArray()); data.Add(b); }
            throw new InvalidDataException("Unterminated import name.");
        }
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "Direct3DCreate9", "Direct3DCreate9Ex", "D3DPERF_BeginEvent", "D3DPERF_EndEvent", "D3DPERF_GetStatus", "D3DPERF_QueryRepeatFrame", "D3DPERF_SetMarker", "D3DPERF_SetRegion", "D3DPERF_SetOptions" };
        for (int offset = 0; offset + 20 <= imports.Size; offset += 20)
        {
            var entry = pe.GetSectionData(imports.RelativeVirtualAddress + offset).GetReader(0, 20);
            int lookup = entry.ReadInt32(); entry.ReadInt32(); entry.ReadInt32(); int name = entry.ReadInt32(); int iat = entry.ReadInt32();
            if (name == 0) break;
            if (!ReadName(name).Equals("d3d9.dll", StringComparison.OrdinalIgnoreCase)) continue;
            found = true;
            var table = pe.GetSectionData(lookup == 0 ? iat : lookup).GetReader();
            for (int i = 0; i < 256; i++)
            {
                uint thunk = table.ReadUInt32();
                if (thunk == 0) break;
                if ((thunk & 0x80000000) != 0 || !allowed.Contains(ReadName(checked((int)thunk + 2))))
                    throw new InvalidDataException("OSD 不支持该 D3D9 导入，拒绝部署。");
                if (i == 255) throw new InvalidDataException("Too many imports.");
            }
        }
        if (!found) throw new InvalidDataException("未确认 d3d9.dll 静态加载路径，拒绝猜测部署。");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref int data, uint size);
}
