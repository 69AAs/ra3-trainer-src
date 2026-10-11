using Ra3Trainer.Core.Memory;

namespace Ra3Trainer.Core.Patching;

public sealed class PatchEngine
{
    private readonly IProcessMemory _memory;
    private readonly AddressResolver _resolver;
    private readonly List<(nint Address, byte[] OriginalBytes)> _installed = new();
    // Install/RestoreAll/查询可能来自不同线程（面板线程 + 心跳/退出路径）：
    // 原来 List<> 的 Add/RemoveAll/RemoveAt/枚举全无同步，并发时会抛或读到半更新状态。
    private readonly object _installedGate = new();

    public PatchEngine(IProcessMemory memory, AddressResolver resolver)
    {
        _memory = memory;
        _resolver = resolver;
    }

    public int InstalledHookCount
    {
        get { lock (_installedGate) return _installed.Count; }
    }

    public IReadOnlyList<(nint Address, int Size)> InstalledRanges
    {
        get { lock (_installedGate) return _installed.Select(hook => (hook.Address, hook.OriginalBytes.Length)).ToArray(); }
    }

    public void Install(IEnumerable<PatchHookPlan> hooks)
    {
        var hookList = hooks.ToArray();
        // Reject malformed plans before the first jump is written. Restoration must
        // cover exactly the same bytes as installation.
        for (var index = 0; index < hookList.Length; index++)
        {
            var hook = hookList[index];
            if (hook.PatchLength < 5 || hook.OriginalBytes.Length != hook.PatchLength)
                throw new PatchInstallException(hook, index + 1, hookList.Length,
                    _resolver.Resolve(hook.Address), new InvalidOperationException("Hook 长度与恢复字节不一致，已拒绝安装。"));
        }
        PatchInstallException? installException = null;
        try
        {
            for (var index = 0; index < hookList.Length; index++)
            {
                var hook = hookList[index];
                var address = _resolver.Resolve(hook.Address);
                try
                {
                    var currentBytes = _memory.ReadBytes(address, hook.OriginalBytes.Length);
                    var target = _resolver.Resolve(hook.Target);
                    var patchBytes = X86PatchEncoder.EncodeNearJumpWithNops(address, target, hook.PatchLength);
                    if (!currentBytes.SequenceEqual(hook.OriginalBytes))
                    {
                        if (currentBytes.SequenceEqual(patchBytes))
                        {
                            AddInstalled(address, hook.OriginalBytes);
                            continue;
                        }

                        throw new InvalidOperationException(
                            $"Original bytes do not match at {hook.Address} (absolute 0x{address:X}); " +
                            $"expected {FormatBytes(hook.OriginalBytes)}, actual {FormatBytes(currentBytes)}.");
                    }

                    AddInstalled(address, hook.OriginalBytes);
                    try
                    {
                        _memory.WriteBytes(address, patchBytes);
                        if (!_memory.ReadBytes(address, patchBytes.Length).SequenceEqual(patchBytes))
                            throw new InvalidOperationException("Hook 写入后回读不一致，开始回滚。");
                    }
                    catch
                    {
                        // A write rejected before changing memory needs no rollback.
                        // If inspection fails, retain the entry and attempt restoration.
                        try
                        {
                            if (_memory.ReadBytes(address, hook.OriginalBytes.Length).SequenceEqual(hook.OriginalBytes))
                            {
                                lock (_installedGate) _installed.RemoveAll(installed => installed.Address == address);
                            }
                        }
                        catch
                        {
                        }
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    installException = new PatchInstallException(hook, index + 1, hookList.Length, address, ex);
                    throw installException;
                }
            }
        }
        catch
        {
            try
            {
                RestoreAll();
            }
            catch (Exception restoreException) when (installException is not null)
            {
                throw new InvalidOperationException(
                    $"{installException.Message} Rollback also failed: {restoreException.Message}",
                    installException);
            }
            catch (Exception restoreException)
            {
                // installException 为空（失败发生在解析/编码阶段）时，原来的 when 过滤器会让
                // 恢复失败被静默丢掉：只抛出原始异常，看不出"可能还有 Hook 没撤"。
                throw new InvalidOperationException(
                    $"回滚失败，可能仍有 Hook 未撤：{restoreException.Message}",
                    restoreException);
            }

            throw;
        }
    }

    public void RestoreAll()
    {
        // 整段持锁：恢复期间不允许并发 Install/查询看到「已写回一半」的列表。
        lock (_installedGate)
        {
        var failures = new List<Exception>();
        for (var index = _installed.Count - 1; index >= 0; index--)
        {
            var installed = _installed[index];
            try
            {
                _memory.WriteBytes(installed.Address, installed.OriginalBytes);
                if (!_memory.ReadBytes(installed.Address, installed.OriginalBytes.Length).SequenceEqual(installed.OriginalBytes))
                    throw new InvalidOperationException("Hook 恢复后回读不一致，保留恢复记录。");
                _installed.RemoveAt(index);
            }
            catch (Exception ex)
            {
                // Keep the failed entry for retry, but restore the other hooks now.
                failures.Add(ex);
            }
        }
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Failed to restore {failures.Count} hook(s): {string.Join("; ", failures.Select(ex => ex.Message))}",
                new AggregateException(failures));
        }
        }
    }

    private void AddInstalled(nint address, byte[] originalBytes)
    {
        lock (_installedGate)
        {
            var existing = _installed.FirstOrDefault(installed => installed.Address == address);
            if (existing.Address == address)
            {
                // 同地址、不同原始字节 = 清单自相矛盾：装上去之后"恢复该写回哪一份"都说不清，
                // 原来只是静默丢掉第二条。这里直接拒绝，让矛盾在装载阶段暴露。
                if (!existing.OriginalBytes.SequenceEqual(originalBytes))
                {
                    throw new InvalidOperationException($"地址 0x{address:X} 有两条原始字节互相矛盾的补丁计划。");
                }

                return;
            }

            _installed.Add((address, originalBytes));
        }
    }

    private static string FormatBytes(IEnumerable<byte> bytes)
    {
        return string.Join(" ", bytes.Select(value => value.ToString("X2")));
    }
}
