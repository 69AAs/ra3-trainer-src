using Ra3Trainer.Core.Manifest;

namespace Ra3Trainer.Core.Hotkeys;

public sealed record HotkeyFeatureBinding(
    HotkeyGesture Gesture,
    TrainerFeature Feature,
    Action Execute);

public sealed class HotkeyFeatureDispatcher
{
    // 键位用 (虚拟键, 修饰键) 做键：原来只按虚拟键去重，Ctrl+F 按住时再按 Shift+F 会被当成自动重复，
    // 第二个手势永远不触发。线程安全：TryDispatch/Release 来自全局键盘钩子线程，Update/SetEnabled 来自 UI 线程。
    private readonly HashSet<(int VirtualKey, HotkeyModifiers Modifiers)> _pressedGestures = [];
    private readonly object _gate = new();
    private IReadOnlyDictionary<HotkeyGesture, HotkeyFeatureBinding[]> _bindings =
        new Dictionary<HotkeyGesture, HotkeyFeatureBinding[]>();

    public bool Enabled { get; private set; }

    /// <summary>
    /// 诊断：最近一次 TryDispatch 的结果（none/disabled/unmatched/suppressed/fired）。
    /// 这三条静默路径以前都不留痕 —— 用户实测"按了像没按"时无法归因。只在钩子线程读写。
    /// </summary>
    public string LastOutcome { get; private set; } = "none";

    public void Update(IEnumerable<HotkeyFeatureBinding> bindings, bool enabled)
    {
        var updated = bindings
            .GroupBy(binding => binding.Gesture)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());
        lock (_gate)
        {
            _bindings = updated;
            Enabled = enabled;
            _pressedGestures.Clear();
        }
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            Enabled = enabled;
            if (!enabled)
            {
                _pressedGestures.Clear();
            }
        }
    }

    public bool TryDispatch(int virtualKey, HotkeyModifiers modifiers)
    {
        HotkeyFeatureBinding[] bindings;
        lock (_gate)
        {
            if (!Enabled)
            {
                LastOutcome = "disabled";
                return false;
            }

            var gesture = _bindings.Keys.FirstOrDefault(key =>
                key.VirtualKey == virtualKey && key.Modifiers == modifiers);
            if (gesture is null || !_bindings.TryGetValue(gesture, out var found))
            {
                LastOutcome = "unmatched";
                return false;
            }

            if (!_pressedGestures.Add((virtualKey, modifiers)))
            {
                // 按下去了却没等到抬起（游戏全屏/切前台吞 keyup）⇒ 之后每次真按都被当成自动重复。
                LastOutcome = "suppressed";
                return true;
            }

            bindings = found;
        }

        LastOutcome = "fired";

        // Execute 放在锁外：动作可能回调进本类（切换开关等），在锁内执行会自锁。
        foreach (var binding in bindings)
        {
            binding.Execute();
        }

        return true;
    }

    public void Release(int virtualKey)
    {
        lock (_gate)
        {
            // 只知道松开的虚拟键（拿不到松开时的修饰键状态）：把这个键的所有组合都清掉。
            _pressedGestures.RemoveWhere(entry => entry.VirtualKey == virtualKey);
        }
    }

    /// <summary>
    /// 看门狗：清掉"还记着按下去、物理上其实已经松开"的键。返回清掉的数量。
    /// 没有它，一次被吞掉的 keyup 会让那个热键**整个会话**静默失效（真机实锤的怀疑路径）。
    /// </summary>
    public int ResetStuckKeys(Func<int, bool> isPhysicallyDown)
    {
        lock (_gate)
        {
            var stale = _pressedGestures.Where(entry => !isPhysicallyDown(entry.VirtualKey)).ToArray();
            foreach (var entry in stale) _pressedGestures.Remove(entry);
            return stale.Length;
        }
    }
}
