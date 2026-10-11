using System.Diagnostics;

namespace Ra3Trainer.Core.Runtime;

public sealed class TrainerProcessLocator : IProcessLocator
{
    private readonly Func<IReadOnlyList<TrainerProcessCandidate>> _snapshotProcesses;

    /// <summary>
    /// Compatibility mode: accept any 1.12.x build, not only the exact build the hook addresses
    /// were derived from. Off by default, so detection behaves exactly as before unless the user
    /// turns it on (see <see cref="TrainerVersionPolicy"/>).
    /// </summary>
    public bool AllowNonStandardVersion { get; set; }

    public TrainerProcessLocator()
        : this(SnapshotProcesses)
    {
    }

    public TrainerProcessLocator(Func<IReadOnlyList<TrainerProcessCandidate>> snapshotProcesses)
    {
        _snapshotProcesses = snapshotProcesses;
    }

    public IReadOnlyList<TrainerProcessCandidate> Snapshot()
    {
        return _snapshotProcesses();
    }

    public TrainerTarget? Find(string processNameOrPath)
    {
        var candidates = Snapshot();
        var candidate = candidates.FirstOrDefault(candidate => Matches(candidate, processNameOrPath));
        if (candidate is null)
        {
            return null;
        }

        var supported = TrainerVersionPolicy.IsSupported(candidate.FileVersion, AllowNonStandardVersion);

        return new TrainerTarget(
            candidate.ModuleName,
            candidate.ModuleBase,
            candidate.Is32Bit,
            supported,
            candidate.ProcessId,
            candidate.ModulePath,
            candidate.FileVersion);
    }

    private static bool Matches(TrainerProcessCandidate candidate, string processNameOrPath)
    {
        return TrainerProcessName.Matches(candidate.ProcessName, processNameOrPath)
            || TrainerProcessName.Matches(candidate.ModuleName, processNameOrPath)
            || ModulePathMatches(candidate.ModulePath, processNameOrPath);
    }

    private static bool ModulePathMatches(string modulePath, string processNameOrPath)
    {
        // 用户设置里的 target_process 常常是模块名（"ra3_1.12.game"）而不是路径：
        // GetFullPath 对空串/非法字符会抛 ArgumentException / NotSupportedException，
        // 逃到 UI 层就是"找游戏进程"按一下崩一次。比不了就当不匹配。
        try
        {
            return Path.GetFullPath(modulePath)
                .Equals(Path.GetFullPath(processNameOrPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static IReadOnlyList<TrainerProcessCandidate> SnapshotProcesses()
    {
        var candidates = new List<TrainerProcessCandidate>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var module = process.MainModule;
                if (module is null)
                {
                    continue;
                }

                candidates.Add(new TrainerProcessCandidate(
                    process.Id,
                    process.ProcessName,
                    module.ModuleName,
                    module.FileName,
                    module.BaseAddress,
                    IsProcess32Bit(process),
                    module.FileVersionInfo.FileVersion ?? string.Empty));
            }
            catch
            {
                // Some system processes deny module access. They are not valid RA3 targets.
            }
            finally
            {
                process.Dispose();
            }
        }

        return candidates;
    }

    private static bool IsProcess32Bit(Process process)
    {
        if (!Environment.Is64BitOperatingSystem)
        {
            return true;
        }

        return IsWow64Process(process.Handle, out var isWow64) && isWow64;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(nint process, out bool wow64Process);
}
