using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Hotkeys;
using Ra3Trainer.Core.Manifest;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class HotkeyFeatureDispatcherTests
{
    [Fact]
    public void TryDispatchIgnoresKeysWhenHotkeysAreDisabled()
    {
        var dispatcher = new HotkeyFeatureDispatcher();
        var invoked = 0;
        dispatcher.Update(
            [new HotkeyFeatureBinding(
                HotkeyGesture.Parse("P"),
                new TrainerFeature("Select Unit Level UP", "选择的单位快速升级", "P", [], "MustCode2+700", "0x08"),
                () => invoked++)],
            enabled: false);

        var handled = dispatcher.TryDispatch(0x50, HotkeyModifiers.None);

        Assert.False(handled);
        Assert.Equal(0, invoked);
    }

    [Fact]
    public void TryDispatchTriggersMatchingFeatureOncePerKeyPress()
    {
        var dispatcher = new HotkeyFeatureDispatcher();
        var invoked = 0;
        dispatcher.Update(
            [new HotkeyFeatureBinding(
                HotkeyGesture.Parse("P"),
                new TrainerFeature("Select Unit Level UP", "选择的单位快速升级", "P", [], "MustCode2+700", "0x08"),
                () => invoked++)],
            enabled: true);

        Assert.True(dispatcher.TryDispatch(0x50, HotkeyModifiers.None));
        Assert.True(dispatcher.TryDispatch(0x50, HotkeyModifiers.None));
        dispatcher.Release(0x50);
        Assert.True(dispatcher.TryDispatch(0x50, HotkeyModifiers.None));

        Assert.Equal(2, invoked);
    }

    [Fact]
    public void TryDispatchCanTriggerMultipleSourceTrainerActionsOnSameHotkey()
    {
        var dispatcher = new HotkeyFeatureDispatcher();
        var invoked = new List<string>();
        dispatcher.Update(
            [
                new HotkeyFeatureBinding(
                    HotkeyGesture.Parse("/"),
                    new TrainerFeature("Select Unit Change ID", "选择的建筑物/单位ID", "/", [], "MustCode2+800", "0x09"),
                    () => invoked.Add("id")),
                new HotkeyFeatureBinding(
                    HotkeyGesture.Parse("/"),
                    new TrainerFeature("Restore Danger Level Normal", "威胁等级恢复原状", "/", ["iEnable+13"], null, "0x00"),
                    () => invoked.Add("danger")),
                new HotkeyFeatureBinding(
                    HotkeyGesture.Parse("/"),
                    new TrainerFeature("Restore Select Ore Mine", "选择的矿脉恢复采集矿量", "/", ["iEnable+14"], null, "0x01"),
                    () => invoked.Add("ore"))
            ],
            enabled: true);

        var handled = dispatcher.TryDispatch(0xBF, HotkeyModifiers.None);

        Assert.True(handled);
        // 执行顺序 = 绑定列表顺序（Update 用 GroupBy→ToDictionary→ToArray 保持插入顺序）：
        // 同一个快捷键上的功能按清单顺序依次执行，这是刻意的契约，不是偶然。
        Assert.Equal(["id", "danger", "ore"], invoked);
    }

    [Fact]
    public void TryDispatchLatchIsKeyedOnKeyAndModifiers()
    {
        var dispatcher = new HotkeyFeatureDispatcher();
        var invoked = new List<string>();
        dispatcher.Update(
            [
                new HotkeyFeatureBinding(
                    HotkeyGesture.Parse("P"),
                    new TrainerFeature("Select Unit Level UP", "选择的单位快速升级", "P", [], "MustCode2+700", "0x08"),
                    () => invoked.Add("plain")),
                new HotkeyFeatureBinding(
                    HotkeyGesture.Parse("Ctrl+P"),
                    new TrainerFeature("Hold Position", "原地待命", "Ctrl+P", [], "MustCode2+704", "0x0A"),
                    () => invoked.Add("ctrl"))
            ],
            enabled: true);

        // 先按 Ctrl+P 再按纯 P：去重锁位必须按 (虚拟键, 修饰键) 记，
        // 只按虚拟键去重的话第二个手势会被当成自动重复，永远不执行。
        Assert.True(dispatcher.TryDispatch(0x50, HotkeyModifiers.Control));
        Assert.True(dispatcher.TryDispatch(0x50, HotkeyModifiers.None));
        Assert.Equal(new[] { "ctrl", "plain" }, invoked);

        // 同一组合按住不放仍然是自动重复：不再执行。
        Assert.True(dispatcher.TryDispatch(0x50, HotkeyModifiers.None));
        Assert.Equal(new[] { "ctrl", "plain" }, invoked);

        // 松开这个虚拟键会清掉它的所有修饰键组合。
        dispatcher.Release(0x50);
        Assert.True(dispatcher.TryDispatch(0x50, HotkeyModifiers.Control));
        Assert.Equal(new[] { "ctrl", "plain", "ctrl" }, invoked);
    }

    [Fact]
    public void TryDispatchIgnoresUnboundKeysAndWrongModifiers()
    {
        var dispatcher = new HotkeyFeatureDispatcher();
        var invoked = 0;
        dispatcher.Update(
            [new HotkeyFeatureBinding(
                HotkeyGesture.Parse("P"),
                new TrainerFeature("Select Unit Level UP", "选择的单位快速升级", "P", [], "MustCode2+700", "0x08"),
                () => invoked++)],
            enabled: true);

        Assert.False(dispatcher.TryDispatch(0x51, HotkeyModifiers.None));      // 没绑定的键
        Assert.False(dispatcher.TryDispatch(0x50, HotkeyModifiers.Control));   // 修饰键不匹配
        Assert.False(dispatcher.TryDispatch(0x50, HotkeyModifiers.Shift));
        Assert.Equal(0, invoked);
    }

    [Fact]
    public void DispatcherTestsRelyOnTheParsedVirtualKeyMapping()
    {
        // 本文件里的 0x50/0xBF 必须与 HotkeyGesture 的映射一致：映射一改这里先红，
        // 而不是让按键断言悄悄失配、只以"没触发"的形式间接失败。
        Assert.Equal(0x50, HotkeyGesture.Parse("P").VirtualKey);
        Assert.Equal(0xBF, HotkeyGesture.Parse("/").VirtualKey);
    }
}
