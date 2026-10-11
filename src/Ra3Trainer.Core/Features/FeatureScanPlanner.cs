using Ra3Trainer.Core.Manifest;

namespace Ra3Trainer.Core.Features;

public enum FeatureScanKind
{
    Toggle,
    Action,
    Unsupported
}

public sealed record FeatureScanItem(
    TrainerFeature Feature,
    FeatureScanKind Kind,
    string? SkipReason)
{
    public bool CanScan => SkipReason is null;
}

public static class FeatureScanPlanner
{
    public static IReadOnlyList<FeatureScanItem> Create(IEnumerable<TrainerFeature> features)
    {
        ArgumentNullException.ThrowIfNull(features);
        var items = new List<FeatureScanItem>();
        var index = 0;
        foreach (var feature in features)
        {
            if (feature is null)
            {
                // 元素为 null 时 CreateItem 里只会抛裸 NRE，看不出是哪一条功能。
                throw new ArgumentException($"features[{index}] 为 null，无法规划扫描项。", nameof(features));
            }

            items.Add(CreateItem(feature));
            index++;
        }

        return items;
    }

    private static FeatureScanItem CreateItem(TrainerFeature feature)
    {
        // IsToggleFeature 要求 ValueHint 为 null，IsActionFeature 要求非 null ⇒ 两者互斥，
        // 不存在"既算 toggle 又算 action"的功能（ValueHint 语义若变，这里要先改判据）。
        if (FeatureController.IsToggleFeature(feature))
        {
            return new FeatureScanItem(feature, FeatureScanKind.Toggle, null);
        }

        if (FeatureController.IsActionFeature(feature))
        {
            // 判据与 FeatureController.DispatchAction 共用一处（CanDispatch），防止两边漂移。
            return FeatureController.CanDispatch(feature)
                ? new FeatureScanItem(feature, FeatureScanKind.Action, null)
                : new FeatureScanItem(
                    feature,
                    FeatureScanKind.Unsupported,
                    "缺少 dispatch target 或 enable flag。");
        }

        return new FeatureScanItem(
            feature,
            FeatureScanKind.Unsupported,
            "不是可触发的 toggle/action 功能。");
    }
}
