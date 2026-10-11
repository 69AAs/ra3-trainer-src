using Ra3Trainer.Core.Manifest;

namespace Ra3Trainer.Core.Features;

public sealed record ReinforcementQueueEntry(
    string Name,
    string UnitIdText,
    string CountText,
    string RankText);

public enum ReinforcementQueueItemStatus
{
    Pending,
    Executing,
    Executed,
    Skipped,
    TimedOut,
    Failed
}

public sealed record ReinforcementQueueResult(
    ReinforcementQueueEntry Entry,
    ReinforcementQueueItemStatus Status,
    string Message);

public static class ReinforcementQueueRunner
{
    public static async Task<IReadOnlyList<ReinforcementQueueResult>> ExecuteAsync(
        IEnumerable<ReinforcementQueueEntry> entries,
        FeatureController controller,
        TrainerFeature reinforcementFeature,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default,
        Action? validateBeforeDispatch = null,
        Action<ReinforcementQueueResult>? onResult = null)
    {
        var results = new List<ReinforcementQueueResult>();
        // entries 为 null、或里面混了 null 条目时，原来会在 await 链里变成 NullReferenceException
        // （调用方只看到"队列炸了"）。在这里就点名拒绝。
        ArgumentNullException.ThrowIfNull(entries);
        var entryList = new List<ReinforcementQueueEntry>(entries);
        if (entryList.Any(entry => entry is null))
        {
            throw new ArgumentException("队列里包含空条目。", nameof(entries));
        }

        ReinforcementQueueItemStatus? stoppedStatus = null;
        string? stoppedReason = null;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stoppedStatus is { } stopped)
            {
                Report(new(entry, stopped, $"{stoppedReason}；本项未派发。"));
                continue;
            }
            validateBeforeDispatch?.Invoke();
            ReinforcementSettings settings;
            try
            {
                settings = ReinforcementSettings.Parse(entry.UnitIdText, entry.CountText, entry.RankText);
            }
            catch (Exception ex)
            {
                Report(new(entry, ReinforcementQueueItemStatus.Skipped, ex.Message));
                continue;
            }

            var dispatched = false;
            try
            {
                var result = await controller.TriggerActionAndWaitForConsumptionAsync(
                    reinforcementFeature,
                    settings,
                    timeout,
                    pollInterval,
                    // 用"动作槽已写入"的提交回调（而不是"C 已发布"的 onDispatched）：坐标动作里
                    // 转换操作码写进槽之后就算提交，之后再失败要停队列（宁可多停一次）。
                    onActionSlotWrite: () => dispatched = true,
                    cancellationToken: cancellationToken,
                    validateBeforeDispatch: validateBeforeDispatch);
                if (result == ActionDispatchResult.TimedOut)
                {
                    stoppedStatus = ReinforcementQueueItemStatus.TimedOut;
                    stoppedReason = "队列已停止：上一批超时";
                }
                Report(new(entry, ToStatus(result), stoppedReason ?? ToMessage(result)));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (dispatched)
                {
                    stoppedStatus = ReinforcementQueueItemStatus.Failed;
                    stoppedReason = "队列已停止：上一批已派发后失败";
                }
                Report(new(entry, ReinforcementQueueItemStatus.Failed,
                    stoppedReason is null ? ex.Message : $"{stoppedReason}；{ex.Message}"));
            }
        }

        return results;

        void Report(ReinforcementQueueResult result)
        {
            results.Add(result);
            try { onResult?.Invoke(result); }
            catch { /* Display/log observers must never change queue results or dispatch. */ }
        }
    }

    private static ReinforcementQueueItemStatus ToStatus(ActionDispatchResult result)
    {
        return result switch
        {
            ActionDispatchResult.Consumed => ReinforcementQueueItemStatus.Executed,
            ActionDispatchResult.TimedOut => ReinforcementQueueItemStatus.TimedOut,
            _ => ReinforcementQueueItemStatus.Failed
        };
    }

    private static string ToMessage(ActionDispatchResult result)
    {
        return result switch
        {
            ActionDispatchResult.Consumed => "生成请求已消费（未逐单位确认结果）",
            ActionDispatchResult.TimedOut => "游戏仍有待消费动作，本次请求未完成；请等待后重试。",
            _ => "增援功能不是可分发动作。"
        };
    }
}
