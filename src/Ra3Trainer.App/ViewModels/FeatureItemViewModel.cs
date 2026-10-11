using System.Windows.Input;
using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Manifest;

namespace Ra3Trainer.App.ViewModels;

public sealed class FeatureItemViewModel : ViewModelBase
{
    private readonly MainViewModel _owner;
    private bool _enabled;
    private bool _isExecuting;
    private string _status = "未启用";

    public FeatureItemViewModel(TrainerFeature feature, MainViewModel owner)
    {
        Feature = feature;
        _owner = owner;
        Command = new RelayCommand(() => _ = ExecuteAsync(), () => _owner.ArePatchesInstalled && !_isExecuting);
    }

    public TrainerFeature Feature { get; }

    public string DisplayName => Feature.DisplayName;

    public string RiskLabel => FeatureSafetyPolicy.RequiresWorldPositionOptIn(Feature) ? FeatureSafetyPolicy.RiskLabel : string.Empty;

    public string DisplayNameWithRisk => string.IsNullOrEmpty(RiskLabel) ? DisplayName : $"{DisplayName}【{RiskLabel}】";

    public string? Hotkey => Feature.Hotkey;

    public bool IsActive => _enabled;

    public string ActionText => IsToggle ? (_enabled ? "关闭" : "开启") : "执行";

    public bool IsToggle => FeatureController.IsToggleFeature(Feature);

    public string Status
    {
        get => _status;
        private set
        {
            _status = value;
            OnPropertyChanged();
        }
    }

    public RelayCommand Command { get; }

    public void RaiseCommandState() => Command.RaiseCanExecuteChanged();

    public void ResetSessionState()
    {
        _enabled = false;
        Status = "未启用";
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(ActionText));
        RaiseCommandState();
    }

    public void ExecuteFromHotkey()
    {
        _ = ExecuteAsync(fromHotkey: true);
    }

    private async Task ExecuteAsync(bool fromHotkey = false)
    {
        if (_isExecuting || !_owner.ArePatchesInstalled)
        {
            // 诊断：这条早退以前完全静默 —— 热键/面板/浮窗三条路打到这里都会"按了没反应"。
            _owner.LogFeatureBlocked(Feature.RawName, _isExecuting ? "已在执行中（上一发没结束）" : "本会话未装 patch");
            return;
        }

        var controller = _owner.FeatureController;
        var cancellationToken = _owner.SessionCancellation;
        CancellationTokenSource? coordinateAction = null;
        var requestId = Guid.NewGuid().ToString("N");
        _owner.OnFeatureExecuted(Feature.RawName, Feature.DisplayName, "request", "请求开始（尚未完成）", requestId);
        try
        {
            if (controller is null)
            {
                Status = "未连接";
                _owner.StatusMessage = "先点「连接游戏」，再点「开启修改」。";
                return;
            }

            if (IsToggle)
            {
                var nextEnabled = !_enabled;
                FeatureSafetyPolicy.Validate(Feature, nextEnabled, _owner.AllowUnverifiedWorldPositionFeatures);
                _owner.OnFeatureExecuted(Feature.RawName, Feature.DisplayName, "toggle-request", nextEnabled ? "请求启用" : "请求关闭");
                if (nextEnabled)
                {
                    _owner.WriteResourceValuesIfNeeded(Feature);
                }

                controller.SetToggle(Feature, nextEnabled);
                _enabled = nextEnabled;
                OnPropertyChanged(nameof(IsActive));
                Status = _enabled ? "已启用" : "已关闭";
                OnPropertyChanged(nameof(ActionText));
                _owner.OnFeatureExecuted(Feature.RawName, Feature.DisplayName, "toggle", Status);
                return;
            }

            FeatureSafetyPolicy.Validate(Feature, true, _owner.AllowUnverifiedWorldPositionFeatures);
            _owner.OnFeatureExecuted(Feature.RawName, Feature.DisplayName, "action-request", "请求执行", requestId);
            var reinforcementSettings = IsReinforcementFeature ? _owner.GetReinforcementSettings() : null;
            if (FeatureSafetyPolicy.IsCoordinateAction(Feature))
            {
                coordinateAction = _owner.BeginCoordinateAction();
                if (coordinateAction is null) return;
                cancellationToken = coordinateAction.Token;
            }
            _owner.WriteResourceValuesIfNeeded(Feature);
            _isExecuting = true;
            Command.RaiseCanExecuteChanged();
            Status = Feature.DispatchTarget is null ? "已触发" : "触发中";

            if (coordinateAction is not null)
                await _owner.PrepareCoordinateActionAsync(controller, Feature, reinforcementSettings, fromHotkey, cancellationToken, requestId);
            cancellationToken.ThrowIfCancellationRequested();
            var result = reinforcementSettings is not null
                ? await controller.TriggerActionAndWaitForConsumptionAsync(Feature, reinforcementSettings, cancellationToken: cancellationToken, validateBeforeDispatch: () => FeatureSafetyPolicy.Validate(Feature, true, _owner.AllowUnverifiedWorldPositionFeatures), requestId: requestId)
                : await controller.TriggerActionAndWaitForConsumptionAsync(Feature, cancellationToken: cancellationToken, validateBeforeDispatch: () => FeatureSafetyPolicy.Validate(Feature, true, _owner.AllowUnverifiedWorldPositionFeatures), requestId: requestId);

            if (!ReferenceEquals(controller, _owner.FeatureController))
            {
                return;
            }

            if (result == ActionDispatchResult.Consumed)
            {
                Status = "动作槽已消费";
                _owner.StatusMessage = "有明确预期值的目标字段已另行读回；其余仅确认动作槽消费。生成／销毁／转让／复制不可回滚。";
            }
            else if (result == ActionDispatchResult.TimedOut)
            {
                Status = "超时";
                _owner.StatusMessage = "游戏仍有待消费动作，本次请求未完成；请等待后重试。";
            }
            else
            {
                Status = "已触发";
            }

            _owner.OnFeatureExecuted(Feature.RawName, Feature.DisplayName, "execute", Status, requestId);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(controller, _owner.FeatureController)) Status = "已取消";
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(controller, _owner.FeatureController)) return;
            Status = "失败";
            // 给人看的那一层翻译成用户话；技术原文照样进日志（见下方 OnFeatureExecuted 的 "失败：" 一条）。
            _owner.StatusMessage = FeatureErrorText.ToUserText(ex.Message);
            _owner.OnFeatureExecuted(Feature.RawName, Feature.DisplayName, IsToggle ? "toggle" : "execute", $"失败：{ex.Message}", requestId);
        }
        finally
        {
            if (coordinateAction is not null) _owner.EndCoordinateAction(coordinateAction);
            _isExecuting = false;
            Command.RaiseCanExecuteChanged();
        }
    }

    private bool IsReinforcementFeature =>
        Feature.RawName.Equals("We Need Back", StringComparison.Ordinal) ||
        string.Equals(Feature.DispatchTarget, "MustCode2+B00", StringComparison.OrdinalIgnoreCase);
}
