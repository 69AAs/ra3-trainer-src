using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ra3Trainer.App;
using Ra3Trainer.App.ViewModels;
using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Runtime;
using Ra3Trainer.App.Views;
using Ra3Trainer.Core.Memory;

internal static class Program
{
    private static int _assertions;
    [STAThread]
    private static int Main(string[] args)
    {
        try { Run(); Console.WriteLine($"PASS: {_assertions} assertions; {RuntimeInformation.ProcessArchitecture} inline selector; no input injection."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Run()
    {
        Check(RuntimeInformation.ProcessArchitecture is Architecture.X86 or Architecture.X64, "supported Windows process architecture");
        var highSample = new ReadOnlyProbe(new SmokeHighProbeMemory(),
            new AddressResolver(0x400000, new Dictionary<string, nint>()), "high-address fake")
            .Capture(false, DateTimeOffset.UnixEpoch);
        Check(highSample.SelectionStable && highSample.Fields["head.health.4"].Value == 100,
            "x86 high user addresses remain readable through the entire fake health chain");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/Ra3Trainer.App;component/Themes/GlobalStyles.xaml", UriKind.Relative) });
        var store = new TrainerAppSettingsStore(Path.Combine(AppContext.BaseDirectory, "smoke-settings.json"));
        store.Save(TrainerAppSettings.Default);
        var manifest = TrainerRuntimeAssets.LoadManifest();
        var launcher = new SmokeLauncher();
        var locator = new SmokeLocator();
        var memory = new SmokeMemory();
        var suspender = new SmokeSuspender();
        var memoryCreations = 0;
        var allocatorCreations = 0;
        var probeCreations = 0;
        var candidate = new LauncherCandidate(Path.Combine(AppContext.BaseDirectory, "CoronaLauncher.exe"),
            "mod 启动器 · CoronaLauncher（假件）", LauncherKind.ModLauncher);
        using var vm = new MainViewModel(manifest, store, launcher, locator,
            _ => { memoryCreations++; return memory; },
            _ => { allocatorCreations++; return memory; }, suspender, _ => [candidate],
            _ => { probeCreations++; throw new InvalidOperationException("Unexpected probe open on disconnected VM"); });
        Check(ReferenceEquals(vm.SelectedLauncher, candidate) && vm.LauncherPath == candidate.Path,
            "synthetic launcher is automatically preselected regardless of installed launchers");
        // 桌面浮窗形态（发丝状态线/迷你条/全条）已删除：该窗口现在只剩"面板渲染器"一种形态，
        // 固定 360×440 且永远展开。旧代码用 SelectedFloatWindowForm = FullBar 强制展开，现在没有形态可切
        // ⇒ 打开面板模式（产品里"游戏内面板"就是这个状态）。
        vm.PanelMode = true;
        // 这 40 条长名预设原来喂给**旧浮窗上的预设列表**；面板没有预设列表（增援页是收藏槽）⇒
        // 留给主窗「增援」页的 PresetSelector（CheckConsolePages 会用），面板另测收藏槽。
        vm.ReinforcementPresets.Clear();
        for (var i = 0; i < 40; i++)
            vm.ReinforcementPresets.Add(new ReinforcementPreset($"预设 {i:D2}：中文长名称用于检查换行与底部滚动可达", (uint)(100 + i), 2, 1));
        var foreground = GetForegroundWindow();
        var focus = GetFocus();
        var activated = 0;
        var window = new FloatingWindow(vm) { Left = -30000, Top = -30000 };
        // Keep production placement/clamping from moving this test HWND onto the desktop.
        window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)
            .AddHook(KeepOffscreen);
        window.Activated += (_, _) => activated++;
        // Production Loaded clamps saved placement; keep this harness offscreen before rendering.
        window.Loaded += (_, _) => { window.Left = -30000; window.Top = -30000; };
        try
        {
            window.Show(); Pump();
            var hwnd = new WindowInteropHelper(window).Handle;
            Check((GetWindowLong(hwnd, -20) & 0x08000000) != 0, "real HWND WS_EX_NOACTIVATE");
            Check(SendMessage(hwnd, 0x21, IntPtr.Zero, new IntPtr(0x02010001)).ToInt32() == 3,
                "real WM_MOUSEACTIVATE returns MA_NOACTIVATE (not a mouse injection)");
            // 面板没有「展开/收起」开关（ToggleButton 已随桌面浮窗删除），也没有 ReinforcementPage / PresetsPage
            // 这两个主窗页名 —— 面板的页签是「功能/增援/选单位」，内容挂在行板 PanelBoardScroll 上。
            var board = (ScrollViewer)window.FindName("PanelBoardScroll");
            Check(board is not null && window.ActualWidth == 360 && window.ActualHeight == 440,
                "面板是该窗口唯一形态：行板已实现且恒为 360×440");
            CheckPanelRowsAreMouseReachable(vm, window, board!, foreground, focus, () => activated);
            RenderPanelEvidence(window, board!);
            CheckConsolePages(app, vm, window);
            var memoryApi = new SmokeMemoryApi();
            using (var localMemory = new Win32ProcessMemory(1234, memoryApi))
            {
                var space = localMemory.InspectAddressSpace();
                Check(space.Complete && space.LargestAlignedBlock >= 0x10000,
                    "preflight enumerates synthetic API regions on both architectures");
                var block = localMemory.Allocate(0x4200);
                localMemory.Free(block);
                Check(localMemory.Query(block)?.State == 0x10000 && memoryApi.Freed == (block, (nuint)0, 0x8000u),
                    "MEM_RELEASE uses combined base, zero size and release flag through fake API");
            }
            CheckIsolatedQuiescence();
            CheckReadOnlyProbeLifecycle();
            CheckCoordinateCountdown(store);
            CheckLaunchWindowTransitions(app, vm, window, store, foreground, focus, launcher, candidate);
            vm.RefreshCommand.Execute(null);
            Check(locator.Finds > 0 && vm.ConnectionState == ConnectionState.Disconnected,
                "refresh uses fake locator and cannot attach to a running game");
            vm.Dispose();
            Check(launcher.Calls.SequenceEqual(new[] { candidate.Path }) &&
                memoryCreations == 0 && allocatorCreations == 0 && probeCreations == 0 && memory.Calls == 0 && suspender.Events.Count == 0,
                "本次运行没有启动任何外部进程：唯一 Start 是无原生回退的假件，未附加、分配、写内存或挂起真实进程");
            Check(activated == 0 && GetForegroundWindow() == foreground && GetFocus() == focus,
                "final full navigation/render/keypad/picker lifecycle audit: Activated == 0 and foreground/focus unchanged");
            Console.WriteLine($"HWND=0x{hwnd.ToInt64():X}; ExStyle=0x{GetWindowLong(hwnd, -20):X8}; WM_MOUSEACTIVATE=3; rows=40; Activated={activated}");
        }
        finally { window.Close(); app.Shutdown(); }
    }

    /// <summary>
    /// 旧 CheckMouseOnlyConsole 点的是**浮窗上的数字键盘**（NumberPad）和"维护"页上的按钮。
    /// 数字键盘整个删除了（面板永不激活 ⇒ 键盘输入根本进不去），"维护"页改名"诊断"且只存在于主窗。
    /// 所以这里只保留真正还在的那些断言：预设自动命名 / 只填参数不发兵、两个安全开关互斥、
    /// 安全采样默认关且不可获焦、未连接时不开原生句柄、主窗原有命令的绑定还在。
    /// 全程按"纯鼠标"路径执行，每步都断言不建新 HWND、不抢前景/焦点。
    /// </summary>
    private static void CheckConsoleMouseOnly(MainViewModel vm, MainWindow main)
    {
        var windows = ThreadWindows();
        var foreground = GetForegroundWindow();
        var focus = GetFocus();
        void Untouched(string what) => Check(
            windows.SetEquals(ThreadWindows()) && GetForegroundWindow() == foreground && GetFocus() == focus,
            "console mouse path keeps HWND/foreground/focus: " + what);

        // 「增援」页：存为预设走真实按钮，自动命名，不需要输入框。
        main.Navigation.Selected = "增援"; Pump();
        vm.SelectedReinforcementUnit = vm.ReinforcementUnits.First(unit => unit.Code == 0x139CBC97);
        var reinforcement = (ScrollViewer)main.FindName("ReinforcementPage");
        var save = Descendants(reinforcement).OfType<Button>().Single(button => (button.Content as string) == "存为预设");
        Invoke(save); Pump();
        Check(vm.SelectedReinforcementPreset?.Name == $"{vm.SelectedReinforcementUnit.Name}×{vm.ReinforcementCountText} / {vm.ReinforcementRankText}星",
            "main saves current configuration with automatic name, no text entry");
        Untouched("save preset");
        var parameters = (vm.ReinforcementUnitIdText, vm.ReinforcementCountText, vm.ReinforcementRankText);
        vm.ApplyReinforcementPresetCommand.Execute(null);
        Check(parameters == (vm.ReinforcementUnitIdText, vm.ReinforcementCountText, vm.ReinforcementRankText),
            "loading preset only fills parameters, does not dispatch");

        // 两个高风险开关 + 安全采样：重构后它们**分散在两页**（旧断言当成"维护页上相邻的 checks[0]/checks[1]"，
        // 现在按 index 取会取到「面板模式 / 游戏内面板 M1」那两个无关开关）⇒ 一律按标签文字取。
        var pageNames = new[] { ("启动", "StartPage"), ("增援", "ReinforcementPage"), ("功能", "FeaturesPage"), ("诊断", "DiagnosticsPage") };
        CheckBox SwitchOn(string pageName, string label)
        {
            main.Navigation.Selected = pageNames.Single(item => item.Item2 == pageName).Item1; Pump();
            var page = (ScrollViewer)main.FindName(pageName);
            return Descendants(page).OfType<CheckBox>().Single(box => (box.Content as TextBlock)?.Text == label);
        }
        var nonstandard = SwitchOn("StartPage", "允许非标准版本");
        var probeCheck = SwitchOn("StartPage", "安全采样（只读，零写入）");
        var worldPosition = SwitchOn("DiagnosticsPage", "开启高风险功能（单位操作、威胁等级、矿点；可能不稳定，需要时再开）");

        // 每个开关只动自己那个标志，不牵连另一个（两个 setter 之间**没有**互相复位的逻辑）。
        nonstandard.IsChecked = true; Pump();
        Check(vm.AllowNonStandardVersion && !vm.AllowUnverifiedWorldPositionFeatures,
            "nonstandard override toggles only its own flag");
        nonstandard.IsChecked = false; worldPosition.IsChecked = true; Pump();
        Check(!vm.AllowNonStandardVersion && vm.AllowUnverifiedWorldPositionFeatures,
            "world-position override toggles only its own flag");
        worldPosition.IsChecked = false; Pump();
        Check(!vm.AllowNonStandardVersion && !vm.AllowUnverifiedWorldPositionFeatures, "both overrides returned to safe defaults");

        Check(!vm.SafeSamplingEnabled && probeCheck.IsChecked == false && !probeCheck.Focusable, "probe defaults off and cannot take focus");
        probeCheck.IsChecked = true; Pump();
        Check(!vm.SafeSamplingEnabled && vm.StatusMessage.Contains("请先连接"), "disconnected probe rejects without opening a native handle");

        // 原有命令的绑定必须还在（旧版把它们全挂在"维护"页上；重构后分散在不同页）⇒
        // 遍历四页收集窗口里真实绑定到的命令对象，再按名字逐条点名（缺哪条一眼看得出，不是笼统一句"binding lost"）。
        var bound = new HashSet<System.Windows.Input.ICommand>();
        foreach (var (section, pageName) in pageNames)
        {
            main.Navigation.Selected = section; Pump();
            // 扫**整窗**：除了当前页，顶部工具条（例如「一键全撤」）在页容器之外，只扫页内会漏掉。
            foreach (var button in Descendants(main).OfType<Button>())
                if (button.Command is not null) bound.Add(button.Command);
        }
        foreach (var name in new[] { nameof(vm.RefreshCommand), nameof(vm.LaunchAndLoadCommand), nameof(vm.InstallPatchesCommand),
            nameof(vm.RestorePatchesCommand), nameof(vm.RescanLaunchersCommand), nameof(vm.SaveLauncherSettingsCommand),
            nameof(vm.InspectHooksCommand), nameof(vm.ExportEvidenceCommand), nameof(vm.ExportProbeCommand),
            nameof(vm.OpenLogFolderCommand), nameof(vm.OpenEvidenceFolderCommand) })
        {
            var command = (System.Windows.Input.ICommand)typeof(MainViewModel).GetProperty(name)!.GetValue(vm)!;
            Check(bound.Contains(command), "original command is still bound somewhere in the console: " + name);
        }
        Untouched("console switches and command bindings");
    }

    private static void CheckReadOnlyProbeLifecycle()
    {
        var manifest = TrainerRuntimeAssets.LoadManifest();
        var resources = new SmokeSessionResources(new());
        var probeHandles = new List<SmokeProbeMemory>();
        using var vm = new MainViewModel(manifest,
            new TrainerAppSettingsStore(Path.Combine(AppContext.BaseDirectory, "probe-smoke-settings.json")),
            new SmokeLauncher(), new SmokeLocator(), _ => throw new Exception("No process-memory factory allowed"),
            _ => throw new Exception("No native allocator allowed"), new SmokeSuspender(), _ => [],
            _ => { var probe = new SmokeProbeMemory(); probeHandles.Add(probe); return probe; });
        // 日志文件是 append + FileShare.ReadWrite：先记住本次运行开始时的文件长度，
        // 否则历史会话里的 probe-start/stop/export 会让断言变成假阳性。
        var diagnostics = (Ra3Trainer.App.Diagnostics.TrainerDiagnostics)Get("_diagnostics")!;
        var logStartOffset = File.Exists(diagnostics.Log.FilePath) ? new FileInfo(diagnostics.Log.FilePath).Length : 0;
        Check(!vm.SafeSamplingEnabled, "new session sampling is always off");
        var session = new TrainerSession(manifest, resources,
            new TrainerTarget(manifest.TargetProcess, 0x400000, true, true), new SequentialRemoteAllocator(0x700000),
            processSuspender: new SmokeSuspender(), targetProcessState: resources);
        Check(session.Attach().Success, "sampling lifecycle uses only synthetic allocation, no game attachment");
        // Only HasExited uses this harness's own Process object. No process memory is opened.
        Set("_targetProcess", System.Diagnostics.Process.GetCurrentProcess());
        Set("_targetProcessId", Environment.ProcessId);
        Set("_session", session);
        Set("_connectionState", ConnectionState.Connected);
        vm.SafeSamplingEnabled = true;
        Check(vm.SafeSamplingEnabled && probeHandles.Count == 1, "explicit on opens only injected read-only fake");
        var timer = (DispatcherTimer)Get("_probeTimer")!;
        Check(timer.IsEnabled && timer.Interval == TimeSpan.FromMilliseconds(500), "independent sampler timer is 2 Hz, not render/frame driven");
        var deadline = Environment.TickCount64 + 8000;
        while (((ProbeLog)Get("_probeLog")!).SampleCount < 2 && Environment.TickCount64 < deadline)
        { Pump(); Thread.Sleep(10); }
        var log = (ProbeLog)Get("_probeLog")!;
        Check(log.SampleCount >= 2 && vm.ProbeSummary.Contains(log.SampleCount.ToString()), "timer appends samples and updates bound count");
        var reads = probeHandles[0].Reads;
        vm.SafeSamplingEnabled = false;
        Check(!timer.IsEnabled && probeHandles[0].Disposed, "off stops timer before disposing read handle");
        typeof(MainViewModel).GetMethod("OnProbeTick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, [null, EventArgs.Empty]);
        Check(probeHandles[0].Reads == reads, "queued tick after stop does not read");
        vm.ExportProbeCommand.Execute(null);
        Check(File.Exists(vm.LastEvidencePath), "export creates archive without opening windows");
        vm.SafeSamplingEnabled = true;
        Check(probeHandles.Count == 2, "re-enable uses a new read-only handle");
        Set("_connectionState", ConnectionState.Disconnected);
        typeof(MainViewModel).GetMethod("OnProbeTick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, [null, EventArgs.Empty]);
        Check(!vm.SafeSamplingEnabled && probeHandles[1].Disposed, "disconnect stops sampling and retires handle");
        using var logStream = new FileStream(diagnostics.Log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var logReader = new StreamReader(logStream);
        // 只看本次运行追加的尾部，历史日志不算数。
        if (logStream.Length >= logStartOffset) logStream.Seek(logStartOffset, SeekOrigin.Begin);
        var entries = logReader.ReadToEnd();
        Check(entries.Contains("probe-start") && entries.Contains("probe-stop") && entries.Contains("probe-export"), "on/off/export retained in session log");
        // No native restore is possible: session/resources have only fake memory and no process id.
        void Set(string name, object value) => typeof(MainViewModel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, value);
        object? Get(string name) => typeof(MainViewModel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm);
    }

    private static void CheckConsolePages(Application app, MainViewModel vm, FloatingWindow floating)
    {
        // Render real catalog data with both responsibility switches left off; no game operation.
        vm.SelectedReinforcementUnit = vm.ReinforcementUnits.First(unit => unit.Code == 0x139CBC97);
        vm.ReinforcementCountText = "5"; vm.ReinforcementRankText = "3";
        vm.ReinforcementPresets.Clear();
        vm.ReinforcementPresets.Add(new ReinforcementPreset("维和步兵 × 5 / 3星", 0x139CBC97, 5, 3));
        vm.ReinforcementPresets.Add(new ReinforcementPreset("维和步兵 × 10 / 0星", 0x139CBC97, 10, 0));
        vm.SelectedReinforcementPreset = vm.ReinforcementPresets[0];
        vm.PresetNameText = vm.SelectedReinforcementPreset.Name;
        // 队列已从 UI 撤掉（MainViewModel 里写着 "Retained queue API; the UI intentionally has no automatic queue button"），
        // 原来这里先 ClearReinforcementQueueCommand 再 AddSelectedPresetToQueueCommand —— 两个命令在产品里都不存在了。
        var shared = typeof(App).GetField("_sharedViewModel", BindingFlags.NonPublic | BindingFlags.Static)!;
        shared.SetValue(null, vm);
        var main = new MainWindow { Left = -30000, Top = -30000, ShowActivated = false };
        var mainActivated = 0;
        main.Activated += (_, _) => mainActivated++;
        main.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(main).Handle).AddHook(KeepOffscreen);
        main.Show(); Pump();
        try
        {
            Check(vm.Groups.Sum(group => group.Features.Count) == 31, "all 31 original UI features retained");
            Check(!vm.AllowNonStandardVersion && !vm.AllowUnverifiedWorldPositionFeatures, "redesign does not opt into either safety override");
            var windows = ThreadWindows();
            var oldCount = vm.ReinforcementCountText;
            var oldRank = vm.ReinforcementRankText;
            var oldCode = vm.ReinforcementUnitIdText;
            var oldPreset = vm.SelectedReinforcementPreset;
            // ---- 主窗：四页（启动/增援/功能/诊断）----
            // 旧循环把主窗和浮窗一起遍历，并假设两边都有 Navigation 和四个同名页 —— 重构后不成立：
            // 浮窗只剩"面板渲染器"（页签是游戏内动词、没有 NavigationRail、没有页 ScrollViewer），
            // 所以主窗和面板分开检查，下面一段才是面板。
            var mainPages = new[] { ("启动", "StartPage"), ("增援", "ReinforcementPage"), ("功能", "FeaturesPage"), ("诊断", "DiagnosticsPage") };
            foreach (var (section, pageName) in mainPages)
            {
                Invoke((Button)main.FindName("Nav" + section)); Pump();
                Check(main.Navigation.Selected == section, "main navigation command " + section);
                Check(main.ActualWidth == 840 && main.ActualHeight == 530, "main exact production size " + section);
                // 左边那条 112px 的竖导航栏已经改版成**顶部横向标签条**（4 等分列，DockPanel.Dock=Top），
                // 所以旧断言 ActualWidth == 112 已无意义 ⇒ 钉住新形态 + 每个页签的参数与页名一致。
                var rail = (FrameworkElement)main.FindName("NavigationRail");
                Check(rail.ActualWidth >= 700 && rail.ActualHeight > 0,
                    $"main navigation is the top tab bar: {rail.ActualWidth:F0}x{rail.ActualHeight:F0}");
                foreach (var (tab, _) in mainPages)
                {
                    var tabButton = (Button)main.FindName("Nav" + tab);
                    Check(Equals(tabButton.CommandParameter, tab),
                        "main tab " + tab + " command parameter matches its section: " + tabButton.CommandParameter);
                }
                var page = (ScrollViewer)main.FindName(pageName);
                var pages = mainPages.Select(item => (ScrollViewer)main.FindName(item.Item2)).ToArray();
                Check(pages.Count(item => item.IsVisible) == 1 && page.IsVisible, "main exactly one page visible " + section);
                page.ScrollToTop(); Pump();
                CheckBounds(page, main, "main " + section + " viewport");
                if (section == "增援")
                    foreach (var name in new[] { "CurrentUnitName", "ChooseUnitButton", "ReadGameUnitButton", "CountValue", "RankValue", "CallReinforcementButton" })
                    {
                        var control = (FrameworkElement)main.FindName(name);
                        var point = control.TranslatePoint(new Point(), page);
                        // 这一页现在比视口高（常驻的选兵两列各 300 DIP）⇒ 首屏之外是正常的滚动内容，
                        // 不是"被裁掉"。所以这里只钉**横向不越界 + 有真实尺寸**；
                        // 纵向可达性由下面"逐个按钮滚进视口再 CheckBounds"那段统一证明（同一批控件都在其中）。
                        Check(control.ActualWidth > 0 && control.ActualHeight > 0
                            && point.X >= -0.5 && point.X + control.ActualWidth <= page.ActualWidth + 0.5,
                            "main reinforcement control laid out inside the page width: " + name);
                    }
                if (section == "功能")
                {
                    var originals = vm.Groups.SelectMany(group => group.Features).ToArray();
                    var featureButtons = Descendants(page).OfType<Button>().Where(button => originals.Any(feature => ReferenceEquals(feature.Command, button.Command))).ToArray();
                    Check(featureButtons.Length == 31, "main all 31 feature commands realized");
                    foreach (var feature in originals)
                    {
                        var row = featureButtons.Single(button => ReferenceEquals(button.Command, feature.Command));
                        var rowRoot = VisualTreeHelper.GetParent(row);
                        var texts = Descendants(rowRoot).OfType<TextBlock>().ToArray();
                        Check(texts.Any(text => text.Text == feature.DisplayName) && texts.Any(text => text.Text == feature.Hotkey), "main original feature name and hotkey " + feature.DisplayName);
                        if (feature.RiskLabel.Length > 0) Check(texts.Any(text => text.Text == feature.RiskLabel && text.Visibility == Visibility.Visible), "main persistent risk label " + feature.DisplayName);
                    }
                }
                RenderConsole(main, "main-" + section, 840, 530);
                foreach (var control in Descendants(page).OfType<FrameworkElement>().Where(item => item.IsVisible && item is Button).ToArray())
                {
                    control.BringIntoView(); Pump();
                    // Nested lists consume RequestBringIntoView. Also scroll the outer page as a user can.
                    var point = control.TranslatePoint(new Point(), page);
                    if (point.Y + control.ActualHeight > page.ViewportHeight)
                        page.ScrollToVerticalOffset(page.VerticalOffset + point.Y + control.ActualHeight - page.ViewportHeight);
                    else if (point.Y < 0) page.ScrollToVerticalOffset(page.VerticalOffset + point.Y);
                    Pump();
                    CheckBounds(control, page, "main " + section + " " + (control as Button)?.Content);
                }
                page.ScrollToVerticalOffset(Math.Min(70, page.ScrollableHeight)); Pump();
                var offset = page.VerticalOffset;
                // 「维护」页已改名「诊断」：切走再切回，页内滚动位置必须还在。
                main.Navigation.Selected = section == "增援" ? "诊断" : "增援"; Pump();
                main.Navigation.Selected = section; Pump();
                Check(Math.Abs(page.VerticalOffset - offset) < 0.1, "main restores page scroll offset " + section);
                Check(windows.SetEquals(ThreadWindows()), "main navigation creates no additional HWND " + section);
                Check(vm.ReinforcementCountText == oldCount && vm.ReinforcementRankText == oldRank && vm.ReinforcementUnitIdText == oldCode && ReferenceEquals(vm.SelectedReinforcementPreset, oldPreset), "main all shared configuration survives navigation " + section);
                Check(!Descendants(main).Any(item => item is Popup or ComboBox), "main no popup/dropdown on any page " + section);
            }
            Check(mainActivated == 0, "main offscreen render Activated == 0");

            // 预设列表：旧断言挂在**浮窗**的预设列表上（那个列表已随桌面浮窗删除），现在只剩主窗这一份。
            main.Navigation.Selected = "增援"; Pump();
            var presetHost = (ScrollViewer)main.FindName("ReinforcementPage");
            var presetList = (ItemsControl)main.FindName("PresetSelector");
            Check(presetList.Items.Count == vm.ReinforcementPresets.Count && presetList.Items.Count > 0,
                "main preset list mirrors the shared collection: " + presetList.Items.Count);
            var presetButton = Descendants(presetList).OfType<Button>().First(button => button.CommandParameter is ReinforcementPreset);
            var preset = (ReinforcementPreset)presetButton.CommandParameter;
            Invoke(presetButton); Pump();
            Check(vm.ReinforcementCountText == preset.Count.ToString() && vm.ReinforcementRankText == preset.Rank.ToString()
                && vm.ReinforcementUnitIdText == $"0x{preset.UnitId:X8}", "main one preset click applies all three settings");
            Check(presetList.ActualHeight <= 180, "main preset list is bounded: " + presetList.ActualHeight);
            Check(presetHost.ExtentHeight > presetHost.ViewportHeight, "main preset list belongs to outer scroll extent");
            vm.ReinforcementPresets.Clear();
            // 绑定/触发器要一帧才结算：有界等到稳定再断言（早一帧读到的还是上一批）。
            for (var guard = 0; guard < 20 && presetList.Items.Count != 0; guard++) { main.UpdateLayout(); Pump(); }
            var emptyState = (TextBlock)main.FindName("PresetEmptyState");
            for (var guard = 0; guard < 20 && !emptyState.IsVisible; guard++) { main.UpdateLayout(); Pump(); }
            Check(presetList.Items.Count == 0 && emptyState.IsVisible,
                "empty preset list swaps in the save guidance (count=" + presetList.Items.Count + ")");
            // 旧断言这里还有一句 "vm.SelectedReinforcementPreset is null"，是当年和已删除的队列按钮
            // CanExecute 一起写的。产品里没有任何路径会清空预设集合（只能一条条存），选中项也不随集合清空而清
            // ⇒ 断言一个够不到的状态没有意义。换成钉住真正的不变量：选中项必须始终是列表里的一员（不许悬空）。
            vm.ReinforcementPresets.Add(new ReinforcementPreset("维和步兵 × 5 / 3星", 0x139CBC97, 5, 3));
            vm.ReinforcementPresets.Add(new ReinforcementPreset("维和步兵 × 10 / 0星", 0x139CBC97, 10, 0));
            vm.SelectedReinforcementPreset = vm.ReinforcementPresets[0]; Pump();
            Check(vm.ReinforcementPresets.Contains(vm.SelectedReinforcementPreset!),
                "selected preset always belongs to the live collection (no dangling selection)");
            Check(!emptyState.IsVisible, "save guidance disappears again once a preset exists");

            // 选兵区（旧 CheckReinforcementEditor）：选兵界面现在只在主窗「增援」页。
            var reinforcementScroll = (ScrollViewer)main.FindName("ReinforcementPage");
            var consoleForeground = GetForegroundWindow();
            var consoleFocus = GetFocus();
            CheckUnitChooser(vm, main, reinforcementScroll, () =>
                GetForegroundWindow() == consoleForeground && GetFocus() == consoleFocus && !main.IsActive && mainActivated == 0);

            // ---- 面板：页签（游戏内动词）+ 行板 ----
            Check(floating.ActualWidth == 360 && floating.ActualHeight == 440, "panel exact production size");
            Check(floating.FindName("NavigationRail") is null, "panel has no console navigation rail");
            var board = (ScrollViewer)floating.FindName("PanelBoardScroll");
            foreach (var (tab, slug) in new[] { ("功能", "function"), ("增援", "reinforcement"), ("选单位", "units") })
            {
                // 配置快照必须**紧贴这次切换之前**取：选兵段是故意改过数量/星级/兵种的，
                // 拿 CheckConsolePages 开头那份旧快照来比，比的是"这段时间没改过配置"，与页签无关。
                var beforeTab = (vm.ReinforcementCountText, vm.ReinforcementRankText, vm.ReinforcementUnitIdText, vm.SelectedReinforcementUnit);
                Invoke((Button)floating.FindName("Nav" + tab)); Pump();
                Check(vm.PanelPage == tab, "panel tab command " + tab + ": " + vm.PanelPage);
                Check(floating.ActualWidth == 360 && floating.ActualHeight == 440, "panel exact size on " + tab);
                Check(vm.PanelRows.Count > 0, "panel page " + tab + " exposes rows: " + vm.PanelRows.Count);
                RenderConsole(floating, "panel-" + slug, 360, 440);
                Check(windows.SetEquals(ThreadWindows()), "panel tab " + tab + " creates no additional HWND");
                Check(vm.ReinforcementCountText == beforeTab.Item1 && vm.ReinforcementRankText == beforeTab.Item2
                    && vm.ReinforcementUnitIdText == beforeTab.Item3 && ReferenceEquals(vm.SelectedReinforcementUnit, beforeTab.Item4),
                    "panel tab " + tab + " does not disturb the shared configuration");
                Check(!Descendants(floating).Any(item => item is Popup or ComboBox or TextBox), "panel never shows a popup/dropdown/TextBox");
                Check(Descendants(floating).OfType<Button>().All(button => !button.Focusable), "all panel buttons refuse keyboard focus");
            }
            var codeBeforePanelSwitch = vm.ReinforcementUnitIdText;
            vm.SelectPanelPage("增援"); Pump();
            Check(vm.PanelPage == "增援" && vm.OverlayPanelSection == "增援",
                "panel page state is a single shared value: " + vm.PanelPage);
            Check(vm.ReinforcementUnitIdText == codeBeforePanelSwitch, "panel page switch keeps the unit code");
            // 行板要真的能滚 —— 但只有**长页**才溢出：功能页 31 项，增援页一屏放得下（本来就不该滚）。
            // 所以这条测量必须落在功能页上，并等布局结算（换页后容器/可滚范围是下一帧才更新的）。
            vm.SelectPanelPage("功能"); Pump();
            var scrollable = 0.0;
            for (var guard = 0; guard < 20 && scrollable <= 0; guard++)
            {
                floating.UpdateLayout(); board?.UpdateLayout(); Pump();
                scrollable = board?.ScrollableHeight ?? 0;
            }
            Check(board is not null && scrollable > 0, "panel board actually scrolls the long feature page: " + scrollable);
            vm.SelectPanelPage("增援"); Pump();
            Check(board!.ScrollableHeight == 0, "short reinforcement page needs no scrolling");
            CheckPanelRowsAreMouseReachable(vm, floating, board!, GetForegroundWindow(), GetFocus(), () => 0);
            CheckConsoleMouseOnly(vm, main);
            var picker = new ReinforcementUnitPickerWindow
            {
                Owner = main,
                DataContext = new ReinforcementUnitPickerViewModel(vm.ReinforcementUnits.ToArray()),
                ShowActivated = false, Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual
            };
            var pickerActivated = 0;
            picker.Activated += (_, _) => pickerActivated++;
            picker.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(picker).Handle).AddHook(KeepOffscreen);
            picker.Show(); Pump();
            RenderConsole(picker, "picker", 640, 430);
            Check(picker.ActualWidth == 640 && picker.ActualHeight == 430 && pickerActivated == 0, "picker exact size and offscreen no activation");
            foreach (var control in Descendants(picker).OfType<Button>().Where(item => item.IsVisible)) CheckBounds(control, picker, "picker button");
            picker.Close();
            foreach (var confirm in new[] { false, true })
            {
                var pickerVm = new ReinforcementUnitPickerViewModel(vm.ReinforcementUnits.ToArray());
                pickerVm.SelectedUnit = pickerVm.Units.First();
                var dialog = new ReinforcementUnitPickerWindow { Owner = main, DataContext = pickerVm, ShowActivated = false };
                dialog.SourceInitialized += (_, _) =>
                {
                    var handle = new WindowInteropHelper(dialog).Handle;
                    HwndSource.FromHwnd(handle).AddHook(KeepOffscreen);
                    SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000080);
                };
                dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(() =>
                {
                    Check(!main.IsVisible && dialog.IsVisible, "picker temporarily hides main while open");
                    if (confirm) pickerVm.ConfirmCommand.Execute(null);
                    else pickerVm.CancelCommand.Execute(null);
                });
                var result = dialog.ShowWithOwnerHidden(); Pump();
                Check(result == confirm && main.IsVisible, "picker confirm/cancel restores main");
                Check(main.ActualWidth == 840 && main.ActualHeight == 530 && main.Navigation.Selected == "诊断", "restored main preserves size and section");
            }
            // 旧代码在这里把 vm.SelectedFloatWindowForm 在 StatusLine(发丝条 200×19) / FullBar(全条) 之间切，
            // 再断言 200×19 的细条。桌面浮窗形态已删除：面板只有一种形态、恒为 360×440 ⇒ 整段删除。
            // 「面板不会因为开关形态而变矮/变空」由上面 CheckPanelRowsAreMouseReachable 的尺寸断言接管。
        }
        finally { main.Close(); shared.SetValue(null, null); }
    }

    private static void CheckBounds(FrameworkElement element, FrameworkElement viewport, string description)
    {
        var point = element.TranslatePoint(new Point(), viewport);
        Check(element.ActualWidth > 0 && element.ActualHeight > 0 && point.X >= -0.5 && point.Y >= -0.5 &&
            point.X + element.ActualWidth <= viewport.ActualWidth + 0.5 && point.Y + element.ActualHeight <= viewport.ActualHeight + 0.5,
            $"{description} not clipped: ({point.X:F1},{point.Y:F1}) {element.ActualWidth:F1}x{element.ActualHeight:F1} in {viewport.ActualWidth:F1}x{viewport.ActualHeight:F1}");
    }

    private static void RenderConsole(Window window, string state, int width, int height)
    {
        Pump();
        GetWindowRect(new WindowInteropHelper(window).Handle, out var bounds);
        Check(bounds.Right < 0 && bounds.Bottom < 0 && bounds.Right - bounds.Left == width && bounds.Bottom - bounds.Top == height,
            "render actual offscreen native rectangle " + state);
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(@"D:\work\mock\render", "round13-C-" + state + ".png");
        // 与 RenderPresetEvidence/RenderUnitEvidence 一致：目录不存在时先建，否则 File.Create 抛
        // DirectoryNotFoundException，会把与 UI 无关的失败报成烟雾测试失败。
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var output = File.Create(path)) encoder.Save(output);
        Console.WriteLine($"PNG={path}; {bitmap.PixelWidth}x{bitmap.PixelHeight}; HWND={bounds.Right-bounds.Left}x{bounds.Bottom-bounds.Top}");
    }

    // 旧 RenderPresetEvidence（拍"浮窗预设列表 + 长名字换行"）已删除：那个列表随桌面浮窗一起没了。
    // 现在拍行板的是 RenderPanelEvidence，拍选兵区的是 RenderUnitEvidence（都在主窗/面板上）。

    /// <summary>
    /// 选兵区检查。旧名 CheckReinforcementEditor，挂在**浮窗**上；选兵界面现在只在主窗「增援」页
    /// （浮窗只剩"面板渲染器"，游戏内选兵走面板「选单位」页签）⇒ 窗口类型改成 MainWindow。
    /// 另一处产品级变化：选兵区**常驻**了（2026-10-07「分类左边固定住，没必要点一下单位就消失」）——
    /// IsChoosing 那个"永远为 true 的死状态"被删，「其他分类」按钮也一起删 ⇒
    /// 旧断言里"两级选择器展开/收起""兵种代号标签"那几条都按新 UI 改写（见各条注释）。
    /// </summary>
    private static void CheckUnitChooser(MainViewModel vm, MainWindow window, ScrollViewer scroll, Func<bool> untouched)
    {
        var editor = window.ReinforcementEditor;
        var originalWindows = ThreadWindows();
        T Find<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Click(string name)
        {
            // 数量/星级那两个是 RepeatButton（ButtonBase），不是 Button —— 按基类取，别按具体类型硬转。
            var button = Find<ButtonBase>(name);
            Check(!button.Focusable && button.IsEnabled, name + " enabled without keyboard focus");
            Invoke(button); Pump();
            Check(untouched(), name + " preserves activation/foreground/focus");
            Check(originalWindows.SetEquals(ThreadWindows()), name + " creates no extra HWND");
            Check(!Descendants(window).Any(item => item is Popup or ComboBox), name + " introduces no popup/dropdown");
        }
        void Row(ListBox list, int index)
        {
            // 列表有虚拟化：容器/行内按钮是**懒生成**的，早一帧取会是 null 或空容器
            // （旧写法直接 Single() ⇒ "Sequence contains no matching element"）。所以先滚进视图再取，带界重试。
            var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
            var button = item is null ? null : Descendants(item).OfType<Button>().FirstOrDefault();
            for (var guard = 0; guard < 20 && button is null; guard++)
            {
                list.ScrollIntoView(list.Items[index]); Pump();
                item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
                button = item is null ? null : Descendants(item).OfType<Button>().FirstOrDefault();
            }
            Check(item is not null && button is not null, $"unit row {index} realized its click button");
            list.BringIntoView(); Pump(); button!.BringIntoView(); Pump();
            var point = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), scroll);
            Check(IsWithin(scroll.InputHitTest(point) as DependencyObject, button), "inline row reachable by pointer");
            Check(!button.Focusable && InlineSelection.GetSelectOnClick(button), "inline unit row uses the nonfocusable SelectOnClick button");
            Invoke(button); Pump();
            Check(untouched(), "inline selection preserves activation/foreground/focus");
            Check(originalWindows.SetEquals(ThreadWindows()), "inline selection creates no extra HWND");
        }
        // 分类行是**普通 ListBoxItem**（靠 ListBox 自身的选中 → SelectedItem 双向绑到 editor.SelectedCategory），
        // 没有 InlineSelection 那个附加行为 —— 旧断言（要求 SelectOnClick 按钮）只对**兵种行**成立。
        // 这里要证的还是同一件事：这一行在视口内可被指针点到、并且不带键盘焦点。
        void CategoryRow(ListBox list, int index)
        {
            var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
            list.BringIntoView(); Pump(); item.BringIntoView(); Pump();
            var point = item.TranslatePoint(new Point(item.ActualWidth / 2, item.ActualHeight / 2), scroll);
            Check(IsWithin(scroll.InputHitTest(point) as DependencyObject, item), "category row reachable by pointer");
            // 这里**故意不**断言"item 不可获焦"：主窗是普通桌面窗口，键盘焦点在这里是正常的
            // （"绝不可获焦"是**面板**的硬要求，已由 CheckPanelRowsAreMouseReachable 那边钉住）。
            // 这一行真正要证的是：点得到、选中真的走到列表自身的选中（进而双向写回 VM）、且不抢前景/焦点。
            list.SelectedIndex = index; Pump();
            Check(item.IsSelected, "category selection goes through the list's own selection");
            Check(untouched(), "category selection preserves activation/foreground/focus");
            Check(originalWindows.SetEquals(ThreadWindows()), "category selection creates no extra HWND");
        }
        var heading = Find<TextBlock>("UnitSelectorHeading");
        var nameLabel = Find<TextBlock>("CurrentUnitName");
        Check(heading.IsVisible && nameLabel.IsVisible && nameLabel.Text == vm.SelectedReinforcementUnit!.Name,
            "default expanded page shows send-unit heading and actual current name");
        // 上一段的可达性遍历把这一页滚过（会滚到底部去检查每个按钮）⇒ 先回页首，
        // 这条断言才真的是在说"默认首屏"，而不是"我恰好滚到哪就量哪"。
        scroll.ScrollToTop(); window.UpdateLayout(); Pump();
        var defaultPoint = nameLabel.TranslatePoint(new Point(), scroll);
        Check(defaultPoint.Y >= 0 && defaultPoint.Y + nameLabel.ActualHeight <= scroll.ViewportHeight,
            "current unit is inside default viewport without scrolling");
        var units = Find<ListBox>("UnitSelector");
        // 旧断言是 Find<TextBlock>("CurrentUnitCode").Text == vm.ReinforcementUnitIdText。
        // 「兵种代号」这个标签已从"兵种"一行删掉（那一行现在只印名字，代号印在每个兵种行的右边）⇒
        // 改成断言"代号确实印在兵种行里"，比断言一个不存在的控件诚实。
        Check(Descendants(units).OfType<TextBlock>().Any(text => text.Text == vm.ReinforcementUnitIdText),
            "current code is printed on the unit rows: " + vm.ReinforcementUnitIdText);
        // 旧断言：!editor.IsChoosing && !UnitSelectionPanel.IsVisible（"两级选择器初始收起"）。
        // 常驻之后两列一直都在：IsChoosing 是死状态、已删除，面板自己也没有 Visibility 开关。
        Check(Find<StackPanel>("UnitSelectionPanel").IsVisible && editor.HasCategory,
            "unit selection columns are always present (IsChoosing dead state removed)");
        Check(ReferenceEquals(Find<Button>("ReadGameUnitButton").Command, vm.ReadSelectedUnitCodeCommand), "reuse game-selected-unit command");
        // 这条断言的本意：呼叫增援按钮走的就是"那条 J 动作"（读当前设置），不是另一套逻辑。
        // 旧写法直接与 vm.Groups 里现取的 feature.Command 比引用。若 vm.Groups 被重建过，
        // 现取到的是**新实例**，而按钮/编辑器握着的是构造时那份 ⇒ 引用比较会假失败或假通过。
        // 所以拆成两段可分辨的事实，并在消息里带出三方的真实类型，一次就能看出断在哪。
        var callFeature = vm.Groups.SelectMany(group => group.Features).Single(feature => feature.Feature.RawName == "We Need Back");
        var callButton = Find<Button>("CallReinforcementButton");
        Check(callButton.Command is not null && ReferenceEquals(callButton.Command, editor.CallCommand),
            "call button is bound to the editor's call command: button=" + (callButton.Command?.GetType().Name ?? "<null>")
            + ", editor=" + (editor.CallCommand?.GetType().Name ?? "<null>"));
        Check(ReferenceEquals(editor.CallCommand, callFeature.Command),
            "editor call command is the J feature command: editor=" + (editor.CallCommand?.GetType().Name ?? "<null>")
            + ", feature=" + (callFeature.Command?.GetType().Name ?? "<null>"));
        Check(!Find<Button>("CallReinforcementButton").IsEnabled && !Find<Button>("ReadGameUnitButton").IsEnabled,
            "disconnected game actions retain production command guards");
        var categories = Find<ListBox>("CategorySelector");
        Click("ChooseUnitButton");
        // 「选择单位」现在 = 把左列跳回**当前兵种所在的分类**（不是展开/收起）：分类列一直都在。
        Check(editor.SelectedCategory == vm.SelectedReinforcementUnit?.Category,
            "C jumps the category column to the current unit's category");
        Check(categories.IsVisible && categories.Items.Count > 1 &&
            categories.Items.Cast<string>().SequenceEqual(vm.ReinforcementUnits.Select(unit => unit.Category).Distinct()),
            "category column matches entire live catalog");
        Check(units.IsVisible, "unit column is present from the start now that both columns are permanent");
        RenderUnitEvidence(window, scroll, "categories");
        CategoryRow(categories, 0);
        Check(units.Items.Count > 0 && units.Items.Cast<ReinforcementUnitEntry>().All(unit => unit.Category == editor.SelectedCategory),
            "category click reveals only its units");
        Check(units.ActualHeight > 0 && units.ActualHeight <= 300 && Descendants(units).OfType<ScrollViewer>().Single().ScrollableHeight > 0,
            "long unit list scrolls internally within its 300 DIP ceiling");
        var unitScroll = Descendants(units).OfType<ScrollViewer>().Single();
        unitScroll.ScrollToTop(); Pump();
        Invoke(Descendants(window).OfType<Button>().Single(button => InlineListPaging.GetTarget(button) == "UnitSelector:down")); Pump();
        Check(unitScroll.VerticalOffset > 0, "mouse next-page button advances unit list");
        Invoke(Descendants(window).OfType<Button>().Single(button => InlineListPaging.GetTarget(button) == "UnitSelector:up")); Pump();
        Check(unitScroll.VerticalOffset == 0, "mouse previous-page button restores unit list");
        var oldCode = vm.ReinforcementUnitIdText;
        CategoryRow(categories, 1);
        Check(vm.ReinforcementUnitIdText == oldCode, "browsing another category cannot clear shared unit");
        CategoryRow(categories, 0);
        var chosen = (ReinforcementUnitEntry)units.Items[1];
        Row(units, 1);
        Check(ReferenceEquals(vm.SelectedReinforcementUnit, chosen) && vm.ReinforcementUnitIdText == chosen.CodeText,
            "unit click writes selected entry and formatted code to shared VM");
        Check(nameLabel.Text == chosen.Name, "current row follows picked unit");
        var selectedRow = (ListBoxItem)units.ItemContainerGenerator.ContainerFromItem(chosen);
        Check(selectedRow is not null && selectedRow.IsSelected, "current unit stays highlighted in the list");
        RenderUnitEvidence(window, scroll, "units-selected");
        Row(units, 1);
        CategoryRow(categories, categories.Items.Count - 1);
        Check(units.Items.Count > 0, "last category remains reachable through inner scrolling");
        var lastUnit = (ReinforcementUnitEntry)units.Items[units.Items.Count - 1];
        Row(units, units.Items.Count - 1);
        Check(vm.ReinforcementUnitIdText == lastUnit.CodeText,
            "last unit remains reachable and commits through inner and outer scrolling");
        // External preset/read-selection changes and live mod categories must stay synchronized.
        var external = vm.ReinforcementUnits.First(unit => unit.Category != chosen.Category);
        vm.SelectedReinforcementUnit = external; Pump();
        Check(nameLabel.Text == external.Name, "external selection updates current row");
        var added = new ReinforcementUnitEntry(0x1234ABCD, "冒烟临时单位", "冒烟临时阵营");
        vm.ReinforcementUnits.Add(added); Pump();
        Check(editor.Categories.Contains(added.Category), "new game/mod category appears without reopening window");
        vm.ReinforcementUnits.Remove(added); Pump();
        Check(!editor.Categories.Contains(added.Category), "removed category cannot remain stale");
        // 旧循环把"非法/超范围输入"也当"能按"来测（Click(button) 后断言被悄悄校正成别的数）。
        // 产品后来**故意改掉**了这一点：CanAdjust 对非法/超范围一律不放行 ⇒ 按钮直接禁用。
        // 所以按"该不该能按"分三类钉住 ——（a）边界：禁用，且直接执行命令仍保持边界值；
        // （b）中间值：可点，点一下 ±1；（c）非法值：禁用，而"直接执行命令"这条兜底（快捷键那条路）
        // 仍按文档默认值校正。两个语义都要真，不能只留一个。
        var fallbackCountDown = (ReinforcementSettings.DefaultCount - 1).ToString();
        var fallbackCountUp = (ReinforcementSettings.DefaultCount + 1).ToString();
        foreach (var (value, button, clickable, expected) in new[] { ("1", "DecreaseCountButton", false, "1"), ("50", "IncreaseCountButton", false, "50"),
            ("1", "IncreaseCountButton", true, "2"), ("50", "DecreaseCountButton", true, "49"),
            ("0", "DecreaseCountButton", false, fallbackCountDown), ("51", "IncreaseCountButton", false, fallbackCountUp),
            ("bad", "IncreaseCountButton", false, fallbackCountUp) })
        {
            vm.ReinforcementCountText = value; Pump();
            var control = Find<ButtonBase>(button);
            if (clickable)
            {
                Check(control.IsEnabled && control.Command.CanExecute(null), $"{button} clickable at count {value}");
                Click(button);
            }
            else
            {
                Check(!control.IsEnabled && !control.Command.CanExecute(null), $"{button} disabled at count {value}");
                control.Command.Execute(null); // Defensive clamping still holds for a direct caller.
                Pump();
            }
            Check(vm.ReinforcementCountText == expected && Find<TextBox>("CountValue").Text == expected,
                $"count {value} via {button} -> {expected}");
            Check(!string.IsNullOrWhiteSpace(editor.Feedback), "count has readable feedback");
        }
        var fallbackRankDown = (ReinforcementSettings.DefaultRank - 1).ToString();
        var fallbackRankUp = ReinforcementSettings.DefaultRank.ToString();   // 3+1 被夹回上限 3
        foreach (var (value, button, clickable, expected) in new[] { ("0", "DecreaseRankButton", false, "0"), ("3", "IncreaseRankButton", false, "3"),
            ("0", "IncreaseRankButton", true, "1"), ("3", "DecreaseRankButton", true, "2"),
            ("-1", "DecreaseRankButton", false, fallbackRankDown), ("4", "IncreaseRankButton", false, fallbackRankUp),
            ("bad", "IncreaseRankButton", false, fallbackRankUp) })
        {
            vm.ReinforcementRankText = value; Pump();
            var control = Find<ButtonBase>(button);
            if (clickable)
            {
                Check(control.IsEnabled && control.Command.CanExecute(null), $"{button} clickable at rank {value}");
                Click(button);
            }
            else
            {
                Check(!control.IsEnabled && !control.Command.CanExecute(null), $"{button} disabled at rank {value}");
                control.Command.Execute(null);
                Pump();
            }
            Check(vm.ReinforcementRankText == expected && Find<TextBlock>("RankValue").Text == expected,
                $"rank {value} via {button} clamps and updates display to {expected}");
            Check(!string.IsNullOrWhiteSpace(editor.Feedback), "rank has readable feedback");
        }
        vm.ReinforcementCountText = "2"; vm.ReinforcementRankText = "1";
        Check(untouched(), "all new editor operations Activated == 0");
    }

    /// <summary>选兵证据图：窗口从浮窗改成主窗（选兵界面现在只在主窗「增援」页）。</summary>
    private static void RenderUnitEvidence(Window window, ScrollViewer scroll, string state)
    {
        var heading = (TextBlock)window.FindName("UnitSelectorHeading");
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + heading.TranslatePoint(new Point(), scroll).Y - 16);
        Pump();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),
            (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(@"D:\work\mock\render", $"main-select-{state}-{RuntimeInformation.ProcessArchitecture}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var output = File.Create(path)) encoder.Save(output);
        GetWindowRect(new WindowInteropHelper(window).Handle, out var bounds);
        Check(bounds.Right < 0 && bounds.Bottom < 0 && bitmap.PixelWidth > 0, "round11 evidence uses real offscreen window");
        Console.WriteLine("PNG=" + path);
    }

    private static IntPtr KeepOffscreen(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0046) // WM_WINDOWPOSCHANGING; WINDOWPOS starts with two HWND fields.
        {
            Marshal.WriteInt32(lParam, IntPtr.Size * 2, -30000);
            Marshal.WriteInt32(lParam, IntPtr.Size * 2 + 4, -30000);
        }
        return IntPtr.Zero;
    }

    private static HashSet<IntPtr> ThreadWindows()
    {
        var windows = new HashSet<IntPtr>();
        EnumThreadWindows(GetCurrentThreadId(), (hwnd, _) => { windows.Add(hwnd); return true; }, IntPtr.Zero);
        return windows;
    }
    private delegate bool EnumWindowCallback(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumWindowCallback callback, IntPtr parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out WindowRect bounds);

    private static void CheckLaunchWindowTransitions(Application app, MainViewModel vm, FloatingWindow floating,
        TrainerAppSettingsStore store, IntPtr foreground, IntPtr focus, SmokeLauncher launcher, LauncherCandidate candidate)
    {
        var shared = typeof(App).GetField("_sharedViewModel", BindingFlags.NonPublic | BindingFlags.Static)!;
        shared.SetValue(null, vm);
        var main = new MainWindow { ShowActivated = false, Left = -30000, Top = -30000 };
        var activations = 0;
        main.Activated += (_, _) => activations++;
        var requests = (Action<LaunchWindowMode>)typeof(MainViewModel)
            .GetField("LaunchWindowModeRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        try
        {
            requests(LaunchWindowMode.Main); Pump();
            Check(main.IsVisible, "launch workflow exposes main without activation");
            var floatShows = 0;
            floating.IsVisibleChanged += (_, _) => floatShows++;
            LaunchWindowCoordinator.RunAsync(() => Task.FromResult(true), () => vm.AutoFloatAfterLaunch, requests)
                .GetAwaiter().GetResult(); Pump();
            Check(!main.IsVisible && floating.IsVisible, "successful launch enters floating mode");
            requests(LaunchWindowMode.Floating); Pump();
            Check(floatShows == 0 && app.Windows.OfType<FloatingWindow>().Count() == 1,
                "visible float is reused without Show or duplication");
            vm.AutoFloatAfterLaunch = false;
            Check(!store.Load().AutoFloatAfterLaunch, "VM setting persists false");
            LaunchWindowCoordinator.RunAsync(() => Task.FromResult(true), () => vm.AutoFloatAfterLaunch, requests)
                .GetAwaiter().GetResult(); Pump();
            Check(main.IsVisible, "disabled setting keeps main on success");
            vm.AutoFloatAfterLaunch = true;
            Check(store.Load().AutoFloatAfterLaunch, "VM setting persists true");
            requests(LaunchWindowMode.Floating);
            WaitUntilIdle(vm);
            vm.LauncherPath = string.Empty;
            Check(vm.LauncherPath == candidate.Path, "regression: clearing path alone is repopulated by selected launcher");
            vm.LaunchAndLoadCommand.Execute(null);
            WaitUntilIdle(vm);
            Check(main.IsVisible && !vm.IsBusy &&
                vm.StatusMessage == SmokeLauncher.Failure && launcher.Calls.Count == 1,
                "fake launcher failure through real command restores main and exact Chinese error");
            requests(LaunchWindowMode.Floating);
            vm.LauncherCandidates.Clear();
            vm.SelectedLauncher = null;
            vm.LauncherPath = string.Empty;
            vm.LaunchAndLoadCommand.Execute(null);
            WaitUntilIdle(vm);
            Check(main.IsVisible && !vm.IsBusy && vm.StatusMessage == SmokeLauncher.Failure && launcher.Calls.Count == 1,
                "empty selection failure restores main and error without calling launcher");
            Check(GetForegroundWindow() == foreground && GetFocus() == focus && activations == 0,
                "main/float transitions and real failed launch preserve foreground and focus");
        }
        finally { main.Close(); shared.SetValue(null, null); }
    }

    private static void CheckIsolatedQuiescence()
    {
        var suspender = new SmokeSuspender();
        var resources = new SmokeSessionResources(suspender.Events);
        var manifest = TrainerRuntimeAssets.LoadManifest();
        using var session = new TrainerSession(manifest, resources,
            new TrainerTarget(manifest.TargetProcess, (nint)0x400000, true, true, 1234), resources,
            processSuspender: suspender, targetProcessState: resources);
        Check(session.Attach().Success && resources.Allocations == 1,
            "isolated session allocates a synthetic combined block without spawning a helper");
        session.InstallPatches(0);
        Check(suspender.Events.SequenceEqual(new[] { "suspend", "resume" }),
            "production session publishes under fake suspension");
        session.Restore();
        Check(suspender.Events.SequenceEqual(new[] { "suspend", "resume", "suspend", "validate", "free", "resume" }) && resources.Frees == 1,
            "production session validates quiescence, frees and resumes in order without a real target");
    }

    /// <summary>
    /// 面板行板的"纯鼠标"检查：这是旧 CheckCaptureLifecycle（桌面浮窗的拖动/鼠标捕获生命周期）
    /// 以及旧浮窗预设列表那 40 行可达性循环的**合并替代**。
    /// 桌面浮窗连同它的拖动、边缘热区、发丝/迷你条形态一起删除了 —— 那些断言的对象（BeginMouseInteraction /
    /// _statusDragging / StatusLinePanel / DragHandle / SetCapture）在产品里已经不存在，留着只会测空气。
    /// 但旧断言里**真正还有意义**的两条留下来了：点行必须不需要鼠标捕获、也不许抢前景/焦点。
    /// </summary>
    private static void CheckPanelRowsAreMouseReachable(MainViewModel vm, FloatingWindow window, ScrollViewer board,
        IntPtr foreground, IntPtr focus, Func<int> activated)
    {
        var windows = ThreadWindows();
        // 面板的页签也是纯鼠标的：点「功能/增援/选单位」只改 VM 状态，不建 HWND、不抢焦点。
        foreach (var section in ConsoleNavigation.PanelSections)
        {
            var tab = (Button)window.FindName("Nav" + section);
            Check(tab is not null && !tab.Focusable && tab.IsEnabled, $"panel tab {section} enabled without keyboard focus");
            Invoke(tab!); Pump();
            Check(vm.PanelPage == section, $"panel tab {section} writes the shared page state: {vm.PanelPage}");
            Check(GetForegroundWindow() == foreground && GetFocus() == focus && !window.IsActive && activated() == 0,
                $"panel tab {section} preserves foreground and focus");
            Check(windows.SetEquals(ThreadWindows()), $"panel tab {section} creates no extra HWND");
        }
        // 功能页：每一条功能都要在行板上（行数比视口高没关系，面板会跟着高亮自动滚）。
        vm.SelectPanelPage("功能"); Pump(); window.UpdateLayout(); Pump();
        var catalogCount = vm.Groups.Sum(group => group.Features.Count);
        var featureRows = vm.PanelRows.Where(row => row.Action.StartsWith("feat:", StringComparison.Ordinal)).ToArray();
        Check(featureRows.Length == catalogCount, $"面板功能页列全了所有功能 {featureRows.Length}/{catalogCount}");
        // 增援页：行少（一屏放得下），拿它的「数量」行当"纯鼠标可达"的样本。
        vm.SelectPanelPage("增援"); Pump();
        // 行板用的是默认 ItemsPanel（StackPanel，不虚拟化）：换页后容器是**下一帧**才重建的，
        // 早一帧去数会数到上一页留下的那批（选单位页正好 6 军种+表头+返回 = 8 个）。所以按模型对账 + 有界重试。
        static IEnumerable<Button> RowButtons(FloatingWindow host) => Descendants(host).OfType<Button>()
            .Where(button => button.CommandParameter is PanelNavRow);
        var modelRows = vm.PanelRows.ToArray();
        Check(modelRows.Length >= 6, $"面板「增援」页的模型行数: {modelRows.Length}");
        var rowButtons = RowButtons(window).ToArray();
        for (var guard = 0; guard < 20 && rowButtons.Length != modelRows.Length; guard++)
        {
            window.UpdateLayout(); Pump();
            modelRows = vm.PanelRows.ToArray();
            rowButtons = RowButtons(window).ToArray();
        }
        Check(rowButtons.Length == modelRows.Length, $"行板把每一行都渲染成按钮: {rowButtons.Length}/{modelRows.Length}");
        var countRow = modelRows.First(row => row.Action == "count");
        var target = rowButtons.First(button => ReferenceEquals(button.CommandParameter, countRow));
        Check(target.Command is not null, "面板行的按钮带着真命令（ActivatePanelRowCommand）");
        board.ScrollToVerticalOffset(0); Pump();
        target.BringIntoView(); Pump();
        var point = target.TranslatePoint(new Point(target.ActualWidth / 2, target.ActualHeight / 2), board);
        Check(point.X >= 0 && point.X < board.ActualWidth && point.Y >= 0 && point.Y < board.ActualHeight,
            $"数量行落在行板视口内: ({point.X:F1},{point.Y:F1})");
        Check(Mouse.Captured is null && GetCapture() == IntPtr.Zero, "面板点行不需要鼠标捕获");
        var before = vm.ReinforcementCountText;
        Invoke(target); Pump();
        Check(vm.PanelResultText.Length > 0 || vm.ReinforcementCountText != before,
            "面板数量行被点后有可见反馈: " + vm.PanelResultText);
        Check(GetForegroundWindow() == foreground && GetFocus() == focus && !window.IsActive && activated() == 0,
            "面板点行保持前景/焦点（整段只有鼠标路径，没有输入注入）");
        Check(windows.SetEquals(ThreadWindows()) && Mouse.Captured is null && GetCapture() == IntPtr.Zero,
            "面板点行不建新 HWND、不留鼠标捕获（旧拖动捕获生命周期已随桌面浮窗删除）");
    }

    /// <summary>面板证据渲染：旧 RenderPresetEvidence 拍的是"浮窗预设列表+长名字换行"，那个列表已不存在 ⇒ 改拍行板。</summary>
    private static void RenderPanelEvidence(FloatingWindow window, ScrollViewer board)
    {
        board.ScrollToVerticalOffset(0); Pump();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),
            (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(@"D:\work\mock\render", $"panel-rows-{RuntimeInformation.ProcessArchitecture}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var output = File.Create(path)) encoder.Save(output);
        GetWindowRect(new WindowInteropHelper(window).Handle, out var bounds);
        Check(bounds.Right < 0 && bounds.Bottom < 0 && bitmap.PixelWidth > 0, "证据图从真实的离屏窗口渲染");
        Console.WriteLine("PNG=" + path);
    }

    private static void Invoke(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    /// <summary>
    /// RepeatButton 是 ButtonBase 但不是 Button（数量/星级那两个 +/- 就是它）⇒ 按具体类型挑对应的
    /// automation peer：走的就是"真点一下"那条语义，没有第二条命令路径。
    /// </summary>
    private static void Invoke(ButtonBase control)
    {
        switch (control)
        {
            case RepeatButton repeat:
                ((IInvokeProvider)new RepeatButtonAutomationPeer(repeat).GetPattern(PatternInterface.Invoke)).Invoke();
                return;
            case ToggleButton toggle:
                ((IToggleProvider)new ToggleButtonAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)).Toggle();
                return;
            case Button plain:
                ((IInvokeProvider)new ButtonAutomationPeer(plain).GetPattern(PatternInterface.Invoke)).Invoke();
                return;
            default:
                throw new InvalidOperationException($"夹具不支持这个控件类型：{control.GetType().Name}");
        }
    }
    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hwnd);
    private static void CheckCoordinateCountdown(TrainerAppSettingsStore store)
    {
        var memory = new SmokeCoordinateMemory();
        using var vm = new MainViewModel(TrainerRuntimeAssets.LoadManifest(), store,
            new SmokeLauncher(), new SmokeLocator(), _ => memory, _ => new SmokeMemory(),
            new SmokeSuspender(), _ => [], _ => throw new InvalidOperationException("No real probe"));
        var controller = new FeatureController(memory, memory.Resolver);
        void Set(string name, object? value) => typeof(MainViewModel).GetProperty(name)!.SetValue(vm, value);
        Set(nameof(vm.FeatureController), controller);
        Set(nameof(vm.ArePatchesInstalled), true);
        vm.AllowUnverifiedWorldPositionFeatures = true;
        var features = vm.Groups.SelectMany(group => group.Features)
            .Where(item => FeatureSafetyPolicy.IsCoordinateAction(item.Feature)).ToArray();
        Check(features.Length >= 3, "coordinate family available to shared UI command");
        var action = features.First(item => item.Feature.RawName == "Get Me Base");
        var progress = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.CoordinateCountdownText)) progress.Add(vm.CoordinateCountdownText); };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // 产品语义（MainViewModel.PrepareCoordinateActionAsync）：先 HasValidCurrentCoordinates **预检**，
        // 只有"缓存坐标无效"才进 3 秒倒计时。预检本身要读内存 ⇒ 旧夹具（任何读都抛的 SmokeMemory）
        // 永远走不到倒计时分支，旧断言 "memory.Calls == 0" 和新设计自相矛盾。
        // 换成能回答读取的 SmokeCoordinateMemory，把**两条分支**都真走一遍。
        memory.SetCoordinates(0); memory.ConversionCoordinate = 0;   // ① 缓存无效 ⇒ 倒计时分支
        action.Command.Execute(null);
        Check(vm.CoordinateCountdownText == "把鼠标移到地图上 · 3" && memory.Dispatches.Count == 0,
            "click shows explicit three-second guidance before any dispatch: " + vm.CoordinateCountdownText);
        action.Command.Execute(null);
        features.First(item => !ReferenceEquals(item, action)).Command.Execute(null);
        Check(progress.Count == 1, "repeated and competing coordinate clicks cannot schedule later dispatch");
        PumpUntil(() => !vm.IsCoordinateActionPending);
        Check(watch.Elapsed >= TimeSpan.FromSeconds(3), "countdown really consumed the full three seconds");
        Check(progress.Take(3).Select(text => text[^1]).SequenceEqual(new[] { '3', '2', '1' }), "countdown reports 3, 2, 1");
        Check(memory.Dispatches.SequenceEqual(new byte[] { 14 }),
            "converted coordinate 0 still rejects generation (stage 1 only): " + string.Join(",", memory.Dispatches));
        Check(action.Status == "失败", "invalid fake state still rejects after countdown");
        // ② 缓存坐标有效 ⇒ 立即派发，不进倒计时（这一条分支旧夹具根本走不到）
        memory.SetCoordinates(200); memory.ConversionCoordinate = 200; memory.Dispatches.Clear(); progress.Clear();
        watch.Restart(); action.Command.Execute(null); Pump();
        Check(watch.Elapsed < TimeSpan.FromSeconds(1) && !progress.Any(text => text.StartsWith("把鼠标")),
            "valid cached coordinates dispatch immediately without a countdown");
        Check(memory.Dispatches.SequenceEqual(new byte[] { 14, 11 }),
            "valid conversion runs the complete two-stage protocol: " + string.Join(",", memory.Dispatches));
        // ③ 取消 / 会话替换 / 权限撤销：一律"零新派发"。
        // 旧断言数的是"内存访问次数"（memory.Calls），但预检本身就会读 ⇒ 那个数必然变；
        // 真正该钉住的是**派发**没发生。
        memory.SetCoordinates(0); memory.ConversionCoordinate = 200;
        var dispatches = memory.Dispatches.Count;
        // 坐标族里各功能的预检需求不同：世界坐标类（如 Get Me Base）这个夹具能完整支撑 →
        // 会进可取消的倒计时；而"作用于已选中对象"那一类（如 Select Unit Copy For Me）需要对象头/类型数据，
        // 这个夹具不提供 ⇒ 预检当场拒绝（mode='本次：预检拒绝，未派发。'）。
        // 两条路都合法，但**必须**落在其中一条，并且都不许立刻派发。
        // 另外要求至少有一个真的走进倒计时并取消掉 —— 否则这条循环退化成"全都拒绝"也会假装通过。
        var enteredCountdown = 0;
        foreach (var feature in features)
        {
            memory.SetCoordinates(0);
            var before = memory.Dispatches.Count;
            feature.Command.Execute(null);
            var pending = vm.IsCoordinateActionPending;
            var refused = vm.CoordinateActionMode.StartsWith("本次：预检拒绝", StringComparison.Ordinal);
            Check((pending || refused) && memory.Dispatches.Count == before,
                $"coordinate feature neither dispatched early nor silently ignored: {feature.Feature.RawName}"
                + $"; pending={pending}; mode='{vm.CoordinateActionMode}'");
            if (pending)
            {
                enteredCountdown++;
                vm.CancelCoordinateActionCommand.Execute(null);
                PumpUntil(() => !vm.IsCoordinateActionPending);
            }
            Check(memory.Dispatches.Count == before, "cancel (or an upfront refusal) leaves zero dispatch: " + feature.Feature.RawName);
        }
        Check(enteredCountdown > 0, "at least one coordinate feature really entered the cancellable countdown");
        // 队列（AddCurrentReinforcementToQueueCommand / ExecuteReinforcementQueueCommand / IsQueueRunning）
        // 已从 UI 撤掉 —— MainViewModel 里写着 "Retained queue API; the UI intentionally has no automatic queue button"，
        // 队列逻辑只剩 internal 的 ExecuteReinforcementQueueAsync 给单测用 ⇒ 这一段整体删除。
        action.Command.Execute(null);
        typeof(MainViewModel).GetMethod("CancelSessionActions", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null);
        PumpUntil(() => !vm.IsCoordinateActionPending);
        Check(memory.Dispatches.Count == dispatches, "session replacement cancels pending countdown without dispatch");
        action.Command.Execute(null);
        vm.AllowUnverifiedWorldPositionFeatures = false;
        vm.AllowUnverifiedWorldPositionFeatures = true;
        PumpUntil(() => !vm.IsCoordinateActionPending);
        Check(memory.Dispatches.Count == dispatches, "revoking and reenabling permission cannot revive pending request");
        Set(nameof(vm.FeatureController), null);
        Set(nameof(vm.ArePatchesInstalled), false);
    }

    private static void PumpUntil(Func<bool> done)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && watch.Elapsed < TimeSpan.FromSeconds(6))
        {
            Pump();
            Thread.Sleep(5);
        }
        Check(done(), "asynchronous fake action completed within bounded wait");
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void WaitUntilIdle(MainViewModel vm)
    {
        var deadline = Environment.TickCount64 + 15000;
        do
        {
            Pump();
            if (!vm.IsBusy) return;
            Thread.Sleep(10);
        } while (Environment.TickCount64 < deadline);
        throw new TimeoutException($"启动流程 15 秒内未结束：{vm.StatusMessage}");
    }
    private static bool IsWithin(DependencyObject? hit, DependencyObject expected)
    {
        while (hit is not null)
        {
            if (ReferenceEquals(hit, expected)) return true;
            hit = VisualTreeHelper.GetParent(hit);
        }
        return false;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); _assertions++; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
