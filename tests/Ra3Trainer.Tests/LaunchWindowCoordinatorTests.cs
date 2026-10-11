using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class LaunchWindowCoordinatorTests
{
    [Fact]
    public void DefaultAndLegacyConstructorEnableAutoFloat()
    {
        Assert.True(TrainerAppSettings.Default.AutoFloatAfterLaunch);
        Assert.True(new TrainerAppSettings("", "", 30).AutoFloatAfterLaunch);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SettingRoundTrips(bool enabled)
    {
        var path = Path.Combine(Path.GetTempPath(), $"round7-{Guid.NewGuid():N}.json");
        try
        {
            var store = new TrainerAppSettingsStore(path);
            store.Save(new TrainerAppSettings("", "-win", 30, ResourceValueSettings.Default, [], AutoFloatAfterLaunch: enabled));
            Assert.Equal(enabled, store.Load().AutoFloatAfterLaunch);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"AutoFloatAfterLaunch\":null}")]
    [InlineData("{\"AutoFloatAfterLaunch\":\"false\"}")]
    public void MissingOrMalformedSettingUsesEnabledDefault(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"round7-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);
            Assert.True(new TrainerAppSettingsStore(path).Load().AutoFloatAfterLaunch);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OnlyCompleteSuccessWithSettingCanHideMain(bool success, bool enabled)
    {
        var modes = new List<LaunchWindowMode>();
        var completion = new TaskCompletionSource<bool>();
        var task = LaunchWindowCoordinator.RunAsync(() => completion.Task, () => enabled, modes.Add);
        Assert.Equal(new[] { LaunchWindowMode.Main }, modes);
        completion.SetResult(success);
        await task;
        Assert.Equal(new[] { LaunchWindowMode.Main,
            success && enabled ? LaunchWindowMode.Floating : LaunchWindowMode.Main }, modes);
    }

    [Theory]
    [InlineData("launch")]
    [InlineData("attach-timeout")]
    [InlineData("load")]
    public async Task FailureKeepsMainAndPreservesError(string stage)
    {
        var modes = new List<LaunchWindowMode>();
        var expected = new InvalidOperationException(stage);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LaunchWindowCoordinator.RunAsync(() => Task.FromException<bool>(expected), () => true, modes.Add));
        Assert.Same(expected, error);
        Assert.Equal(new[] { LaunchWindowMode.Main, LaunchWindowMode.Main }, modes);
    }

    [Fact]
    public async Task CancelledLaunchRestoresMainAndPropagatesCancellation()
    {
        var modes = new List<LaunchWindowMode>();
        var completion = new TaskCompletionSource<bool>();
        var task = LaunchWindowCoordinator.RunAsync(() => completion.Task, () => true, modes.Add);
        Assert.Equal(new[] { LaunchWindowMode.Main }, modes);

        completion.SetCanceled(); // attach-timeout 之类被取消的启动

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        // 取消路径同样要还原主窗（收尾在 finally 里），而不是留在浮窗状态。
        Assert.Equal(new[] { LaunchWindowMode.Main, LaunchWindowMode.Main }, modes);
    }

    [Fact]
    public async Task AutoFloatEnabledDuringTheWaitAlsoFloats()
    {
        var enabled = false;
        var modes = new List<LaunchWindowMode>();
        var completion = new TaskCompletionSource<bool>();
        var task = LaunchWindowCoordinator.RunAsync(() => completion.Task, () => enabled, modes.Add);

        enabled = true; // 等待期间用户自己打开了自动浮窗

        completion.SetResult(true);
        await task;
        // 收尾时惰性重读开关：不能把调用瞬间的快照当成最终决定。
        Assert.Equal(new[] { LaunchWindowMode.Main, LaunchWindowMode.Floating }, modes);
    }

    [Fact]
    public async Task FailedLaunchNeverEvaluatesTheAutoFloatPredicate()
    {
        var evaluated = 0;
        var modes = new List<LaunchWindowMode>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => LaunchWindowCoordinator.RunAsync(
            () => Task.FromException<bool>(new InvalidOperationException("launch")),
            () => { evaluated++; return true; },
            modes.Add));

        // 失败必须短路：读开关会碰 VM 状态，不该在失败收尾时发生。
        Assert.Equal(0, evaluated);
        Assert.Equal(new[] { LaunchWindowMode.Main, LaunchWindowMode.Main }, modes);
    }

    [Fact]
    public async Task UserCanDisableAutoFloatWhileLauncherIsWaiting()
    {
        var enabled = true;
        var modes = new List<LaunchWindowMode>();
        var completion = new TaskCompletionSource<bool>();
        var task = LaunchWindowCoordinator.RunAsync(() => completion.Task, () => enabled, modes.Add);
        enabled = false;
        completion.SetResult(true);
        await task;
        Assert.DoesNotContain(LaunchWindowMode.Floating, modes);
    }
}
