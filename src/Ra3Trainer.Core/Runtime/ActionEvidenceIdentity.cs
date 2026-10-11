using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ra3Trainer.Core.Runtime;

public sealed record IdentityValue(string? Value, string Status);
public sealed record OperatorEvidenceContext(string? ModName = null, string? ModVersion = null,
    string? ModContentSha256 = null, string? MapIdentity = null, int? InitialUnitCount = null,
    string? DeclaredUtc = null, string? Notes = null);
public sealed record ActionEvidenceIdentity(string AttachSessionId, int? Pid, IdentityValue ProcessStart,
    IdentityValue TrainerSha256, IdentityValue BootstrapSha256, OperatorEvidenceContext OperatorContext,
    string OperatorContextStatus, DateTimeOffset CapturedUtc)
{
    // Local declaration, never remote scanning. Content SHA is declared, not silently treated as verified.
    public static ActionEvidenceIdentity Capture(int? pid, string contextPath)
    {
        IdentityValue Read(Func<string> read)
        {
            try { return new(read(), "observed"); }
            catch (Exception ex) { return new(null, "unavailable: " + ex.Message); }
        }
        var start = Read(() => { using var p = Process.GetProcessById(pid ?? -1); return p.StartTime.ToUniversalTime().ToString("O"); });
        // ProcessPath 在某些宿主（单文件/特殊加载）下会是 null：原来直接 ! 解引用会抛，
        // 让整份身份取证变成一条崩溃日志。
        var trainerPath = Environment.ProcessPath;
        var trainer = trainerPath is null
            ? new IdentityValue(null, "unavailable: 无法定位修改器自身路径")
            : Read(() => { using var file = File.OpenRead(trainerPath); return Convert.ToHexString(SHA256.HashData(file)); });
        var bootstrap = Read(() =>
        {
            using var stream = typeof(TrainerRuntimeAssets).Assembly.GetManifestResourceStream("Ra3Trainer.Core.Assets.00_bootstrap.aa")
                ?? throw new IOException("bootstrap resource missing");
            return Convert.ToHexString(SHA256.HashData(stream));
        });
        var context = new OperatorEvidenceContext();
        var status = "unavailable: 未提供本次 mod 名称/版本/内容 SHA、地图、初始单位量；禁止推断";
        try
        {
            if (File.Exists(contextPath))
            {
                // 用流限制读取：先 FileInfo.Length 再 ReadAllText 之间有 TOCTOU 窗口
                // （文件可以在两次调用之间长大/被替换）。解析顺手加 MaxDepth。
                using var stream = new FileStream(contextPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Length > 65536) throw new IOException("context exceeds 64 KiB");
                context = JsonSerializer.Deserialize<OperatorEvidenceContext>(stream,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 16 }) ?? new();
                status = "operator-declared-unverified; null fields unavailable; must match this attach/session";
            }
        }
        catch (Exception ex) { status = "unavailable: " + ex.Message; }
        return new(Guid.NewGuid().ToString("N"), pid, start, trainer, bootstrap, context, status, DateTimeOffset.UtcNow);
    }
}
