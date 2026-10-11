using System.Diagnostics;
using System.IO;
using System.Reflection;
using Ra3Trainer.Core.Runtime;

namespace Ra3Trainer.App.Diagnostics;

public interface IProductOverlay
{
    string Status { get; }
    void BeforeLaunch(string path, IReadOnlyList<LauncherCandidate> candidates);
    void Attach(int pid, string version);
    void Publish(bool enabled, bool connected, int count, string action);
    void Stop();
    void Disconnect();
    void Cleanup();
    void StartCleanupWorker();
}

/// <summary>Optional OSD orchestration. Errors never escape into game-hook restoration.</summary>
public sealed class ProductOverlay : IProductOverlay
{
    private readonly Action<string, string> _log;
    private readonly OverlayDeployment _deployment = new(OverlayDeployment.JournalPath);
    private OverlayStatusSlot? _slot;
    private OverlayPanelSlot? _panelSlot;
    private OffscreenPanel? _panel;
    private readonly Func<OffscreenPanel>? _createPanel;
    private bool _panelFailed;
    private bool _panelFailedLogged;
    private bool _feedbackUnreadableLogged;
    // M3: clicks are polled on their own light timer (the panel itself only republishes
    // once a second, which would make a click feel dead). This path never renders.
    private System.Windows.Threading.DispatcherTimer? _clickTimer;
    private long _lastClicks = -1;
    private long _lastKeys = -1;
    // 面板里的滚轮计数（原生侧连 delta 一起发布，宿主只取差值）。
    private int _lastWheel = -1;
    // One poll can owe at most this many repeated presses. Bounded so a stalled UI
    // thread (or a wrapped counter) can never turn into a burst of actions.
    private const int MaxKeyRepeatsPerPoll = 8;
    private bool _clickArmedLogged;
    private string _version = string.Empty;
    private int? _pendingPid;
    private int? _feedbackPid;
    private long _feedbackCreation;
    private readonly OverlayEntryRelay _entryRelay = new();
    private readonly OverlayLogReader _nativeLogReader = new();
    private bool _stopped;
    private string? _lastError;
    private readonly Mutex _lease = new(false, @"Local\Ra3Trainer.Overlay.Deployment");
    private bool _ownsLease;
    public string Status { get; private set; } = "游戏内面板：等待下次启动部署。";
    public ProductOverlay(Action<string, string> log, Func<OffscreenPanel>? createPanel = null)
    {
        _log = log;
        _createPanel = createPanel;
        try { _ownsLease = _lease.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsLease = true; }
    }

    public void BeforeLaunch(string launcherPath, IReadOnlyList<LauncherCandidate> candidates)
    {
        Try(() =>
        {
            if (!_ownsLease) throw new IOException("OSD 部署清理仍在进行。");
            bool running = false;
            foreach (var process in Process.GetProcesses()) running |= IsGameAndDispose(process);
            if (running) throw new IOException("游戏已运行，本次不部署；退出游戏后重试一键启动。");
            if (!_deployment.Cleanup(ReadPayload())) throw new IOException("DLL 仍被占用，等待退出后清理。");
            var roots = candidates.Where(c => c.Kind == LauncherKind.GameExecutable)
                .Select(c => Path.GetDirectoryName(c.Path)!).Append(Path.GetDirectoryName(launcherPath)!)
                .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
            var games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                foreach (var directory in new[] { root, Path.Combine(root, "Data") })
                    if (Directory.Exists(directory))
                        foreach (var game in Directory.EnumerateFiles(directory, "*.game"))
                            if (Path.GetFileName(game).Equals("RA3_1.12.game", StringComparison.OrdinalIgnoreCase)) games.Add(Path.GetFullPath(game));
            }
            if (games.Count != 1) throw new IOException($"游戏镜像候选数={games.Count}，不能确定部署目录；OSD 不安装。");
            _deployment.Deploy(games.Single(), ReadPayload());
            Status = "游戏内面板：已部署，等待游戏加载及设备回调。";
            _log("osd-deployed", "owned-proxy journal-written");
        });
    }

    private static byte[] ReadPayload()
    {
        using var resource = typeof(ProductOverlay).Assembly.GetManifestResourceStream("Ra3Overlay.dll")
            ?? throw new IOException("嵌入的 OSD 资源缺失。");
        using var buffer = new MemoryStream(); resource.CopyTo(buffer); return buffer.ToArray();
    }

    private static bool IsGameAndDispose(Process process)
    {
        using (process)
        {
            try { return process.ProcessName.StartsWith("ra3_", StringComparison.OrdinalIgnoreCase); }
            catch (InvalidOperationException) { return false; }
        }
    }

    public void Attach(int pid, string version)
    {
        _pendingPid = pid;
        Try(() =>
        {
            _version = version;
            using var process = Process.GetProcessById(pid);
            _feedbackPid = pid;
            _feedbackCreation = process.StartTime.ToFileTimeUtc();
            _stopped = false;
            if (!File.Exists(OverlayDeployment.JournalPath))
                throw new IOException("未确认自有代理已加载；游戏已启动时不会事后猜设备或注入。");
            var record = System.Text.Json.JsonSerializer.Deserialize<OverlayDeploymentRecord>(File.ReadAllText(OverlayDeployment.JournalPath));
            var loaded = record is null ? null : process.Modules.Cast<ProcessModule>().FirstOrDefault(m =>
                Path.GetFullPath(m.FileName).Equals(record.Target, StringComparison.OrdinalIgnoreCase));
            if (record is null || loaded is null)
                throw new IOException("游戏使用其他 D3D9 模块；OSD 不可用。");
            using var file = File.OpenRead(loaded.FileName);
            if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)) != record.Sha256)
                throw new IOException("已加载代理的文件哈希不符。");
            try
            {
                _slot = new OverlayStatusSlot(pid, process.StartTime.ToFileTimeUtc());
                _panelFailed = false;
                if (_createPanel is not null)
                {
                    _panelSlot = new OverlayPanelSlot(pid, process.StartTime.ToFileTimeUtc());
                    _panel = _createPanel();
                    _lastClicks = -1; _lastKeys = -1; _clickArmedLogged = false;   // never replay a click/key that happened before we attached
                }
            }
            catch
            {
                _panelSlot?.Dispose(); _panelSlot = null;
                _slot?.Dispose(); _slot = null;
                throw;
            }
            _pendingPid = null;
            StartHeartbeat();
            Status = "游戏内面板：代理已加载；可见性以游戏画面及 osd-panel-drawn 日志为准。";
            _log("osd-status-slot", $"pid={pid} bytes={OverlayStatusSlot.Size} host-writer dll-read-only");
        });
    }

    public void Publish(bool enabled, bool connected, int count, string action)
    {
        if (_pendingPid is int pid) Attach(pid, _version);
        Try(() => _slot?.Publish(enabled, connected, count, _version, action));
        Try(() =>
        {
            if (!enabled || !connected || _panelFailed) { _panelSlot?.Disable(); return; }
            if (_panelSlot is null || _panel is null) return;
            // M4: Ins is the show/hide key, so hiding must really hide. Without this the
            // panel stayed on screen (it is drawn whenever the mapping is valid) and the
            // key only flipped a line of text.
            if (!_panel.PanelMode) { _panelSlot.Disable(); _clickTimer?.Stop(); return; }
            try
            {
                _panelSlot.PanelMode = _panel.PanelMode;
            // The 80 ms event poll must be started from here: TryDispatchPanelEvents is the
            // method this timer calls, so it can never start itself.
            if (_panel.PanelMode)
            {
                _clickTimer ??= new System.Windows.Threading.DispatcherTimer(
                    TimeSpan.FromMilliseconds(25), System.Windows.Threading.DispatcherPriority.Input,
                    (_, _) => TryDispatchPanelEvents(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
                if (!_clickTimer.IsEnabled) _clickTimer.Start();
            }
            else _clickTimer?.Stop();
            var publishedNow = _panelSlot.Publish(_panel.Pixels(), _panel.PanelWidth, _panel.PanelHeight, _panel.X, _panel.Y);
            if (_panelFailed)
            {
                // 一次瞬时异常不该让面板死一整个会话（真机实锤：面板中段变黑后整场没回来）。
                _panelFailed = false;
                _panelFailedLogged = false;
                _log("osd-panel-recovered", "面板已恢复发布");
            }
            if (publishedNow)
                {
                    try { _log("osd-panel-published", $"generation={_panelSlot.Generation} w={_panel.PanelWidth} h={_panel.PanelHeight} fmt=Pbgra32 pos={_panel.X},{_panel.Y}"); }
                    catch { /* Diagnostics cannot disable a successfully published frame. */ }
                }
            }
            catch
            {
                _panelFailed = true;
                if (!_panelFailedLogged)
                {
                    _panelFailedLogged = true;
                    _log("osd-panel-publish-failed", "面板发布抛异常：本会话面板已停用（原状态只有重连才会复位）");
                }
                _panelSlot.Disable();
                throw;
            }
        });
        RefreshFeedback();
    }

    /// <summary>
    /// M3/M4: the game side counts left-button presses and panel keys raised while
    /// panel mode is on. A change in either count means exactly one input: map it
    /// onto the offscreen panel and let that control run its own command. Everything
    /// here fails closed: no panel mode, no event, a torn read or an unknown ABI
    /// layout = do nothing.
    /// </summary>
    private void TryDispatchPanelEvents()
    {
        try
        {
            if (_panelSlot is null || _panel is null || _panelFailed) { _clickTimer?.Stop(); return; }
            if (!_panel.PanelMode) { _clickTimer?.Stop(); _lastClicks = -1; _lastKeys = -1; _lastWheel = -1; return; }
            if (!_panelSlot.TryReadEvent(out var e) || e.Mode != 1) return;
            if (!_clickArmedLogged) { _clickArmedLogged = true; _log("osd-panel-click-armed", $"timer-running clicks={e.Clicks} keys={e.KeyCount} mode={e.Mode}"); }
            if (_lastClicks < 0) { _lastClicks = e.Clicks; _lastKeys = e.KeyCount; _lastWheel = -1; return; }   // prime only, never replay history
            if (e.Clicks != _lastClicks) DispatchPanelClick(e);
            if (e.KeyCount != _lastKeys) DispatchPanelKey(e);
            // 面板里的滚轮：原生侧连 delta 一起发布，行板比视口高时用它翻（面板只有 360×440）。
            if (_lastWheel < 0) _lastWheel = (int)e.Wheel;
            else if ((int)e.Wheel != _lastWheel)
            {
                var delta = (int)e.Wheel - _lastWheel;
                _lastWheel = (int)e.Wheel;
                DispatchPanelWheel(delta);
            }
        }
        catch (Exception ex)
        {
            // A click or a key must never be able to break the overlay or the game session.
            _log("osd-panel-click-failed", ex.GetType().Name);
        }
    }

    private void DispatchPanelClick(OverlayPanelSlot.PanelEvent e)
    {
        var dropped = e.Clicks - _lastClicks - 1;
        _lastClicks = e.Clicks;
        if (!PanelClickMap.TryMap(e.ClickX, e.ClickY, _panel!.X, _panel.Y,
                                  _panel.PanelWidth, _panel.PanelHeight, out var x, out var y))
        {
            // Named, never silent: this is the difference between "outside the panel"
            // and "the click never reached us at all".
            _log("osd-panel-click-outside", $"pos={e.ClickX},{e.ClickY} panel={_panel.X},{_panel.Y} clicks={e.Clicks}");
            return;
        }
        var invoked = _panel.TryInvokeAt(x, y, out var hit);
        _log("osd-panel-click", $"pos={e.ClickX},{e.ClickY} local={x},{y} hit={(invoked ? hit : "none")} dropped={dropped}");
    }

    /// <summary>
    /// M4: the numpad key the game swallowed, applied to the panel's own key table.
    /// The DLL only ever reports codes from its panel key set; anything else maps to
    /// None in the pure key map and therefore cannot fire an action.
    /// </summary>
    private void DispatchPanelKey(OverlayPanelSlot.PanelEvent e)
    {
        // The native side counts every numpad key it swallows, so a poll that lands
        // late must not lose the presses in between: mashing or holding a key
        // auto-repeats ~30/s and the poll is 25 ms. Replaying the last key for each
        // missed press is exact in practice - two *different* numpad keys cannot be
        // pressed 25 ms apart by hand. Measured before this fix, in one 9-minute
        // session: 364 presses seen, 28 silently dropped (dropped=1x16, dropped=2x6).
        var missed = e.KeyCount - _lastKeys;
        var repeats = Math.Clamp(missed, 1, MaxKeyRepeatsPerPoll);
        var dropped = missed - repeats;
        _lastKeys = e.KeyCount;
        var command = _panel!.DispatchPanelKey((int)e.LastKey);
        for (var i = 1; i < repeats; i++) command = _panel.DispatchPanelKey((int)e.LastKey);
        _log("osd-panel-key", $"vk=0x{e.LastKey:X2} cmd={command} repeats={repeats} dropped={dropped} keys={e.KeyCount}");
    }

    /// <summary>
    /// 面板里的滚轮：一格 120 → 行板滚 120px。面板只有 360×440，主层 16 行放不下，
    /// 用它翻比"再来一个光标"强（原生侧本来就吞掉了面板内的滚轮）。
    /// </summary>
    private void DispatchPanelWheel(int delta)
    {
        try
        {
            _panel?.DispatchPanelWheel(delta);
        }
        catch (Exception ex)
        {
            _log("osd-panel-wheel-failed", ex.Message);
        }
    }

    private void RefreshFeedback()
    {
        if (_feedbackPid is not int pid) return;
        try
        {
            using var stream = new FileStream(Path.Combine(Path.GetTempPath(), $"Ra3Overlay-{pid}.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Bounded incremental work also follows failures after a long-running session's log exceeds 1 MiB.
            var nativeLog = _nativeLogReader.Read(stream, _feedbackCreation);
            _entryRelay.Forward(nativeLog, _feedbackCreation, _log);
            var feedback = OverlayFeedback.Parse(nativeLog, _feedbackCreation);
            if (!_panelFailed && feedback is not null && (!_stopped || feedback.Contains("本局未安装", StringComparison.Ordinal)))
            {
                if (Status != feedback) _log("osd-native-feedback", feedback);
                Status = feedback;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 以前这里是空 catch：原生日志读不到时，界面会一直"没有新反馈"，而用户完全不知道。
            if (_feedbackUnreadableLogged) return;
            _feedbackUnreadableLogged = true;
            _log("osd-feedback-unreadable", ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// 心跳必须跟着**墙钟**走，不能只在状态变化时写：游戏里安静打一会儿没人按键，
    /// 原生侧 5 秒收不到心跳就判 heartbeat-expired、把面板连同映射一起释放 ——
    /// 表现就是"面板打着打着自己没了"。用后台定时器而不是 DispatcherTimer：
    /// 界面线程被装 patch／导出取证／扫描启动器占住时，心跳照样跳。
    /// </summary>
    private const int HeartbeatIntervalMs = 1000;
    private System.Threading.Timer? _heartbeat;

    private void StartHeartbeat() =>
        _heartbeat ??= new System.Threading.Timer(_ => TouchHeartbeat(), null, HeartbeatIntervalMs, HeartbeatIntervalMs);

    private void TouchHeartbeat()
    {
        try
        {
            _slot?.Touch();
            _panelSlot?.Touch();
        }
        catch (Exception ex)
        {
            // 心跳失败不改任何状态：原生侧会按超时自己收面板，这里只留痕。
            _log("osd-heartbeat-failed", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void StopHeartbeat()
    {
        _heartbeat?.Dispose();
        _heartbeat = null;
    }

    public void Stop()
    {
        StopHeartbeat();
        // 清掉"待挂接"的 pid：否则 Stop 之后任何一次 Publish（界面定时器还会调）都会
        // 拿它再 Attach 一遍 —— 用户刚停掉的面板会自己回来。
        _pendingPid = null;
        _clickTimer?.Stop();
        _lastClicks = -1; _lastKeys = -1; _clickArmedLogged = false;
        Publish(false, false, 0, "一键全撤／断开");
        _log("osd-stop-request", "OverlayStop(FALSE) on next Present; no unload proof; game restore independent");
        Status = _slot is null ? "OSD 未确认加载；不影响游戏 hook 全撤。"
            : "OSD 已请求停绘；已安装的入口、设备引用及模块刻意留驻至游戏退出。";
        _stopped = true;
        RefreshFeedback();
    }

    public void Disconnect()
    {
        _pendingPid = null;
        Stop();
        _feedbackPid = null;
        // 先把字段清空再 Dispose：心跳是后台线程，原来"先 Dispose 再置 null"中间那段窗口里
        // 心跳会去 Touch 一个已经释放的槽位（虽然被 catch 住，但每秒一条噪声日志）。
        var slot = _slot;
        _slot = null;
        Try(() => slot?.Dispose());
        var panelSlot = _panelSlot;
        _panelSlot = null;
        Try(() => panelSlot?.Dispose());
        Try(() => { _panel?.Dispose(); _panel = null; });
        Cleanup();
    }

    public void Cleanup() => Try(() =>
    {
        if (!_ownsLease) return;
        var done = _deployment.Cleanup(ReadPayload());
        _log(done ? "osd-cleanup-complete" : "osd-cleanup-pending", done ? "owned-files-removed" : "loaded-or-locked; journal-retained");
    });

    public void StartCleanupWorker()
    {
        if (!File.Exists(OverlayDeployment.JournalPath)) { ReleaseLease(); return; }
        Try(() =>
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            info.ArgumentList.Add("--overlay-cleanup");
            info.ArgumentList.Add(Environment.ProcessId.ToString());
            using var worker = Process.Start(info);
            _log("osd-cleanup-worker", "wait-for-host-exit then retry owned DLL until unlocked");
        });
        ReleaseLease();
    }

    private void ReleaseLease()
    {
        if (_ownsLease) { _lease.ReleaseMutex(); _ownsLease = false; }
        _lease.Dispose();
    }

    public static Task RunCleanupWorker(int ownerPid) => RunCleanupWorkerCore(ownerPid, OverlayDeployment.JournalPath);

    internal static async Task RunCleanupWorkerCore(int ownerPid, string journalPath)
    {
        if (!File.Exists(journalPath))
        {
            var folder = Path.GetDirectoryName(journalPath)!;
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "overlay-cleanup.log"), "osd-cleanup-complete no-work" + Environment.NewLine);
            return;
        }
        try { if (ownerPid > 0) { using var owner = Process.GetProcessById(ownerPid); await owner.WaitForExitAsync(); } }
        catch (ArgumentException) { /* Parent already exited. */ }
        var deployment = new OverlayDeployment(journalPath);
        // A bounded worker; persistent journal also recovers after crash/reboot.
        for (var i = 0; i < 86400; i++)
        {
            // Another worker may have completed while we waited for the owner/lease.
            if (!File.Exists(journalPath))
            {
                var folder = Path.GetDirectoryName(journalPath)!;
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "overlay-cleanup.log"), "osd-cleanup-complete no-work" + Environment.NewLine);
                return;
            }
            using var lease = new Mutex(false, @"Local\Ra3Trainer.Overlay.Deployment");
            bool acquired;
            try { acquired = lease.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            try
            {
                if (acquired && deployment.Cleanup(ReadPayload()))
                {
                    var folder = Path.GetDirectoryName(journalPath)!;
                    Directory.CreateDirectory(folder);
                    File.AppendAllText(Path.Combine(folder, "overlay-cleanup.log"), "osd-cleanup-complete owned-files-removed" + Environment.NewLine);
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { File.AppendAllText(Path.Combine(Path.GetDirectoryName(journalPath)!, "overlay-cleanup.log"), ex.Message + Environment.NewLine); return; }
            finally { if (acquired) lease.ReleaseMutex(); }
            await Task.Delay(1000);
        }
    }

    private void Try(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            Status = "游戏内面板不可用：" + ex.Message;
            if (_lastError != ex.Message) _log("osd-unavailable", ex.Message);
            _lastError = ex.Message;
        }
    }
}
