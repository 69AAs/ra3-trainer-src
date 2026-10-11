using Ra3Trainer.Core.Manifest;
using Ra3Trainer.Core.Features;
namespace Round30Baseline;
public static class BaselineQueueRunner
{
    public static async Task<IReadOnlyList<ReinforcementQueueResult>> ExecuteAsync(
        IEnumerable<ReinforcementQueueEntry> entries,
        FeatureController controller,
        TrainerFeature reinforcementFeature,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default,
        Action? validateBeforeDispatch = null)
    {
        var results = new List<ReinforcementQueueResult>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            validateBeforeDispatch?.Invoke();
            ReinforcementSettings settings;
            try
            {
                settings = ReinforcementSettings.Parse(entry.UnitIdText, entry.CountText, entry.RankText);
            }
            catch (Exception ex)
            {
                results.Add(new ReinforcementQueueResult(entry, ReinforcementQueueItemStatus.Skipped, ex.Message));
                continue;
            }

            try
            {
                var result = await controller.TriggerActionAndWaitForConsumptionAsync(
                    reinforcementFeature,
                    settings,
                    timeout,
                    pollInterval,
                    cancellationToken: cancellationToken,
                    validateBeforeDispatch: validateBeforeDispatch);
                results.Add(new ReinforcementQueueResult(entry, ToStatus(result), ToMessage(result)));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new ReinforcementQueueResult(entry, ReinforcementQueueItemStatus.Failed, ex.Message));
            }
        }

        return results;
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
