using System.Diagnostics;
using Ra3Trainer.Core.Manifest;
using Ra3Trainer.Core.Memory;
using Ra3Trainer.Core.Runtime;

namespace Ra3Trainer.Core.Features;

public sealed class FeatureController
{
    public static readonly TimeSpan DefaultDispatchTimeout = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan DefaultDispatchPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly FeatureMemoryAccess _memory;
    private readonly CancellationTokenSource _stopCancellation = new();
    private readonly AddressResolver _resolver;
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private readonly object _toggleGate = new();
    private readonly List<(nint Address, byte[] Bytes)> _pendingToggleRestores = new();
    public event Action<string>? SafetyMessage;
    public Action<ActionEvidence>? EvidenceSink { get; set; }
    public string? AttachSessionId { get; set; }
    public string? TrailingRequestId { get; private set; }
    public string? LastRequestId { get; private set; }

    /// <summary>Called only by the opt-in safe probe, after failure, at a reduced cadence.</summary>
    public ProtocolSnapshot? CaptureTrailingEvidence()
    {
        if (TrailingRequestId is null || !_dispatchGate.Wait(0)) return null;
        try
        {
            lock (_toggleGate)
            {
                if (_stopCancellation.IsCancellationRequested) return null;
                return ActionEvidence.Capture(_memory, _resolver);
            }
        }
        catch { return null; }
        finally { _dispatchGate.Release(); }
    }

    public int PendingToggleRestoreCount
    {
        get { lock (_toggleGate) return _pendingToggleRestores.Count; }
    }

    public FeatureController(IProcessMemory memory, AddressResolver resolver)
    {
        _memory = new FeatureMemoryAccess(memory);
        _resolver = resolver;
    }

    public static bool IsToggleFeature(TrainerFeature feature)
    {
        return (feature.EnableFlags.Count > 0 || feature.ToggleBytePatches is { Count: > 0 }) &&
            feature.ValueHint is null;
    }

    /// <summary>Cancel waits and drain native accesses. Managed continuations may still unwind.</summary>
    public void Stop()
    {
        lock (_toggleGate)
        {
            _stopCancellation.Cancel();
            _memory.Stop();
        }
    }

    /// <summary>Stop new actions and drain current operations; recovery writes remain available.</summary>
    public void QuiesceActions()
    {
        lock (_toggleGate)
        {
            _stopCancellation.Cancel();
            _memory.Drain();
        }
    }

    public static bool IsActionFeature(TrainerFeature feature)
    {
        return feature.ValueHint is not null;
    }

    /// <summary>
    /// 解析清单里的 value_hint（约定 "0xNN"）。畸形值原来抛的是 Convert.ToByte 的
    /// Format/Overflow/ArgumentOutOfRange，看不出是哪个功能写坏了。
    /// </summary>
    private static byte ParseValueHint(TrainerFeature feature, string hint)
    {
        if (hint.Length < 3 || !hint.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            !byte.TryParse(hint.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidOperationException($"{feature.DisplayName} 的 value_hint '{hint}' 不是 0xNN 形式。");
        }

        return value;
    }

    /// <summary>
    /// Action 功能"能不能派发"的唯一判据（planner 扫面与 DispatchAction 都问这里）。
    /// 两处各写一份的话，改动只落一边就会出现"扫得出来、发不出去"或反之。
    /// </summary>
    public static bool CanDispatch(TrainerFeature feature)
    {
        return feature.DispatchTarget is not null || feature.EnableFlags.Count > 0;
    }

    public void SetToggle(TrainerFeature feature, bool enabled)
    {
        if (!IsToggleFeature(feature))
        {
            throw new InvalidOperationException($"{feature.DisplayName} is not a toggle feature.");
        }

        lock (_toggleGate)
        {
            if (enabled) _stopCancellation.Token.ThrowIfCancellationRequested();
            if (enabled) ValidateBeforeWrite(feature);
            // Never take a new baseline from memory whose previous rollback failed.
            RetryPendingToggleRestores();
            var writes = new List<(nint Address, byte[] Bytes)>();
            foreach (var flag in feature.EnableFlags)
                writes.Add((_resolver.Resolve(flag), [enabled ? (byte)1 : (byte)0]));
            if (feature.ToggleBytePatches is not null)
                foreach (var patch in feature.ToggleBytePatches)
                    writes.Add((_resolver.Resolve(patch.Address),
                        (enabled ? patch.EnabledBytes : patch.DisabledBytes).ToArray()));

            // Snapshot every range before the first write (also handles overlapping ranges).
            var originals = writes.Select(w => (w.Address, Bytes: _memory.ReadBytes(w.Address, w.Bytes.Length))).ToArray();
            try
            {
                for (var index = 0; index < writes.Count; index++)
                {
                    // WriteBytes can modify memory and then throw (partial write / protect / flush).
                    _pendingToggleRestores.Add(originals[index]);
                    _memory.WriteBytes(writes[index].Address, writes[index].Bytes);
                    if (!_memory.ReadBytes(writes[index].Address, writes[index].Bytes.Length).SequenceEqual(writes[index].Bytes))
                        throw new InvalidOperationException("功能开关写后读回不符。");
                }
                _pendingToggleRestores.Clear();
            }
            catch (Exception writeError)
            {
                SafetyMessage?.Invoke("功能开关写入或读回失败：" + writeError.Message);
                try { RetryPendingToggleRestores(); }
                catch (Exception restoreError)
                {
                    throw new AggregateException("功能切换失败且回滚未完成；恢复记录已保留，请重试或恢复 Patch。", writeError, restoreError);
                }
                throw;
            }
        }
    }

    public void RetryPendingToggleRestores()
    {
        lock (_toggleGate)
        {
            var errors = new List<Exception>();
            for (var index = _pendingToggleRestores.Count - 1; index >= 0; index--)
            {
                var restore = _pendingToggleRestores[index];
                try
                {
                    _memory.WriteBytes(restore.Address, restore.Bytes);
                    _pendingToggleRestores.RemoveAt(index);
                }
                catch (Exception ex)
                {
                    errors.Add(new InvalidOperationException($"恢复 0x{restore.Address:X} 失败。", ex));
                }
            }
            if (errors.Count > 0) throw new AggregateException("功能开关仍有待恢复字节。", errors);
        }
    }

    /// <summary>只提交经过预检的动作；不宣称游戏执行完成。需要目标读回确认时使用异步接口。</summary>
    public void TriggerAction(TrainerFeature feature)
    {
        TriggerAction(feature, reinforcementSettings: null);
    }

    /// <summary>只提交经过预检的动作；异步接口等待消费并确认可读回的目标字段。</summary>
    public void TriggerAction(TrainerFeature feature, ReinforcementSettings? reinforcementSettings)
    {
        _stopCancellation.Token.ThrowIfCancellationRequested();
        if (FeatureSafetyPolicy.IsCoordinateAction(feature))
        {
            const string reason = "坐标动作必须通过异步两阶段根链校验接口；已拒绝同步派发，未写入。";
            SafetyMessage?.Invoke(reason);
            throw new InvalidOperationException(reason);
        }
        EnterSynchronousDispatch();
        try { DispatchAction(feature, reinforcementSettings); }
        finally { _dispatchGate.Release(); }
    }

    private SelectedObjectGuard? DispatchAction(TrainerFeature feature, ReinforcementSettings? reinforcementSettings, bool coordinateValidated = false)
    {
        lock (_toggleGate)
        {
            _stopCancellation.Token.ThrowIfCancellationRequested();
            var valueHint = feature.ValueHint;
            if (valueHint is null)
            {
                throw new InvalidOperationException($"{feature.DisplayName} is not an action feature.");
            }

            if (feature.DispatchTarget is not null && !CoordinateSlotIdle())
                throw new InvalidOperationException("上一动作尚未被游戏消费，已拒绝覆盖。");

            if (FeatureSafetyPolicy.IsCoordinateAction(feature) && !coordinateValidated)
                throw new InvalidOperationException("坐标动作必须先消费转换派发，已拒绝绕过校验。");
            var validation = FeatureSafetyPolicy.IsObjectFlagRecovery(feature) ? null :
                ValidateBeforeWrite(feature, reinforcementSettings, coordinateValidated);

            if (FeatureSafetyPolicy.RequiresSelectedObjectValidation(feature) && feature.DispatchTarget is not null)
            {
                var dispatchBytes = _memory.ReadBytes(_resolver.Resolve(RemoteStateLayout.ActionDispatch), 4);
                if (dispatchBytes.Length != 4 || dispatchBytes.Any(value => value != 0))
                    throw new InvalidOperationException("动作槽未完整归零，已拒绝单位操作；请先一键全撤并重新装载。");
            }

            var value = ParseValueHint(feature, valueHint);
            if (feature.DispatchTarget is null)
            {
                if (!CanDispatch(feature))
                {
                    throw new InvalidOperationException($"{feature.DisplayName} has no dispatch target or enable flag.");
                }

                WriteTransaction(feature.EnableFlags.Select(flag =>
                    (Address: _resolver.Resolve(flag), Bytes: new[] { value })).ToArray());
                return validation;
            }

            if (IsReinforcementFeature(feature))
            {
                WriteReinforcementSettingsCore(reinforcementSettings ?? ReinforcementSettings.Default);
            }

            _memory.WriteBytes(_resolver.Resolve(RemoteStateLayout.ActionDispatch), stackalloc[] { value });
            return validation;
        }
    }

    public async Task<ActionDispatchResult> TriggerActionAndWaitForConsumptionAsync(
        TrainerFeature feature,
        ReinforcementSettings? reinforcementSettings = null,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        Action? onDispatched = null,
        CancellationToken cancellationToken = default,
        Action? validateBeforeDispatch = null,
        string? requestId = null,
        Action? onActionSlotWrite = null)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopCancellation.Token);
        cancellationToken = linkedCancellation.Token;
        var effectiveTimeout = timeout ?? DefaultDispatchTimeout;
        var effectivePollInterval = pollInterval ?? DefaultDispatchPollInterval;
        if (effectiveTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (effectivePollInterval < TimeSpan.FromMilliseconds(1) || effectivePollInterval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ActionEvidence? evidence = null;
        var previousEvidence = ActionEvidence.Current.Value;
        var outcome = "rejected";
        string? rejection = null;
        try
        {
            if (EvidenceSink is not null)
            {
                evidence = new ActionEvidence(_resolver, requestId ?? Guid.NewGuid().ToString("N"), AttachSessionId,
                    feature.RawName, effectiveTimeout.TotalMilliseconds, effectivePollInterval.TotalMilliseconds);
                LastRequestId = evidence.RequestId;
                ActionEvidence.Current.Value = evidence;
            }
        }
        catch { /* Evidence setup cannot prevent dispatch. */ }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (feature.DispatchTarget is not null && !CoordinateSlotIdle())
            {
                // A timed-out action may still be pending in the game. Do not overwrite it
                // or change reinforcement parameters before it has been consumed.
                outcome = "TimedOut";
                rejection = "请求前动作槽或派发锁未完整归零（含短读）；未派发、未覆盖参数。";
                return ActionDispatchResult.TimedOut;
            }

            validateBeforeDispatch?.Invoke();
            var result = FeatureSafetyPolicy.IsCoordinateAction(feature)
                ? await DispatchCoordinatesAsync(feature, reinforcementSettings, timeout, pollInterval,
                    onDispatched, cancellationToken, onActionSlotWrite).ConfigureAwait(false)
                : await DispatchAndWaitAsync(feature, reinforcementSettings, timeout, pollInterval,
                    onDispatched, cancellationToken, onActionSlotWrite).ConfigureAwait(false);
            outcome = result.ToString();
            if (result == ActionDispatchResult.TimedOut)
                rejection = "阶段等待到期，最后动作槽或派发锁仍非空或短读；未重试、未清槽、未继续派发。";
            return result;
        }
        catch (Exception ex) { rejection = ex.Message; outcome = ex is OperationCanceledException ? "cancelled" : "rejected"; throw; }
        finally
        {
            // Finish the clock before diagnostic-only reads and disk I/O. No additional reads in the polling loop.
            try
            {
                evidence?.Finish(outcome, rejection);
                ActionEvidence.Current.Value = null;
                if (evidence is not null)
                {
                    // 成功（Consumed/NotRequired）要把上一次失败的 trailing 记录清掉，
                    // 否则后续请求的尾随取证一直挂在旧失败 id 上（原来只赋值、从不清）。
                    TrailingRequestId = outcome is "Consumed" or "NotRequired" ? null : evidence.RequestId;
                    if (!_stopCancellation.IsCancellationRequested)
                        evidence.TerminalSnapshot = ActionEvidence.Capture(_memory, _resolver);
                }
            }
            catch { /* Only evidence is lost. */ }
            ActionEvidence.Current.Value = previousEvidence;
            _dispatchGate.Release();
            try { if (evidence is not null) EvidenceSink?.Invoke(evidence); }
            catch { /* Disk/sink failure must never replace the result or original exception. */ }
        }
    }

    private async Task<ActionDispatchResult> DispatchAndWaitAsync(
        TrainerFeature feature,
        ReinforcementSettings? reinforcementSettings,
        TimeSpan? timeout,
        TimeSpan? pollInterval,
        Action? onDispatched,
        CancellationToken cancellationToken,
        Action? onActionSlotWrite)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = DispatchAction(feature, reinforcementSettings);
        // 非坐标路径：动作槽在 DispatchAction 内已写入。onActionSlotWrite 与既有 onDispatched
        // 同点触发 ⇒ 这条路径的语义与改动前完全一致（坐标路径的"写槽前"提交见 DispatchCoordinatesAsync）。
        onActionSlotWrite?.Invoke();
        onDispatched?.Invoke();

        if (feature.DispatchTarget is null)
        {
            return ActionDispatchResult.NotRequired;
        }

        return await WaitForConsumptionAsync(validation, timeout, pollInterval, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ActionDispatchResult> DispatchCoordinatesAsync(TrainerFeature feature,
        ReinforcementSettings? settings, TimeSpan? timeout, TimeSpan? pollInterval,
        Action? onDispatched, CancellationToken cancellationToken, Action? onActionSlotWrite)
    {
        uint epoch;
        uint generationEpoch;
        lock (_toggleGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateBeforeWrite(feature, settings);
            RequireCoordinateSlotIdle();
            epoch = ReadConversionEpoch();
            ActionEvidence.Observe(e => { e.BeginPhase("conversion"); e.Phase.ConversionEpochBefore = epoch; e.Phase.ExpectedConversionEpoch = unchecked(epoch + 1); });
            // 【连发队列停止时序】写动作槽"之前"就触发提交回调（宁可多停一次）：转换操作码一旦
            // 写进槽里，之后任何失败——转换超时、未拿到完成凭据、凭据读取不完整、生成阶段异常——
            // 都可能留下不干净的动作槽；此时队列必须停下，不能当成普通失败继续派发下一批。
            // 位置在校验/槽空闲/epoch 读取全部通过之后：纯校验拒绝（如根链不成立）不写槽、
            // 不算提交，队列照旧继续。注意这与 onDispatched（"C 已发布"的既有语义）是两件事，
            // onDispatched 的位置与含义保持不变。
            onActionSlotWrite?.Invoke();
            // Only the game-side conversion branch zeros the output, immediately before conversion.
            _memory.WriteBytes(_resolver.Resolve(RemoteStateLayout.ActionDispatch),
                new byte[] { RemoteStateLayout.CoordinateConversionDispatch });
        }
        var converted = await WaitForConsumptionAsync(null, timeout, pollInterval, cancellationToken, true).ConfigureAwait(false);
        if (converted != ActionDispatchResult.Consumed) return converted;
        lock (_toggleGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireCoordinateSlotIdle();
            if (ReadConversionEpoch() != unchecked(epoch + 1))
            {
                const string reason = "本次转换未返回完成凭据，世界坐标新鲜性无法证明；已拒绝生成，未写入。";
                SafetyMessage?.Invoke(reason);
                throw new InvalidOperationException(reason);
            }
            // B is read-only. Failure writes neither settings nor generation dispatch.
            generationEpoch = ReadProtocolWord(RemoteStateLayout.DispatchEpoch);
            ActionEvidence.Observe(e => { e.BeginPhase("generation"); e.Phase.DispatchEpochBefore = generationEpoch; e.Phase.ExpectedDispatchEpoch = unchecked(generationEpoch + 1); });
            DispatchAction(feature, settings, coordinateValidated: true);
        }
        // onDispatched（"请求的动作 C 已发布"）保持原有语义与位置：生成派发写入之后才触发。
        onDispatched?.Invoke();
        // Consumption confirms the protocol only. Creation/copy cannot be rolled back or
        // reported as successfully materialized without an independently readable result.
        var generated = await WaitForConsumptionAsync(null, timeout, pollInterval, cancellationToken, true).ConfigureAwait(false);
        if (generated == ActionDispatchResult.Consumed)
        {
            string? reason = null;
            if (ReadProtocolWord(RemoteStateLayout.DispatchEpoch) != unchecked(generationEpoch + 1))
                reason = "生成派发未返回完成凭据，无法确认已执行；未尝试回滚。";
            else if (ReadProtocolWord(RemoteStateLayout.CreationFailure) != 0)
                reason = "游戏未解析单位类型、生成函数返回空对象或新单位等级组件为空；可能已部分生成，无法回滚，请检查战场。";
            if (reason is not null) { SafetyMessage?.Invoke(reason); throw new InvalidOperationException(reason); }
        }
        return generated;
    }

    private void RequireCoordinateSlotIdle()
    {
        if (!CoordinateSlotIdle())
            throw new InvalidOperationException("动作槽或远程派发锁未完整归零，已拒绝坐标动作。");
    }

    private uint ReadConversionEpoch() => ReadProtocolWord(RemoteStateLayout.ConversionEpoch);

    private uint ReadProtocolWord(string address)
    {
        var bytes = _memory.ReadBytes(_resolver.Resolve(address), 4);
        if (bytes.Length != 4) throw new InvalidOperationException("转换完成凭据读取不完整，已拒绝执行。");
        return BitConverter.ToUInt32(bytes);
    }

    private bool CoordinateSlotIdle()
    {
        var dispatch = _memory.ReadBytes(_resolver.Resolve(RemoteStateLayout.ActionDispatch), 4);
        var busy = _memory.ReadBytes(_resolver.Resolve(RemoteStateLayout.DispatchLock), 4);
        return dispatch.Length == 4 && busy.Length == 4 && dispatch.All(b => b == 0) && busy.All(b => b == 0);
    }

    private async Task<ActionDispatchResult> WaitForConsumptionAsync(SelectedObjectGuard? validation,
        TimeSpan? timeout, TimeSpan? pollInterval, CancellationToken cancellationToken, bool coordinate = false)
    {

        var effectiveTimeout = timeout ?? DefaultDispatchTimeout;
        var effectivePollInterval = pollInterval ?? DefaultDispatchPollInterval;
        var started = Stopwatch.GetTimestamp();
        ActionEvidence.Observe(e => e.Phase.WaitStartQpc = started);
        try
        {
        while (Stopwatch.GetElapsedTime(started) < effectiveTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActionEvidence.Observe(e => e.Poll());
            if (coordinate ? CoordinateSlotIdle() : ReadActionDispatch() == 0)
            {
                ConfirmAfterWrite(validation);
                return ActionDispatchResult.Consumed;
            }

            var remaining = effectiveTimeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < effectivePollInterval ? remaining : effectivePollInterval, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        ActionEvidence.Observe(e => e.Poll());
        if (coordinate ? !CoordinateSlotIdle() : ReadActionDispatch() != 0) return ActionDispatchResult.TimedOut;
        ConfirmAfterWrite(validation);
        return ActionDispatchResult.Consumed;
        }
        finally { ActionEvidence.Observe(e => e.Phase.WaitEndQpc = Stopwatch.GetTimestamp()); }
    }

    /// <summary>Read-only scheduling hint. Dispatch still performs conversion, epoch and all guards again.</summary>
    public bool HasValidCurrentCoordinates(TrainerFeature feature, ReinforcementSettings? settings = null)
    {
        _stopCancellation.Token.ThrowIfCancellationRequested();
        lock (_toggleGate)
        {
            RequireCoordinateSlotIdle();
            // Bad roots/types must fail immediately, not be disguised as a cursor countdown.
            ValidateBeforeWrite(feature, settings);
            try { new SelectedObjectGuard(_memory, _resolver).Validate(feature, settings, true); return true; }
            catch (InvalidOperationException) { return false; }
        }
    }

    private SelectedObjectGuard ValidateBeforeWrite(TrainerFeature feature, ReinforcementSettings? settings = null, bool coordinates = false)
    {
        var guard = new SelectedObjectGuard(_memory, _resolver);
        try { guard.Validate(feature, settings, coordinates); }
        catch (InvalidOperationException ex) { SafetyMessage?.Invoke(ex.Message); throw; }
        return guard;
    }

    private void ConfirmAfterWrite(SelectedObjectGuard? validation)
    {
        try { validation?.Confirm(); }
        catch (InvalidOperationException ex) { SafetyMessage?.Invoke(ex.Message); throw; }
    }

    public void WriteReinforcementSettings(ReinforcementSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _stopCancellation.Token.ThrowIfCancellationRequested();
        EnterSynchronousDispatch();
        try { WriteReinforcementSettingsCore(settings); }
        finally { _dispatchGate.Release(); }
    }

    private void EnterSynchronousDispatch()
    {
        if (!_dispatchGate.Wait(0))
            throw new InvalidOperationException("动作校验或派发仍在进行，已拒绝并发写入。");
    }

    private void WriteReinforcementSettingsCore(ReinforcementSettings settings)
    {
        lock (_toggleGate)
        {
            if (!CoordinateSlotIdle())
                throw new InvalidOperationException("上一动作尚未被游戏消费，不能修改增援参数。");
            WriteSettings([
                (RemoteStateLayout.ReinforcementUnitId, settings.UnitId),
                (RemoteStateLayout.ReinforcementCount, (uint)settings.Count),
                (RemoteStateLayout.ReinforcementRank, (uint)settings.Rank)]);
        }
    }

    public void WriteResourceValues(ResourceValueSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // 与其它公开写入口同规矩：动作校验/派发进行中拒绝并发写入
        // （原来这里绕过了 _dispatchGate，等于在"已声明拒绝并发写入"的类里留了一个例外）。
        EnterSynchronousDispatch();
        try
        {
            WriteSettings([
                (RemoteStateLayout.MoneyAmount, (uint)settings.MoneyAmount),
                (RemoteStateLayout.PowerValue, (uint)settings.PowerValue),
                (RemoteStateLayout.ScPointValue, (uint)settings.ScPointValue)]);
        }
        finally
        {
            _dispatchGate.Release();
        }
    }

    public uint ReadSelectedUnitCode()
    {
        var bytes = _memory.ReadBytes(_resolver.Resolve(RemoteStateLayout.SelectedUnitCode), 4);
        return BitConverter.ToUInt32(bytes);
    }

    /// <summary>
    /// 【只读】尽力读出"当前选中单位"的模板内部名（<c>SovietNewTower</c> 这种），给"自动记单位"起名字用。
    ///
    /// <para>链与守卫**完全一致**（`root=[game+8DB73C]` → `[root+50]` → `[+8]` → `[+138]`=单位 → `+4`=模板），
    /// 但这里**只读、绝不写、绝不抛**：任何一步读不到就返回 null，调用方落回「未命名」。
    /// 只读按构造不可能崩游戏（反复踩的坑是"调引擎内部函数"，读数不是）。</para>
    /// </summary>
    public string? TryReadSelectedUnitInternalName()
    {
        try
        {
            var root = Deref(_resolver.Resolve("ra3_1.12.game+8DB73C"));
            if (root == 0) return null;
            if (U32(root + 0x5C) == 0) return null;                     // 没选中单位 ⇒ 没必要往下走
            var node = Deref(root + 0x50);
            var wrapper = Deref(node + 8);
            var unit = Deref(wrapper + 0x138);
            var template = Deref(unit + 4);
            if (template == 0) return null;

            var known = UnitNaming.InternalNames.Select(unit => unit.InternalName).ToArray();
            return UnitTemplateNameProbe.TryRead(_memory, template, known);
        }
        catch
        {
            return null;                                                // 读不到就是读不到，不打扰用户
        }
    }

    /// <summary>读 4 字节指针并按"范围内 + 4 字节对齐"校验（只读路径专用）。</summary>
    private nint Deref(nint address)
    {
        var bytes = _memory.ReadBytes(address, 4);
        if (bytes.Length != 4) throw new InvalidOperationException("指针读取不完整。");
        var value = BitConverter.ToUInt32(bytes);
        if (value < EmpiricalSafetyBounds.MinimumPointer || value > EmpiricalSafetyBounds.MaximumPointer || (value & 3) != 0)
        {
            throw new InvalidOperationException($"指针非法：0x{value:X8}。");
        }

        return unchecked((nint)(nuint)value);
    }

    /// <summary>读 4 字节字段（只读路径专用）。</summary>
    private uint U32(nint address)
    {
        var bytes = _memory.ReadBytes(address, 4);
        return bytes.Length != 4 ? throw new InvalidOperationException("字段读取不完整。") : BitConverter.ToUInt32(bytes);
    }

    /// <summary>
    /// 【心灵控制·解除心灵控制】把快照里的旧 owner 写回 [U+0x418]。
    ///
    /// 与「心灵控制（写入）」的关系：写入走的是**引擎自己的 0x09 动作**（把 [U+0x418] 改成本机玩家 [ID]，
    /// 见 Assets/00_bootstrap.aa:966-997 / SelectedObjectGuard.cs:136-143）；引擎没有「改成任意 owner」的动作，
    /// 所以还原只能是对象级回写——本方法把它收进同一个 <c>_dispatchGate</c> + <c>_toggleGate</c>，
    /// 且**只当该字段当前仍等于快照记录的新值**时才写回（否则单位已被换代/重新选中，拒绝，绝不动别人的对象）。
    /// 调用方必须已通过「高风险功能」闸门。
    /// </summary>
    public OwnershipRestoreOutcome RestoreSelectedUnitOwnership(nint ownerAddress, uint expectedCurrent, uint restoreValue)
    {
        EnterSynchronousDispatch();
        try
        {
            lock (_toggleGate)
            {
                _stopCancellation.Token.ThrowIfCancellationRequested();
                var beforeBytes = _memory.ReadBytes(ownerAddress, 4);
                if (beforeBytes.Length != 4)
                    throw new InvalidOperationException("归属字段读取不完整，已拒绝还原，未写入。");
                var before = BitConverter.ToUInt32(beforeBytes);
                if (before != expectedCurrent)
                    throw new InvalidOperationException(
                        $"归属字段当前值 0x{before:X8} 与快照记录的新值 0x{expectedCurrent:X8} 不一致" +
                        "（单位可能已被换掉/重新选中）；已拒绝还原，未写入。");
                if (restoreValue == expectedCurrent)
                    return new OwnershipRestoreOutcome(ownerAddress, before, before, false,
                        "归属字段已是快照里的旧值，无需还原。");
                // WriteTransaction 自带「写前快照 + 写后读回 + 失败回滚」。
                WriteTransaction([(ownerAddress, BitConverter.GetBytes(restoreValue))]);
                var afterBytes = _memory.ReadBytes(ownerAddress, 4);
                if (afterBytes.Length != 4)
                    throw new InvalidOperationException("还原写后回读不完整，无法确认结果。");
                var after = BitConverter.ToUInt32(afterBytes);
                if (after != restoreValue)
                    throw new InvalidOperationException($"还原写后回读不符：写出 0x{restoreValue:X8}，读回 0x{after:X8}。");
                return new OwnershipRestoreOutcome(ownerAddress, before, after, true, null);
            }
        }
        finally { _dispatchGate.Release(); }
    }

    /// <summary>
    /// 【心灵控制·账本条目】把 `[U+0x414]` 写成**本机玩家的条目**（`[[ID]+0xAC]`）。
    ///
    /// 为什么需要它（2026-10-08 真机确认）：只写 owner（引擎动作 0x09）时，被心灵控制的生产建筑会**卡住、不再出单位**；
    /// 把条目也写成用户的之后，**它继续出单位、而且产出的单位归用户** —— 实测三步：心灵控制（不出单位）→ 补写条目 → 出单位且归我。
    /// 条目 = `[本机玩家 + 0xAC]`（三次独立真机验证：玩家自己那栋楼 `[U+0x414]` 恰好等于它）。
    ///
    /// 与「解除心灵控制」同款纪律：**纯写入**（不走引擎、不调任何游戏函数 ⇒ 不会像调引擎函数那样把游戏带崩），
    /// 收进同一把 `_dispatchGate` + `_toggleGate`，**只当该字段当前仍等于预期值时才写**（否则对象已换代/被改 ⇒ 拒绝），
    /// 写完回读核对。调用方必须已通过「高风险功能」闸门。
    /// </summary>
    public OwnershipRestoreOutcome WriteSelectedUnitEntry(nint entryAddress, uint expectedCurrent, uint value)
    {
        EnterSynchronousDispatch();
        try
        {
            lock (_toggleGate)
            {
                _stopCancellation.Token.ThrowIfCancellationRequested();
                var beforeBytes = _memory.ReadBytes(entryAddress, 4);
                if (beforeBytes.Length != 4)
                    throw new InvalidOperationException("账本条目字段读取不完整，已拒绝写入，未写内存。");
                var before = BitConverter.ToUInt32(beforeBytes);
                if (before != expectedCurrent)
                    throw new InvalidOperationException(
                        $"账本条目当前值 0x{before:X8} 与预期 0x{expectedCurrent:X8} 不一致" +
                        "（对象可能已被换掉/被别人改过）；已拒绝写入，未写内存。");
                if (value == expectedCurrent)
                    return new OwnershipRestoreOutcome(entryAddress, before, before, false, "账本条目已一致，无需写入。");
                WriteTransaction([(entryAddress, BitConverter.GetBytes(value))]);
                var afterBytes = _memory.ReadBytes(entryAddress, 4);
                if (afterBytes.Length != 4)
                    throw new InvalidOperationException("账本条目写后回读不完整，无法确认结果。");
                var after = BitConverter.ToUInt32(afterBytes);
                if (after != value)
                    throw new InvalidOperationException($"账本条目写后回读不符：写出 0x{value:X8}，读回 0x{after:X8}。");
                return new OwnershipRestoreOutcome(entryAddress, before, after, true, null);
            }
        }
        finally { _dispatchGate.Release(); }
    }

    /// <summary>
    /// 诊断用（只读）：在"当前选中单位代号"附近扫描疑似对象名指针，把能读出 ASCII 的候选收集起来，
    /// 供"把游戏里的单位自动翻译成中文名"使用。地址无效就跳过，绝不写内存。
    /// </summary>
    public IReadOnlyList<string> ProbeSelectedUnitNameCandidates(int window = 0xA0, int maxResults = 24)
    {
        var results = new List<string>();
        var baseAddress = _resolver.Resolve(RemoteStateLayout.SelectedUnitCode);

        for (var offset = -window; offset <= window && results.Count < maxResults; offset += 4)
        {
            uint pointer;
            try
            {
                pointer = BitConverter.ToUInt32(_memory.ReadBytes(baseAddress + offset, 4));
            }
            catch (OperationCanceledException)
            {
                // 会话已停（Stop 之后 FeatureMemoryAccess 会抛）⇒ 这是"扫描被中止"，
                // 不是"这个地址读不到"：吞掉会让调用方拿到一份假的"没找到"。
                throw;
            }
            catch
            {
                continue;
            }

            if (pointer is < 0x00400000 or > 0x7FFF0000)
            {
                continue;
            }

            try
            {
                var bytes = _memory.ReadBytes((nint)(int)pointer, 48);
                var text = new string(bytes.TakeWhile(b => b is >= 0x20 and < 0x7F).Select(b => (char)b).ToArray());
                if (text.Length >= 5 && text.All(c => char.IsLetterOrDigit(c) || c == '_'))
                {
                    results.Add($"{(offset >= 0 ? "+" : "-")}{Math.Abs(offset):X2} = {text}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 指针无效：跳过
            }
        }

        return results;
    }

    public byte ReadActionDispatch()
    {
        lock (_toggleGate)
        {
            _stopCancellation.Token.ThrowIfCancellationRequested();
            return _memory.ReadBytes(_resolver.Resolve(RemoteStateLayout.ActionDispatch), 1)[0];
        }
    }

    public void Reset(TrainerFeature feature)
    {
        EnterSynchronousDispatch();
        try
        {
            lock (_toggleGate)
            {
                // Never erase an unconsumed conversion/generation request, including after timeout
                // or cancellation: the game may still execute it later.
                if (feature.DispatchTarget is not null)
                {
                    var pending = _memory.ReadBytes(_resolver.Resolve(RemoteStateLayout.ActionDispatch), 1)[0];
                    var busy = _memory.ReadBytes(_resolver.Resolve(RemoteStateLayout.DispatchLock), 4);
                    if (pending is >= 0x0B and <= 0x0E || busy.Length != 4 || busy.Any(b => b != 0))
                        throw new InvalidOperationException("坐标动作尚未被游戏消费，不能清除动作槽；未尝试回滚生成。");
                }
                ResetCore(feature);
            }
        }
        finally { _dispatchGate.Release(); }
    }

    private void ResetCore(TrainerFeature feature)
    {
        if (IsToggleFeature(feature))
        {
            SetToggle(feature, false);
            return;
        }

        if (!IsActionFeature(feature))
        {
            return;
        }

        if (feature.DispatchTarget is not null)
        {
            _memory.WriteBytes(_resolver.Resolve(RemoteStateLayout.ActionDispatch), stackalloc[] { (byte)0 });
        }

        foreach (var flag in feature.EnableFlags)
        {
            _memory.WriteBytes(_resolver.Resolve(flag), stackalloc[] { (byte)0 });
        }
    }

    private static bool IsReinforcementFeature(TrainerFeature feature)
    {
        return feature.RawName.Equals("We Need Back", StringComparison.Ordinal) ||
            string.Equals(feature.DispatchTarget, "MustCode2+B00", StringComparison.OrdinalIgnoreCase);
    }

    private void WriteSettings((string Address, uint Value)[] settings)
    {
        WriteTransaction(settings.Select(s => (Address: _resolver.Resolve(s.Address), Bytes: BitConverter.GetBytes(s.Value))).ToArray());
    }

    private void WriteTransaction((nint Address, byte[] Bytes)[] writes)
    {
        lock (_toggleGate)
        {
            _stopCancellation.Token.ThrowIfCancellationRequested();
            RetryPendingToggleRestores();
            var originals = writes.Select(w => (w.Address, Bytes: _memory.ReadBytes(w.Address, w.Bytes.Length))).ToArray();
            try
            {
                for (var index = 0; index < writes.Length; index++)
                {
                    _pendingToggleRestores.Add(originals[index]);
                    _memory.WriteBytes(writes[index].Address, writes[index].Bytes);
                    if (!_memory.ReadBytes(writes[index].Address, writes[index].Bytes.Length).SequenceEqual(writes[index].Bytes))
                        throw new InvalidOperationException("参数写后读回不符。");
                }
                _pendingToggleRestores.Clear();
            }
            catch (Exception writeError)
            {
                SafetyMessage?.Invoke("参数写入或读回失败：" + writeError.Message);
                try { RetryPendingToggleRestores(); }
                catch (Exception restoreError)
                {
                    throw new AggregateException("参数写入失败且回滚未完成；恢复记录已保留。", writeError, restoreError);
                }
                throw;
            }
        }
    }
}
