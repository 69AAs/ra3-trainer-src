using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Threading;
using Ra3Trainer.App.Diagnostics;
using Ra3Trainer.App.Hotkeys;
using Ra3Trainer.App.Views;
using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Hotkeys;
using Ra3Trainer.Core.Manifest;
using Ra3Trainer.Core.Memory;
using Ra3Trainer.Core.Runtime;

namespace Ra3Trainer.App.ViewModels;

public enum ConnectionState
{
    Disconnected,
    Connected,
    Processing,
    AbnormalExit,
    ExitUnknown
}

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly TrainerManifest _manifest;
    private string _attachedIdentity = "未附加";
    private readonly TrainerAppSettingsStore _settingsStore;
    private readonly IProcessLocator _locator;
    private readonly ILauncher _launcher;
    private readonly Func<int, IProcessMemory> _memoryFactory;
    private readonly Func<IProcessMemory, IRemoteAllocator> _allocatorFactory;
    // 取「当前 mod」的硬信号（进程命令行 + 已加载模块）。默认走真机器目标进程；夹具可注入假信号。
    private readonly Func<string?, int?, ModSignalSnapshot> _modSignals;
    private readonly IProcessSuspender _processSuspender;
    private readonly Func<string, IReadOnlyList<LauncherCandidate>> _scanLaunchers;
    private readonly LowLevelKeyboardHook _keyboardHook = new();
    private readonly ForegroundWindowProcess _foregroundWindowProcess = new();
    private readonly HotkeyFeatureDispatcher _hotkeyDispatcher = new();
    private readonly TrainerDiagnostics _diagnostics = new(AppContext.BaseDirectory);
    private readonly DispatcherTimer _targetExitTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _probeTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Func<int, IProbeMemory> _probeMemoryFactory;
    private IProbeMemory? _probeMemory;
    private ReadOnlyProbe? _probe;
    private ProbeLog? _probeLog;
    private bool _safeSamplingEnabled;
    private Process? _targetProcess;
    private string? _targetModulePath;
    private bool _inspectHooksBeforeInstall = true;
    private bool _autoCollectEvidenceOnExit = true;
    private string _diagnosticsSummary = "未连接。";
    private string _hookSummary = "未检查。";
    private string _featureSummary = "未连接。";
    private string _lastEvidencePath = string.Empty;
    private IReadOnlyList<ReinforcementUnitEntry> _reinforcementUnits = ReinforcementUnitCatalog.LoadBuiltIn();
    private IProcessMemory? _memory;
    private TrainerSession? _session;
    private IProductOverlay? _overlay;
    private readonly DispatcherTimer _overlayTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    // 后台自动采样（用户 2026-10-06 的要求）：换个 mod 别让我们手打几百条单位。
    // 只读 —— 读的就是已经在读的 SelectedUnitCode 那 4 个字节，不写游戏内存、不调引擎内部。
    private readonly DispatcherTimer _unitSampleTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly HashSet<uint> _sampledUnits = new();

    /// <summary>调节态：在数值行上按 5 进入 ⇒ 4/6 改大小 ⇒ 再按 5 确认退出（用户 2026-10-06 定的口径）。</summary>
    private bool _panelAdjustMode;
    private bool _overlayEnabled = true;
    private string _overlayAction = "尚无动作";
    private int _overlayPanelX = 24, _overlayPanelY = 24;
    private string _overlayPanelSection = ConsoleNavigation.DefaultPanelSection;
    public int OverlayPanelX { get => _overlayPanelX; set { _overlayPanelX = Math.Clamp(value, 0, 8192); OnPropertyChanged(); OnPropertyChanged(nameof(PanelPositionText)); } }
    public int OverlayPanelY { get => _overlayPanelY; set { _overlayPanelY = Math.Clamp(value, 0, 8192); OnPropertyChanged(); OnPropertyChanged(nameof(PanelPositionText)); } }
    public IReadOnlyList<string> OverlayPanelSections => ConsoleNavigation.PanelSections;
    /// <summary>
    /// **唯一的页状态**：主窗页签、浮窗页签栏、游戏内面板的行集合全读它。
    /// 以前这里是三份（Navigation.Selected / _overlayPanelSection / _panelPage）各走各的，
    /// "点主窗页签面板不动、面板换页主窗不动"都是这么来的。改这一份会级联到面板页。
    /// </summary>
    public string OverlayPanelSection
    {
        get => _overlayPanelSection;
        set
        {
            if (!ConsoleNavigation.IsPanelSection(value)) return;
            if (_overlayPanelSection == value && _panelPage == value) return;
            _overlayPanelSection = value;
            OnPropertyChanged();
            if (_panelPage != value)
            {
                _panelPage = value;
                OnPropertyChanged(nameof(PanelPage));
                RefreshPanelRows();
            }
        }
    }
    public string OverlayStatus => _overlay?.Status ?? "游戏内状态行未连接。";

    private bool _panelMode;
    /// <summary>M4: the panel's own row cursor. Keys move it; the panel draws it.</summary>
    public PanelNavigator Navigator { get; } = new();
    // 面板不再有"光标"：每一行印着自己的键（1/5//+//./0 等），按哪个键执行哪一行。
    // 面板只做打仗的事（发兵/换兵/调数值/找兵），低频的启动与诊断留在主窗页签。
    public const string PanelLayerMain = "main";
    // 找兵第 1 层从"军种"（陆/空/海/建筑/特殊）改成**真阵营**（盟军/苏联/帝国/神州），
    // 值名也一起改（"domain" 会误导下一个人以为这层还是军种）。用户 2026-10-07 口径 B。
    public const string PanelLayerFaction = "faction";
    // 第 2 层 = **游戏自带分类**（步兵/车辆/飞行器/舰船/建筑/特殊，来自游戏内部名 editor_name）。
    // 用户 2026-10-07 定案："第 2 层用游戏分类"；「用途」那层从面板流程整个去掉。
    public const string PanelLayerDomain = "domain";
    public const string PanelLayerUnit = "unit";
    /// <summary>
    /// 口径 B（2026-10-06）：收藏槽**不再占键** —— 1/3/7/9 归回方向/确定那一片，收藏槽靠 8/2 走到 + 5 执行。
    /// </summary>
    private static readonly string[] PanelSlotKeys = ["1", "3", "7", "9"];
    /// <summary>一屏 10 个：编号 1..9 和 0。</summary>
    private static readonly string[] PanelPickKeys = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"];
    private const int PanelSlotCount = 4;
    private const int PanelScreenSize = 10;
    private string _panelLayer = PanelLayerMain;
    // 找兵第 1 层选的是**真阵营名**（"盟军"…），不再是 UnitDomain 枚举 ⇒ 收 string?。
    private string? _panelFaction;
    // 找兵第 2 层选的是**游戏分类**（步兵/车辆/飞行器/舰船/建筑/特殊）。
    private UnitDomain? _panelDomain;
    private int _panelScreen;
    private bool _panelPositionMode;
    private readonly List<ReinforcementUnitEntry> _panelRecent = new();
    private string? _panelResult;
    public IReadOnlyList<PanelNavRow> PanelRows => Navigator.Rows;
    public string PanelPageLabel => Navigator.PageLabel;
    public string PanelSelectionLabel => Navigator.SelectionLabel;
    public string PanelPositionText => $"{_overlayPanelX} , {_overlayPanelY}";
    public string PanelLayer => _panelLayer;
    public string? PanelFaction => _panelFaction;
    public UnitDomain? PanelDomain => _panelDomain;
    public int PanelScreen => _panelScreen;
    public bool PanelPositionMode => _panelPositionMode;
    public IReadOnlyList<ReinforcementUnitEntry> PanelSlots => PanelSlotUnits();

    /// <summary>M2a: 面板模式同时打开小键盘导航。同一实例被浮窗与离屏面板共用。</summary>
    public bool PanelMode
    {
        get => _panelMode;
        set
        {
            if (_panelMode == value) return;
            _panelMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PanelModeText));
            OnPropertyChanged(nameof(OverlayPanelTitle));
            // 进出面板模式都回到主层：面板只有这一层是"打仗用的"，
            // 选择层和位置调整不会跨模式留着（否则下一次开面板会莫名停在半路）。
            _panelLayer = PanelLayerMain;
            _panelFaction = null;
            _panelDomain = null;
            _panelScreen = 0;
            _panelPositionMode = false;
            _panelAdjustMode = false;   // 调节态也要清：OCR 复核抓到的（关了面板再开还留着"正在调数量"）
            SetPanelResult(null);
            // 开面板就是开始一局游戏，桌面那侧的旧状态（启动器扫描/开启修改之类）到这里已经过期，
            // 留着会以"结果行"的名义画进游戏画面（用户点过："面板的增援你看着不乱吗"）。
            if (value) StatusMessage = string.Empty;
            RefreshPanelRows();
            if (value)
            {
                MovePanelHighlight(1);   // 开面板：高亮落在第一个能执行的行上，一眼看到"现在在哪"
            }
            else
            {
                Navigator.ClearSelection();   // 关面板：不留高亮，画面必须恢复原样
            }

            RefreshPanelRows();
        }
    }
    public string PanelModeText => PanelMode
        ? "面板模式 · Num8/2 选行 · Num5 执行 · Num1 心灵控制（直通） · Num4/6 调数值（先按5进调节） · Num7/9 或 PgUp/PgDn 换页 · 0 返回 · Ins 关"
        : string.Empty;
    public void TogglePanelMode() => PanelMode = !PanelMode;
    public string OverlayGuidance => OverlayFeedback.Guidance;
    public bool OverlayRefused => OverlayStatus.Contains("本局未安装", StringComparison.Ordinal);
    public string OverlayPanelTitle => OverlayRefused ? "游戏内面板本局未安装 · 诊断页查看原因"
        : PanelMode ? "红警3 · 面板模式已开" : "红警3 · 分区控制台";

    public bool OverlayEnabled
    {
        get => _overlayEnabled;
        set { _overlayEnabled = value; PublishOverlay(); OnPropertyChanged(); }
    }
    private void PublishOverlay()
    {
        RunOverlay(overlay => overlay.Publish(_overlayEnabled, _session?.CanUseFeatures == true && ArePatchesInstalled,
            EnabledFeatureCount, _overlayAction));
        OnPropertyChanged(nameof(OverlayStatus));
        OnPropertyChanged(nameof(OverlayRefused));
        OnPropertyChanged(nameof(OverlayPanelTitle));
    }
    private void RunOverlay(Action<IProductOverlay> operation)
    {
        try { if (_overlay is not null) operation(_overlay); }
        catch (Exception ex) { _diagnostics.Log.Write("osd-isolated-failure", new { error = ex.Message }); }
    }
    private int? _targetProcessId;
    private string _statusMessage = "还没连接游戏。";
    private string _launcherPath;
    private string _launcherArguments;
    private string _reinforcementUnitIdText = $"0x{ReinforcementSettings.DefaultUnitId:X8}";
    private ReinforcementUnitEntry? _selectedReinforcementUnit;
    private bool _syncingReinforcementUnit;
    private LauncherCandidate? _selectedLauncher;
    private bool _syncingLauncher;
    private readonly string _userUnitCatalogPath;
    private readonly List<ReinforcementUnitEntry> _userUnits = new();
    private bool _unitCatalogReady;
    // 单位按 mod 分层：阵营显示表 + mod 别名（来自 settings，缺省=内置默认），
    // 以及"显示全部 mod 单位"开关（默认关：只显当前 mod + 原版通用）。
    private IReadOnlyList<UnitTaxonomy.FactionDisplay> _factionDisplay = UnitTaxonomy.DefaultFactionDisplays;
    private IReadOnlyDictionary<string, string> _modSourceAliases = UnitModScope.DefaultModSourceAliases;
    // 已知 mod 的盘上/进程里指纹（来自 settings，缺省=内置：日冕 + ARMod）。装新 mod 加配置即可，不改代码。
    private IReadOnlyList<ModFingerprint> _modFingerprints = ModIdentifier.DefaultFingerprints;
    // 当前 mod 的多信号识别结果（命令行为准 > 已加载模块 > 启动器配置 > default）。默认 = 未知（宽容）。
    // 为什么要有它：上一轮"当前 mod"只由启动器 exe 名推 —— 换 mod 忘了改配置就错判。
    private ModIdentity _modIdentity = ModIdentity.Unknown;
    private bool _showAllModUnits;
    private readonly IReadOnlyDictionary<string, string> _unitNameMap = ReinforcementUnitCatalog.LoadBuiltInNameMap();
    private string _reinforcementCountText = ReinforcementSettings.DefaultCount.ToString();
    private string _reinforcementRankText = ReinforcementSettings.DefaultRank.ToString();
    private string _presetNameText = string.Empty;
    private string _moneyAmountText;
    private string _powerValueText;
    private string _scPointValueText;
    private ReinforcementPreset? _selectedReinforcementPreset;
    private readonly int _attachTimeoutSeconds;
    private bool _arePatchesInstalled;
    private bool _isBusy;
    private bool _isScanningUnits;
    private ConnectionState _connectionState = ConnectionState.Disconnected;
    private bool _allowNonStandardVersion;
    private volatile bool _allowUnverifiedWorldPositionFeatures;
    private bool _autoFloatAfterLaunch = true;
    public event Action<LaunchWindowMode>? LaunchWindowModeRequested;

    public bool AutoFloatAfterLaunch
    {
        get => _autoFloatAfterLaunch;
        set
        {
            if (_autoFloatAfterLaunch == value) return;
            var previous = _autoFloatAfterLaunch;
            _autoFloatAfterLaunch = value;
            try { PersistSettings(); }
            catch (Exception ex)
            {
                _autoFloatAfterLaunch = previous;
                StatusMessage = $"保存自动浮窗设置失败：{ex.Message}";
            }
            OnPropertyChanged();
        }
    }
    private CancellationTokenSource _sessionCancellation = new();
    private int _hotkeyGeneration;
    private bool _disposed;

    private CancellationTokenSource? _coordinateActionCancellation;
    private string _coordinateCountdownText = string.Empty;
    private string _coordinateActionMode = "坐标有效立即执行；面板无效时倒计时，热键无效时直接提示。";
    public string CoordinateActionMode
    {
        get => _coordinateActionMode;
        private set { _coordinateActionMode = value; OnPropertyChanged(); }
    }
    public string CoordinateCountdownText
    {
        get => _coordinateCountdownText;
        private set { _coordinateCountdownText = value; OnPropertyChanged(); }
    }
    public bool IsCoordinateActionPending => _coordinateActionCancellation is not null;
    public string SelectedUnitHelp => "只对选中列表的第一个单位生效";
    public RelayCommand CancelCoordinateActionCommand { get; private set; } = null!;

    internal CancellationTokenSource? BeginCoordinateAction()
    {
        if (_disposed || IsCoordinateActionPending || !ArePatchesInstalled) return null;
        _coordinateActionCancellation = CancellationTokenSource.CreateLinkedTokenSource(SessionCancellation);
        OnPropertyChanged(nameof(IsCoordinateActionPending));
        CancelCoordinateActionCommand.RaiseCanExecuteChanged();
        return _coordinateActionCancellation;
    }

    internal async Task PrepareCoordinateActionAsync(FeatureController controller, TrainerFeature feature,
        ReinforcementSettings? settings, bool fromHotkey, CancellationToken token, string? requestId = null)
    {
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(controller, FeatureController) || !ArePatchesInstalled)
            throw new OperationCanceledException(token);
        bool currentValid;
        try { currentValid = controller.HasValidCurrentCoordinates(feature, settings); }
        catch { CoordinateActionMode = "本次：预检拒绝，未派发。"; throw; }
        // A cached zero/old copy must never make the first hotkey unusable. The unchanged
        // Core conversion obtains this request's coordinates and rejects invalid results.
        if (currentValid || fromHotkey)
        {
            _diagnostics.Log.Write("coordinate-timing", new { requestId, attachSessionId = _diagnostics.ActionIdentity?.AttachSessionId, feature = feature.RawName, source = fromHotkey ? "hotkey" : "panel", mode = "immediate", cachedCoordinatesValid = currentValid });
            CoordinateActionMode = "本次：立即执行 · 无倒计时";
            CoordinateCountdownText = "立即转换并校验本次坐标 · 通过后派发";
            StatusMessage = CoordinateCountdownText;
            return;
        }
        CoordinateActionMode = "本次：坐标未通过预检 · 倒计时后重新校验";
        _diagnostics.Log.Write("coordinate-timing", new { requestId, attachSessionId = _diagnostics.ActionIdentity?.AttachSessionId, feature = feature.RawName, source = "panel", mode = "countdown", cachedCoordinatesValid = false });
        await WaitForMapCursorAsync(controller, token);
    }

    internal async Task WaitForMapCursorAsync(FeatureController controller, CancellationToken token)
    {
        for (var seconds = 3; seconds > 0; seconds--)
        {
            token.ThrowIfCancellationRequested();
            CoordinateCountdownText = $"把鼠标移到地图上 · {seconds}";
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(controller, FeatureController) || !ArePatchesInstalled)
            throw new OperationCanceledException(token);
        CoordinateCountdownText = "倒计时结束，正在转换并校验坐标";
    }

    internal void EndCoordinateAction(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_coordinateActionCancellation, cancellation))
        {
            _coordinateActionCancellation = null;
            CoordinateCountdownText = string.Empty;
            OnPropertyChanged(nameof(IsCoordinateActionPending));
            CancelCoordinateActionCommand.RaiseCanExecuteChanged();
        }
        cancellation.Dispose();
    }

    internal CancellationToken SessionCancellation => _sessionCancellation.Token;

    public ConnectionState ConnectionState
    {
        get => _connectionState;
        private set
        {
            _connectionState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConnectionStateText));
        }
    }

    /// <summary>连接状态**给用户看的那句话**（枚举名是内部实现，绝不能让用户看到 Disconnected 这种英文）。</summary>
    public string ConnectionStateText => ConnectionState switch
    {
        ConnectionState.Disconnected => "未连接",
        ConnectionState.Connected => "已连接",
        ConnectionState.Processing => "处理中",
        ConnectionState.AbnormalExit => "游戏异常退出",
        ConnectionState.ExitUnknown => "游戏已退出",
        _ => "未知"
    };

    /// <summary>Explicit, persisted acceptance of unverified position and camera risks.</summary>
    public bool AllowUnverifiedWorldPositionFeatures
    {
        get => _allowUnverifiedWorldPositionFeatures;
        set
        {
            if (_allowUnverifiedWorldPositionFeatures == value) return;
            _allowUnverifiedWorldPositionFeatures = value;
            // Revoking consent retires this request even if consent is enabled again.
            // The linked token is checked by Core before conversion and generation.
            if (!value) _coordinateActionCancellation?.Cancel();
            try
            {
                PersistSettings();
            }
            catch (Exception ex)
            {
                // A failed opt-in save must not silently enable the risky features.
                if (value) _allowUnverifiedWorldPositionFeatures = false;
                OnPropertyChanged();
                OnPropertyChanged(nameof(WorldPositionRiskNotice));
                StatusMessage = $"保存风险设置失败：{ex.Message}；当前已禁止新的坐标／对象类请求，但磁盘仍保留上次设置，重启前请重试保存。";
                return;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(WorldPositionRiskNotice));
            StatusMessage = WorldPositionRiskNotice;
        }
    }

    public string WorldPositionRiskNotice => AllowUnverifiedWorldPositionFeatures
        ? "已允许校验：仅在主机只读校验通过后执行；缺少完整对象链时拒绝。经验观测边界不是引擎不变量，仍有对象变化风险。生成／销毁／转让／复制不可回滚；选择会保存。"
        : "默认关闭：勾选后仍须校验通过才执行。关闭只阻止新请求；已开启的缩放／随地建造需单独关闭，生成／销毁／转让／复制不可回滚。";

    /// <summary>
    /// Compatibility mode for the version check: when on, the trainer also attaches to 1.12.x
    /// builds other than the one the hook addresses were derived from. Persisted immediately so a
    /// restart keeps the user's choice; default off = strict match.
    /// </summary>
    public bool AllowNonStandardVersion
    {
        get => _allowNonStandardVersion;
        set
        {
            if (_allowNonStandardVersion == value)
            {
                return;
            }

            _allowNonStandardVersion = value;
            _locator.AllowNonStandardVersion = value;
            OnPropertyChanged();
            try
            {
                PersistSettings();
            }
            catch (Exception ex)
            {
                StatusMessage = $"保存设置失败：{ex.Message}";
            }
        }
    }

    /// <summary>
    /// Run the hook-site check before installing (default on). Turning it off skips the refusal that
    /// protects against writing into a binary that does not match the patch manifest.
    /// </summary>
    public bool InspectHooksBeforeInstall
    {
        get => _inspectHooksBeforeInstall;
        set
        {
            if (_inspectHooksBeforeInstall == value)
            {
                return;
            }

            _inspectHooksBeforeInstall = value;
            OnPropertyChanged();
            PersistSettingsSafe();
        }
    }

    /// <summary>Write a crash-evidence bundle automatically when the target dies with an error.</summary>
    public bool AutoCollectEvidenceOnExit
    {
        get => _autoCollectEvidenceOnExit;
        set
        {
            if (_autoCollectEvidenceOnExit == value)
            {
                return;
            }

            _autoCollectEvidenceOnExit = value;
            OnPropertyChanged();
            PersistSettingsSafe();
        }
    }


    /// <summary>
    /// Persists the pinned floating-window form. Driven from the floating window's own right-click
    /// menu so the switch works without opening the main window mid-game.
    /// </summary>
    /// <summary>
    /// 浮窗形态下拉的可选项。顺序＝推荐程度：发丝状态线最不挡视线，完整条是回滚档。
    /// </summary>

    /// <summary>下拉的绑定目标；写回时立刻落盘，重开还是这个形态。</summary>



    private MainViewModel(TrainerManifest manifest, TrainerAppSettingsStore settingsStore)
        : this(manifest, settingsStore, new GameLauncher(), new TrainerProcessLocator(),
            processId => new Win32ProcessMemory(processId), memory => (IRemoteAllocator)memory,
             Win32ProcessSuspender.Instance, path => LauncherScanner.Scan(LauncherScanner.BuildRoots(path)))
    {
        try { _overlay = new ProductOverlay((kind, message) => _diagnostics.Log.Write(kind, new { message }), () => new OffscreenPanel(this)); }
        catch (Exception ex) { _diagnostics.Log.Write("osd-init-failed", new { error = ex.Message }); }
        RunOverlay(overlay => overlay.Cleanup());
        _overlayTimer.Tick += (_, _) => PublishOverlay();
    }

    /// <summary>Explicit dependency composition; the default factory retains production services.</summary>
    public MainViewModel(TrainerManifest manifest, TrainerAppSettingsStore settingsStore,
        ILauncher launcher, IProcessLocator locator, Func<int, IProcessMemory> memoryFactory,
        Func<IProcessMemory, IRemoteAllocator> allocatorFactory, IProcessSuspender processSuspender,
        Func<string, IReadOnlyList<LauncherCandidate>> scanLaunchers,
        Func<int, IProbeMemory>? probeMemoryFactory = null, IProductOverlay? overlay = null,
        Func<string?, int?, ModSignalSnapshot>? modSignals = null)
    {
        _overlay = overlay;
        _modSignals = modSignals ?? TargetModSignalProbe.Capture;
        _probeMemoryFactory = probeMemoryFactory ?? (id => new Win32ProbeMemory(id));
        _probeTimer.Tick += OnProbeTick;
        ExportProbeCommand = new RelayCommand(ExportProbe);
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _memoryFactory = memoryFactory ?? throw new ArgumentNullException(nameof(memoryFactory));
        _allocatorFactory = allocatorFactory ?? throw new ArgumentNullException(nameof(allocatorFactory));
        _processSuspender = processSuspender ?? throw new ArgumentNullException(nameof(processSuspender));
        _scanLaunchers = scanLaunchers ?? throw new ArgumentNullException(nameof(scanLaunchers));
        var lastExit = _diagnostics.LastExit;
        _lastEvidencePath = lastExit?.EvidencePath ?? string.Empty;
        LastExitDescription = lastExit is null ? "尚无退出记录。" : $"上次退出 {lastExit.Time:O}：{lastExit.Description}";
        _manifest = manifest;
        _settingsStore = settingsStore;
        var settings = settingsStore.Load();
        _launcherPath = settings.LauncherPath;
        _launcherArguments = settings.LauncherArguments;
        _factionDisplay = settings.FactionDisplay;
        _modSourceAliases = settings.ModSourceAliases;
        _modFingerprints = settings.ModFingerprints;
        _attachTimeoutSeconds = settings.AttachTimeoutSeconds;
        _allowNonStandardVersion = settings.AllowNonStandardVersion;
        _allowUnverifiedWorldPositionFeatures = settings.AllowUnverifiedWorldPositionFeatures;
        _autoFloatAfterLaunch = settings.AutoFloatAfterLaunch;
        _locator.AllowNonStandardVersion = _allowNonStandardVersion;
        _inspectHooksBeforeInstall = settings.InspectHooksBeforeInstall;
        _autoCollectEvidenceOnExit = settings.AutoCollectEvidenceOnExit;
        _moneyAmountText = settings.ResourceValues.MoneyAmount.ToString();
        _powerValueText = settings.ResourceValues.PowerValue.ToString();
        _scPointValueText = settings.ResourceValues.ScPointValue.ToString();
        Groups = new ObservableCollection<FeatureGroupViewModel>(
            CreateGroups(TrainerFeatureCatalog.CreateUiFeatures(manifest.Features)));
        ReinforcementPresets = new ObservableCollection<ReinforcementPreset>(settings.ReinforcementPresets);
        LauncherCandidates = new ObservableCollection<LauncherCandidate>();
        RescanLaunchers();
        // 先识别「当前 mod」（构造期还没进程信号 ⇒ 落回启动器配置/default），再据此定位分档的单位清单。
        _modIdentity = ComputeModIdentity();
        _userUnitCatalogPath = ReinforcementUnitCatalog.DefaultUserCatalogPath(AppContext.BaseDirectory);
        // 老文件（向后兼容）+ 当前 mod 专属（mod 之间单位不互通，各存各的）
        _userUnits.AddRange(ReinforcementUnitCatalog.LoadUserCatalog(_userUnitCatalogPath));
        _userUnits.AddRange(LoadModUserCatalog(
            CurrentModBucketLabel(),
            ReinforcementUnitCatalog.UserCatalogPathForModKey(AppContext.BaseDirectory, _modIdentity.ModKey)));
        ReinforcementUnits = new ObservableCollection<ReinforcementUnitEntry>(
            ReinforcementUnitCatalog.Merge(_reinforcementUnits, _userUnits));
        _unitCatalogReady = true;
        // 714 条平铺没法挑：按"阵营 · 单位/建筑"分组显示（默认视图上挂分组描述，UI 不用管）
        System.Windows.Data.CollectionViewSource.GetDefaultView(ReinforcementUnits)
            .GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(ReinforcementUnitEntry.Category)));
        SynchronizationContext.Current?.Post(_ => OnPropertyChanged(nameof(ReinforcementUnitsSummary)), null);
        SyncSelectedReinforcementUnit();
        CancelCoordinateActionCommand = new RelayCommand(() =>
        {
            _coordinateActionCancellation?.Cancel();
            StatusMessage = "已取消后续坐标动作；已发出的动作不能撤回。";
        }, () => IsCoordinateActionPending);
        RefreshCommand = new RelayCommand(RefreshProcess);
        BrowseLauncherCommand = new RelayCommand(BrowseLauncherPath, () => !IsBusy);
        SaveLauncherSettingsCommand = new RelayCommand(SaveLauncherSettings);
        LaunchAndLoadCommand = new RelayCommand(() => _ = LaunchAndLoadAsync(), () => !IsBusy);
        OpenReinforcementUnitPickerCommand = new RelayCommand(OpenReinforcementUnitPicker, () => !IsBusy);
        SaveReinforcementPresetCommand = new RelayCommand(SaveReinforcementPreset, () => !IsCoordinateActionPending);
        ApplyReinforcementPresetCommand = new RelayCommand(value =>
        {
            if (value is ReinforcementPreset preset) SelectedReinforcementPreset = preset;
            ApplySelectedReinforcementPreset();
        }, () => !IsCoordinateActionPending);
        InstallPatchesCommand = new RelayCommand(InstallPatches, () => _session?.CanUseFeatures == true && !ArePatchesInstalled && _session.InstalledHookCount == 0);
        RestorePatchesCommand = new RelayCommand(RestorePatches);
        ActivatePanelRowCommand = new RelayCommand(value => ActivatePanelRow(value as PanelNavRow));
        PanelPageCommand = new RelayCommand(value => SelectPanelPage(value as string));
        ReadSelectedUnitCodeCommand = new RelayCommand(ReadSelectedUnitCode, () => FeatureController is not null);
        ProbeSelectedUnitOwnershipCommand = new RelayCommand(ProbeSelectedUnitOwnership,
            () => FeatureController is not null && _targetProcessId is not null);
        // 心灵控制（写入）走引擎自己的 0x09 动作 ⇒ 必须有装好的 patch（游戏侧代码才在）。
        ApplyOwnershipTransferCommand = new RelayCommand(() => _ = ApplyOwnershipTransferAsync(),
            () => FeatureController is not null && _targetProcessId is not null && ArePatchesInstalled);
        // 「解除心灵控制」用最近一次落盘快照回写；没快照就不给点。
        RestoreOwnershipCommand = new RelayCommand(RestoreOwnership,
            () => FeatureController is not null && _targetProcessId is not null &&
                  _lastOwnershipApply is not null);
        ScanUnitTypesCommand = new RelayCommand(
            () => _ = ScanUnitTypesAsync(),
            () => !_isScanningUnits && _targetProcessId is not null);
        InspectHooksCommand = new RelayCommand(InspectHookSites);
        ExportEvidenceCommand = new RelayCommand(() => ExportEvidence("手动导出"));
        OpenLogFolderCommand = new RelayCommand(() => OpenFolder(LogDirectory));
        OpenDataFolderCommand = new RelayCommand(() => OpenFolder(Ra3Trainer.Core.Runtime.TrainerPaths.DataRoot));
        OpenEvidenceFolderCommand = new RelayCommand(() => OpenFolder(EvidenceDirectory));
        ClearFeatureFilterCommand = new RelayCommand(() => FeatureFilter = string.Empty);
        RescanLaunchersCommand = new RelayCommand(RescanLaunchers, () => !IsBusy);
        _keyboardHook.KeyDown += OnKeyboardHookKeyDown;
        _keyboardHook.KeyUp += OnKeyboardHookKeyUp;
        _targetExitTimer.Tick += OnTargetExitTimerTick;
        _diagnostics.Log.Write("session-start", new
        {
            targetProcess = _manifest.TargetProcess,
            hookCount = _manifest.PatchManifest.Hooks.Count,
            featureCount = Groups.Sum(group => group.Features.Count),
            allowNonStandardVersion = _allowNonStandardVersion,
            allowUnverifiedWorldPositionFeatures = AllowUnverifiedWorldPositionFeatures,
            inspectHooksBeforeInstall = _inspectHooksBeforeInstall,
            autoCollectEvidenceOnExit = _autoCollectEvidenceOnExit,
            attachTimeoutSeconds = _attachTimeoutSeconds
        });
        RefreshFeatureSummary();
        ApplyFeatureFilter();
        // M4: 面板行是快照，模型一变就重建；高亮行由 PanelNavigator 钳制。
        PropertyChanged += OnPanelSourceChanged;
        Navigator.PropertyChanged += OnNavigatorChanged;
        ReinforcementPresets.CollectionChanged += (_, _) => RefreshPanelRows();
        LauncherCandidates.CollectionChanged += (_, _) => RefreshPanelRows();
        foreach (var item in Groups.SelectMany(group => group.Features)) item.PropertyChanged += OnFeatureChanged;
        RefreshPanelRows();
    }

    public static MainViewModel LoadDefault()
    {
        return new MainViewModel(TrainerRuntimeAssets.LoadManifest(), new TrainerAppSettingsStore());
    }

    public ObservableCollection<FeatureGroupViewModel> Groups { get; }

    public ObservableCollection<ReinforcementPreset> ReinforcementPresets { get; }


    public RelayCommand RefreshCommand { get; }

    public RelayCommand BrowseLauncherCommand { get; }

    public RelayCommand SaveLauncherSettingsCommand { get; }

    public RelayCommand LaunchAndLoadCommand { get; }

    public RelayCommand OpenReinforcementUnitPickerCommand { get; }

    public RelayCommand SaveReinforcementPresetCommand { get; }

    public RelayCommand ApplyReinforcementPresetCommand { get; }

    /// <summary>M4: 面板行的鼠标入口（与 Num5 走同一条执行路径，不复制逻辑）。</summary>
    public RelayCommand ActivatePanelRowCommand { get; }

    /// <summary>M4: 面板页签（与 Num4/6 换页写同一个 OverlayPanelSection）。</summary>
    public RelayCommand PanelPageCommand { get; }





    public RelayCommand InstallPatchesCommand { get; }

    public RelayCommand RestorePatchesCommand { get; }

    public RelayCommand ReadSelectedUnitCodeCommand { get; }

    /// <summary>只读取证：读出选中单位的对象地址与归属候选字段（心灵控制可行性证据，不写内存）。</summary>
    public RelayCommand ProbeSelectedUnitOwnershipCommand { get; }

    /// <summary>心灵控制（写入）：把当前选中单位变成自己的——复用引擎 0x09 动作，写前落盘快照，写后回读。</summary>
    public RelayCommand ApplyOwnershipTransferCommand { get; }

    /// <summary>
    /// 心灵控制的**键盘快捷键**（与其它功能同一套全局钩子，写法见 <see cref="HotkeyGesture"/>）。
    /// 键位依据（两边都查过，照 references/hotkey-occupancy-check-both-sides.md）：
    /// 修改器 32 条占 Ctrl+F1~F12 / #186~#191 / #219~#222 / Delete / PP / PgUp / PgDn / #106；
    /// 游戏 <c>Profiles\h\hotkeys.ini</c> 的带修饰组合逐条看过（ALT+字母、CTRL+J/K/L/;/R/V/Z、SHIFT+L/S/SPACE/TAB）——
    /// **CTRL+D 两边都没被占**（游戏占的是 ALT+D = 坚守位置）。
    /// </summary>
    internal const string OwnershipHotkey = "Ctrl+D";

    /// <summary>解除心灵控制：把最近一次心灵控制快照里的旧 owner 写回 [U+0x418]。</summary>
    public RelayCommand RestoreOwnershipCommand { get; }

    /// <summary>扫描游戏内存里的单位名表，把隐藏/mod 单位的代号挖出来（只读；需要游戏在跑）。</summary>
    public RelayCommand ScanUnitTypesCommand { get; }

    public FeatureController? FeatureController { get; private set; }

    public bool ArePatchesInstalled
    {
        get => _arePatchesInstalled;
        private set
        {
            _arePatchesInstalled = value;
            OnPropertyChanged();
            RaiseFeatureCommandStates();
        }
    }

    public string LauncherPath
    {
        get => _launcherPath;
        set
        {
            var previousMod = _modIdentity.ModKey;
            _launcherPath = value;
            OnPropertyChanged();
            SyncSelectedLauncher();
            // 启动器换了 ⇒ 重算「当前 mod」（硬信号仍在时硬信号说了算）。
            RefreshModIdentity();
            if (_unitCatalogReady && previousMod != _modIdentity.ModKey)
            {
                ReloadUserUnitCatalog();
            }
        }
    }

    private void ReloadUserUnitCatalog()
    {
        _userUnits.Clear();
        _userUnits.AddRange(ReinforcementUnitCatalog.LoadUserCatalog(_userUnitCatalogPath));
        _userUnits.AddRange(LoadModUserCatalog(
            CurrentModBucketLabel(),
            ReinforcementUnitCatalog.UserCatalogPathForModKey(AppContext.BaseDirectory, _modIdentity.ModKey)));
        ReinforcementUnits.Clear();
        foreach (var entry in ReinforcementUnitCatalog.Merge(_reinforcementUnits, _userUnits))
        {
            ReinforcementUnits.Add(entry);
        }
        // Do not remember the previous mod's currently selected code in the new mod.
        ReinforcementUnitIdText = $"0x{ReinforcementSettings.DefaultUnitId:X8}";
        OnPropertyChanged(nameof(ReinforcementUnitsSummary));
    }

    public string LauncherArguments
    {
        get => _launcherArguments;
        set
        {
            _launcherArguments = value;
            OnPropertyChanged();
        }
    }

    public string ReinforcementUnitIdText
    {
        get => _reinforcementUnitIdText;
        set
        {
            _reinforcementUnitIdText = value;
            OnPropertyChanged();
            SyncSelectedReinforcementUnit();
        }
    }

    /// <summary>可选兵种（来自游戏内置代号表，166 项，带中文名）。</summary>
    public ObservableCollection<ReinforcementUnitEntry> ReinforcementUnits { get; }

    /// <summary>
    /// 找兵/选单位是否放出**其他 mod** 的单位（默认关）。
    /// 关：只显「当前 mod + 原版通用」；开：全部 mod 都显，且每条非原版单位带来源标注（"仅日冕"）。
    /// 认不出当前 mod（没配启动器）时本开关无意义 —— 那本来就是宽松模式、什么都显。
    /// </summary>
    public bool ShowAllModUnits
    {
        get => _showAllModUnits;
        set
        {
            if (_showAllModUnits == value) return;
            _showAllModUnits = value;
            OnPropertyChanged();
        }
    }

    /// <summary>当前 mod 的识别结果（命令行 &gt; 已加载模块 &gt; 启动器配置 &gt; default）。</summary>
    public ModIdentity CurrentMod => _modIdentity;

    /// <summary>给人看的一句：「当前 mod：日冕 / 原版 / 未知（未过滤，避免藏掉你的单位）」。</summary>
    public string CurrentModText => _modIdentity.DisplayText;

    /// <summary>这句话是凭什么认出来的（命令行 / 已加载模块 / 启动器配置 / 无）+ 有没有挖到这个 mod 自带的单位名字表。</summary>
    public string CurrentModEvidenceText
    {
        get
        {
            var evidence = _modIdentity.EvidenceText;
            return _modNameTable.Names.Count > 0
                ? $"{evidence}；已读它自带的单位名字表（{_modNameTable.Names.Count} 条）"
                : evidence;
        }
    }

    /// <summary>
    /// 重算「当前 mod」识别结果并在变化时通知界面。硬信号优先，全缺落回 default（宽松不过滤）。
    /// 触发点：换启动器、检测到/附加到游戏进程、清会话。
    /// </summary>
    public void RefreshModIdentity()
    {
        _modIdentity = ComputeModIdentity();
        RefreshModNameTable();                       // 顺手"扫一眼这个 mod"（读它自带的单位名字表；只读）
        OnPropertyChanged(nameof(CurrentMod));
        OnPropertyChanged(nameof(CurrentModText));
        OnPropertyChanged(nameof(CurrentModEvidenceText));
        OnPropertyChanged(nameof(ReinforcementUnitsSummary));
    }

    /// <summary>取一次信号快照并识别；探测/识别任何异常都不许冒到 UI（落回启动器配置）。</summary>
    private ModIdentity ComputeModIdentity()
    {
        var fallback = new ModSignalSnapshot(LauncherPath, null, Array.Empty<string>());
        ModSignalSnapshot snapshot;
        try
        {
            snapshot = _modSignals(LauncherPath, _targetProcessId) ?? fallback;
        }
        catch
        {
            snapshot = fallback;
        }

        try
        {
            return ModIdentifier.Identify(snapshot, _modSourceAliases, _modFingerprints);
        }
        catch
        {
            return ModIdentifier.Identify(fallback, _modSourceAliases, _modFingerprints);
        }
    }

    /// <summary>
    /// 当前 mod **自己带的**单位名字表（内部名 → 它的显示名）。
    /// ★ 2026-10-11 用户：「如果市面上又出现新的 mod 了，那我们修改器，名字能对上吗？有没有说在我们修改器启动的时候
    /// 会去扫一眼这个 mod？」⇒ 现在**每次识别 mod 时都会去扫一眼**：找它的 skudef → 读 add-big → 在包里挖出
    /// data\gamestrings.csf → 解析出 NAME:&lt;内部名&gt;。结果缓存成 unit-names-&lt;mod&gt;.txt（只读游戏文件，绝不写）。
    /// </summary>
    private ModNameTable _modNameTable = ModNameTable.Empty;

    private string _modNameTableModKey = string.Empty;

    /// <summary>按当前 mod 重新读一遍它自带的单位名字表（换了 mod 才重读；读不到就是空的，不影响任何功能）。</summary>
    private void RefreshModNameTable()
    {
        var modKey = _modIdentity.ModKey ?? string.Empty;
        if (string.Equals(modKey, _modNameTableModKey, StringComparison.OrdinalIgnoreCase)) return;
        _modNameTableModKey = modKey;
        try
        {
            var table = ModNameTableBuilder.LoadOrBuild(modKey, LauncherPath, AppContext.BaseDirectory, out var error);
            _modNameTable = table ?? ModNameTable.Empty;
            _diagnostics.Log.Write("mod-name-table", new
            {
                modKey,
                count = _modNameTable.Names.Count,
                source = _modNameTable.SourcePath,
                error,
            });
            OnPropertyChanged(nameof(CurrentModEvidenceText));
        }
        catch (Exception ex)
        {
            _modNameTable = ModNameTable.Empty;
            _diagnostics.Log.Write("mod-name-table-failed", new { modKey, error = ex.Message });
        }
    }

    /// <summary>当前 mod 的可见范围（由识别结果推；没配/认不出 ⇒ 宽松）。</summary>
    public UnitScope CurrentUnitScope() => UnitModScope.ForModKey(_modIdentity.ModKey, _modSourceAliases);

    /// <summary>附加到目标后重认「当前 mod」：键真变了才重载分档清单（别在附加时白清一次用户选择）。</summary>
    private void RefreshModIdentityAfterAttach()
    {
        var previous = _modIdentity.ModKey;
        RefreshModIdentity();
        if (_unitCatalogReady && previous != _modIdentity.ModKey)
        {
            ReloadUserUnitCatalog();
        }
    }

    /// <summary>
    /// 「本 mod」在单位分类里用的桶名：优先别名表（`remix → REMIX`、`corona → 日冕`），
    /// 认不出就用识别结果自己的显示名（比如 `Remix_0.3.7`）——**再不行才是原始 key**。
    /// 这样别人装了什么 mod，分类里都能看到一个人能看懂的名字，而不是"其他"。
    /// </summary>
    private string CurrentModBucketLabel()
    {
        var label = UnitModScope.LabelFor(_modIdentity.ModKey, _modSourceAliases);
        if (!string.IsNullOrWhiteSpace(label) &&
            !string.Equals(label, _modIdentity.ModKey, StringComparison.OrdinalIgnoreCase))
        {
            return label;
        }

        var display = (_modIdentity.DisplayText ?? string.Empty).Trim();
        return display.Length > 0 ? display : label;
    }

    /// <summary>
    /// 加载「本 mod 的用户单位清单」。★ 加载前先跑一遍**自愈**（用户 2026-10-11：
    /// 「这种东西不应该我们每次去手动处理，应该是修改器自动处理的机制，不然这个修改器我们发布出去之后，
    /// 别人用的时候，那怎么办」）：去重 + 修坏名字（`新单位 0x代号` / 空）+ 清掉空行；
    /// **只有真改过才**备份（`units-xxx.txt.bak-&lt;时间戳&gt;`）并原子写回。名字离线解不出来的原样留着，
    /// 等他下次在游戏里选中该单位时自动补上（见 <see cref="SampleSelectedUnit"/>）。
    /// </summary>
    private IReadOnlyList<ReinforcementUnitEntry> LoadModUserCatalog(string bucketLabel, string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                // mod 升级会把键里的版本号去掉 ⇒ 老清单（units-remix037.txt）自动搬到新名字下，玩家不用重学一遍。
                var sibling = ReinforcementUnitCatalog.FindVersionSiblingCatalog(AppContext.BaseDirectory, _modIdentity.ModKey);
                if (sibling is not null)
                {
                    File.Copy(sibling, path, overwrite: false);
                    _diagnostics.Log.Write("unit-catalog-carried-over", new { from = sibling, to = path });
                }
            }

            if (File.Exists(path))
            {
                var repair = UnitCatalogMaintenance.Normalize(File.ReadAllLines(path), UnitNaming.KnownName);
                if (repair.Changed)
                {
                    var backup = $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss}";
                    File.Copy(path, backup, overwrite: false);
                    var staging = path + ".tmp" + Environment.ProcessId.ToString("X");
                    File.WriteAllLines(staging, repair.Lines, System.Text.Encoding.UTF8);
                    File.Move(staging, path, overwrite: true);
                    _diagnostics.Log.Write("unit-catalog-repaired", new
                    {
                        path,
                        backup,
                        renamed = repair.Renamed,
                        deduped = repair.Deduped,
                        blankLines = repair.Dropped,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            // 自愈失败绝不影响启动/换 mod（用户的清单宁可不动，也不许因此起不来）。
            _diagnostics.Log.Write("unit-catalog-repair-failed", new { path, error = ex.Message });
        }

        return ReinforcementUnitCatalog.StampModCategory(
            ReinforcementUnitCatalog.LoadUserCatalog(path), bucketLabel);
    }

    /// <summary>找兵/选单位真正参与展示的单位：按当前 mod 分层过滤（开了"显示全部"就全放出来）。</summary>
    public IEnumerable<ReinforcementUnitEntry> ScopedReinforcementUnits()
    {
        if (ShowAllModUnits) return ReinforcementUnits;
        var scope = CurrentUnitScope();
        return ReinforcementUnits.Where(unit => scope.Allows(unit));
    }

    /// <summary>阵营的显示名（配置里可改名；没配就是原文）。</summary>
    public string FactionDisplayName(string faction) =>
        _factionDisplay.FirstOrDefault(display => string.Equals(display.Faction, faction, StringComparison.Ordinal))?.Label
        ?? faction;

    /// <summary>当前选中的兵种（界面按名字选；代号只是备注，不再让人手打）。</summary>
    public ReinforcementUnitEntry? SelectedReinforcementUnit
    {
        get => _selectedReinforcementUnit;
        set
        {
            if (ReferenceEquals(_selectedReinforcementUnit, value))
            {
                return;
            }

            _selectedReinforcementUnit = value;
            OnPropertyChanged();

            if (!_syncingReinforcementUnit && value is not null)
            {
                _syncingReinforcementUnit = true;
                try
                {
                    ReinforcementUnitIdText = value.CodeText;
                }
                finally
                {
                    _syncingReinforcementUnit = false;
                }
            }
        }
    }

    /// <summary>代号 → 名字的双向同步：预设/“用游戏里选中的”改代号时，下拉框跟着换名字；
    /// 代号不在清单里就临时加一条“未知单位”，不让人看到空白。</summary>
    private void SyncSelectedReinforcementUnit()
    {
        if (_syncingReinforcementUnit)
        {
            return;
        }

        _syncingReinforcementUnit = true;
        try
        {
            if (UnitCodeParser.TryParse(_reinforcementUnitIdText, out var code))
            {
                var match = ReinforcementUnitCatalog.FindByCode(ReinforcementUnits, code);
                if (match is null)
                {
                    // 清单外的单位（多半是当前 mod 的自研单位）：记住它，下次可以直接在下拉里选。
                    // 注意 mod 之间单位不互通 → 按当前 mod 分档存（units-<mod>.txt），换 mod 不会拿错代号。
                    var modSource = _modIdentity.ModKey;
                    // E：从游戏里学到的单位自动归「本局 / 当前 mod」——来源记成当前 mod 的显示名（日冕）或归一化键，
                    // 这样它**一定在当前作用域内可见**（就是当前 mod 的来源 token）；换到别的 mod 才会被挡。/ F：
                    // 默认自动推导阵营（此处归"当前模组"）；要手动改归属，就编辑 units-<mod>.txt 里的名字前缀（"盟军 · 我的新兵"）。
                    var learnedSource = UnitModScope.LearnedSourceForModKey(_modIdentity.ModKey, _modSourceAliases);
                    match = new ReinforcementUnitEntry(
                        code,
                        $"新单位 {UnitCodeParser.Format(code)}",
                        "当前模组 · 单位",
                        learnedSource);
                    ReinforcementUnits.Add(match);
                    _userUnits.Add(match);
                    try
                    {
                        var modCatalogPath = ReinforcementUnitCatalog.UserCatalogPathForModKey(AppContext.BaseDirectory, _modIdentity.ModKey);
                        ReinforcementUnitCatalog.SaveUserCatalog(modCatalogPath, _userUnits);
                        _diagnostics.Log.Write("unit-remembered", new { code = match.CodeText, path = modCatalogPath, mod = modSource });
                    }
                    catch (Exception ex)
                    {
                        _diagnostics.Log.Write("unit-remember-failed", new { error = ex.Message });
                    }
                }

                _selectedReinforcementUnit = match;
            }
            else
            {
                _selectedReinforcementUnit = null;
            }

            OnPropertyChanged(nameof(SelectedReinforcementUnit));
        }
        finally
        {
            _syncingReinforcementUnit = false;
        }
    }

    public string ReinforcementCountText
    {
        get => _reinforcementCountText;
        set
        {
            _reinforcementCountText = value;
            OnPropertyChanged();
        }
    }

    public string ReinforcementRankText
    {
        get => _reinforcementRankText;
        set
        {
            _reinforcementRankText = value;
            OnPropertyChanged();
        }
    }

    public string PresetNameText
    {
        get => _presetNameText;
        set
        {
            _presetNameText = value;
            OnPropertyChanged();
        }
    }

    public string MoneyAmountText
    {
        get => _moneyAmountText;
        set
        {
            _moneyAmountText = value;
            OnPropertyChanged();
        }
    }

    public string PowerValueText
    {
        get => _powerValueText;
        set
        {
            _powerValueText = value;
            OnPropertyChanged();
        }
    }

    public string ScPointValueText
    {
        get => _scPointValueText;
        set
        {
            _scPointValueText = value;
            OnPropertyChanged();
        }
    }

    public ReinforcementPreset? SelectedReinforcementPreset
    {
        get => _selectedReinforcementPreset;
        set
        {
            _selectedReinforcementPreset = value;
            OnPropertyChanged();
            RaiseCommandStates();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            OnPropertyChanged();
            RaiseCommandStates();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public void RefreshProcess()
    {
        try { DisposeSession(); }
        catch (Exception ex)
        {
            StatusMessage = $"旧会话恢复失败，请先重试「恢复 Patch」。{ex.Message}";
            RefreshFeatureSummary();
            RaiseCommandStates();
            return;
        }
        ConnectionState = ConnectionState.Processing;
        var target = _locator.Find(_manifest.TargetProcess);
        _diagnostics.RecordModuleSnapshot(target?.ProcessId);
        _diagnostics.Log.Write("detect", target is null
            ? new { found = false, targetProcess = _manifest.TargetProcess }
            : new
            {
                found = true,
                processId = target.ProcessId,
                moduleName = target.ProcessName,
                modulePath = target.ModulePath,
                moduleBase = $"0x{target.ModuleBase:X}",
                is32Bit = target.Is32Bit,
                fileVersion = target.FileVersion,
                versionSupported = target.VersionSupported
            });
        if (target is null)
        {
            StatusMessage = $"未找到 {_manifest.TargetProcess}。";
            ConnectionState = ConnectionState.Disconnected;
            RaiseCommandStates();
            return;
        }

        AttachTarget(target, autoInstall: true);
    }

    public async Task LaunchAndLoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ConnectionState = ConnectionState.Processing;
        try
        {
            await LaunchWindowCoordinator.RunAsync(LaunchAndLoadCoreAsync, () => AutoFloatAfterLaunch,
                mode => LaunchWindowModeRequested?.Invoke(mode));
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task<bool> LaunchAndLoadCoreAsync()
    {
        SaveLauncherSettings();

        var launcherName = SelectedLauncher?.DisplayName
            ?? (string.IsNullOrWhiteSpace(LauncherPath) ? "（未设置启动方式）" : Path.GetFileName(LauncherPath));
        var isGameItself = LauncherScanner.Classify(Path.GetFileName(LauncherPath ?? string.Empty))
            == LauncherKind.GameExecutable;

        // 走 mod 启动器时，人还要在启动器里点"开始游戏"，所以等得更久一点
        var waitSeconds = isGameItself
            ? CurrentSettings().AttachTimeoutSeconds
            : Math.Max(CurrentSettings().AttachTimeoutSeconds, 240);

        StatusMessage = $"正在启动：{launcherName}";
        if (string.IsNullOrWhiteSpace(LauncherPath))
        {
            StatusMessage = "先选一个启动器，或点「浏览…」指定程序。";
            return false;
        }

        if (OverlayEnabled) RunOverlay(overlay => overlay.BeforeLaunch(LauncherPath, LauncherCandidates.ToArray()));
        using var launcherProcess = _launcher.Start(LauncherPath, LauncherArguments ?? string.Empty);

        StatusMessage = isGameItself
            ? $"已启动 {launcherName}，等待 {_manifest.TargetProcess}。"
            : $"已启动 {launcherName}：在启动器里点开始游戏后，游戏进程一出现就会自动开启修改（最多等 {waitSeconds} 秒）。";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(waitSeconds + 5));
        var waiter = new GameProcessWaiter(_locator.Find);
        var target = await waiter.WaitForAsync(
            _manifest.TargetProcess,
            TimeSpan.FromSeconds(waitSeconds),
            cancellation.Token);
        if (target is null)
        {
            StatusMessage = $"启动后未找到 {_manifest.TargetProcess}。";
            return false;
        }

        return AttachTarget(target, autoInstall: true);
    }

    public void SaveLauncherSettings()
    {
        try
        {
            _settingsStore.Save(CurrentSettings());
            StatusMessage = "设置已保存。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存启动器路径失败：{ex.Message}";
        }
    }

    private void BrowseLauncherPath()
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择启动方式（mod 启动器 / RA3.exe）",
                Filter = "启动器或游戏 (*.exe)|*.exe|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                LauncherPath = dialog.FileName;
                SaveLauncherSettings();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"选择启动器路径失败：{ex.Message}";
        }
    }

    private bool AttachTarget(TrainerTarget target, bool autoInstall)
    {
        try
        {
            // Launch-and-load can also reach here while an earlier session is attached.
            // Do not lose its hook restoration records or process handles.
            DisposeSession();
            if (target.ProcessId is null)
            {
                throw new InvalidOperationException("无法确定目标进程 PID。");
            }

            _targetProcessId = target.ProcessId;
            _targetModulePath = target.ModulePath;
            // 进程已知 ⇒ 取硬信号（命令行 + 已加载模块）重认「当前 mod」；硬信号压过启动器配置。
            RefreshModIdentityAfterAttach();
            _attachedIdentity = $"{target.ProcessName}; PID={target.ProcessId}; path={target.ModulePath}; version={target.FileVersion}; base=0x{target.ModuleBase:X}; attachedAt={DateTimeOffset.Now:O}";
            try
            {
                _attachedIdentity += $"; fileLastWriteUtc={File.GetLastWriteTimeUtc(target.ModulePath):O}";
                using var image = File.OpenRead(target.ModulePath);
                _attachedIdentity += $"; SHA256={Convert.ToHexString(SHA256.HashData(image))}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { _diagnostics.Log.Write("target-file-time-unavailable", new { error = ex.Message }); }
            _diagnostics.RecordModuleSnapshot(target.ProcessId);
            _targetProcess = Process.GetProcessById(target.ProcessId.Value);
            _diagnostics.RecordActionIdentity(target.ProcessId);
            // Watch from attach onward, including rejected/unpatched sessions.
            _targetExitTimer.Start();
            _memory = _memoryFactory(target.ProcessId.Value);
            _session = new TrainerSession(_manifest, _memory, target, _allocatorFactory(_memory),
                bootstrapLines: TrainerRuntimeAssets.ReadBootstrapLines(), processSuspender: _processSuspender);
            var result = _session.Attach();
            StatusMessage = result.Message;
            _diagnostics.Log.Write("attach", new
            {
                success = result.Success,
                message = result.Message,
                processId = target.ProcessId,
                modulePath = target.ModulePath,
                moduleBase = $"0x{target.ModuleBase:X}",
                fileVersion = target.FileVersion,
                versionSupported = target.VersionSupported
            });
            RefreshDiagnosticsSummary();
            if (result.Success && autoInstall)
            {
                InstallPatches();
            }
            if (result.Success)
            {
                RunOverlay(overlay => overlay.Attach(target.ProcessId.Value, target.FileVersion ?? "未知版本"));
                PublishOverlay();
                _overlayTimer.Start();
                if (!_unitSampleTimer.IsEnabled)
                {
                    _unitSampleTimer.Tick += (_, _) => SampleSelectedUnit();
                    _unitSampleTimer.Start();
                }
            }
            return result.Success && (!autoInstall || (ArePatchesInstalled && FeatureController is not null && _session.CanUseFeatures));
        }
        catch (Exception ex)
        {
            _diagnostics.Log.Write("attach-failed", new { error = ex.Message });
            RefreshRemoteMemorySummary();
            StatusMessage = ex.Message;
            return false;
        }
        finally
        {
            RaiseCommandStates();
        }
    }

    public void InstallPatches()
    {
        if (_session is null)
        {
            StatusMessage = "先点「连接游戏」。";
            return;
        }

        if (FeatureController?.PendingToggleRestoreCount is > 0 || EnabledFeatureCount > 0)
        {
            StatusMessage = "上次的改动还没还原：先点顶栏「一键全撤」，再重新开启。";
            return;
        }
        if (_session.InstalledHookCount > 0)
        {
            StatusMessage = "修改已经开着：先点「一键全撤」，再重新开启。";
            return;
        }

        try
        {
            if (_inspectHooksBeforeInstall)
            {
                var report = _session.InspectHookSites();
                _diagnostics.RecordHookReport(report);
                HookSummary = report.Summary;
                if (report.AllUnexpected)
                {
                    StatusMessage =
                        $"已拒绝安装：{report.Summary}。目标代码与 patch 清单完全不符（主程序被换过或版本不同），不盲写内存。";
                    return;
                }

                if (report.UnexpectedCount > 0)
                {
                    StatusMessage = $"注意：{report.Summary}；按你的要求继续安装，已记入日志。";
                }
            }

            var resourceValues = GetResourceValueSettings();
            StopProbe("重新装载远程代码");
            _session.InstallPatches();
            if (_session.Resolver is not null && _memory is not null)
            {
                FeatureController = new FeatureController(_memory, _session.Resolver);
                FeatureController.AttachSessionId = _diagnostics.ActionIdentity?.AttachSessionId;
                FeatureController.EvidenceSink = evidence => _diagnostics.Log.Write("action-evidence", evidence);
                FeatureController.SafetyMessage += message =>
                    _diagnostics.Log.Write("feature-validation", new { message });
                FeatureController.WriteResourceValues(resourceValues);
            }
            ArePatchesInstalled = true;
            ConnectionState = ConnectionState.Connected;
            StartHotkeys();
            _diagnostics.Log.Write("install", new
            {
                hookCount = _session.InstalledHookCount,
                expectedHooks = _manifest.PatchManifest.Hooks.Count,
                symbols = _session.RemoteSymbolSummary
            });
            _targetExitTimer.Start();
            RefreshDiagnosticsSummary();
            // 状态行说人话（技术细节 hookCount/expectedHooks/symbols 已经写进上面那条日志了）。
            StatusMessage = $"修改已开启，快捷键可用（共启用 {_session.InstalledHookCount} 项）。";
            ReadSelectedUnitCodeCommand.RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            StopHotkeys();
            ArePatchesInstalled = false;
            ConnectionState = ConnectionState.Disconnected;
            var remainingHooks = _session.InstalledHookCount;
            _diagnostics.Log.Write("install-failed", new { error = ex.Message, remainingHooks });
            HookSummary = $"装载失败；残留 Hook={remainingHooks}";
            RefreshRemoteMemorySummary();
            StatusMessage = remainingHooks > 0
                ? $"装载未完成，仍有 {remainingHooks} 个 Hook；请点击「恢复 Patch」后重试。{ex.Message}"
                : $"装载失败，未残留 Hook。{ex.Message}";
            RefreshFeatureSummary();
        }
        finally
        {
            RaiseCommandStates();
        }
    }

    public void RestorePatches()
    {
        _overlayEnabled = false;
        RunOverlay(overlay => overlay.Stop());
        OnPropertyChanged(nameof(OverlayEnabled));
        OnPropertyChanged(nameof(OverlayStatus));
        CancelSessionActions();
        StopHotkeys();
        ArePatchesInstalled = false;
        ConnectionState = ConnectionState.Disconnected;
        try
        {
            RestoreAllSessionChanges();
            FeatureController?.Stop();
            FeatureController = null;
            ResetFeatureStates();
            _diagnostics.Log.Write("restore", new { reason = "user" });
            HookSummary = "Hook 已恢复。";
            StatusMessage = "已还原。游戏内面板会消失；重启游戏后彻底干净。";
        }
        catch (Exception ex)
        {
            var remainingHooks = _session?.InstalledHookCount ?? 0;
            var pendingToggleRestores = FeatureController?.PendingToggleRestoreCount ?? 0;
            HookSummary = $"待恢复：Hook={remainingHooks}；开关字节范围={pendingToggleRestores}";
            StatusMessage = $"恢复未完成，记录已保留，请重试「恢复 Patch」。{ex.Message}";
            _diagnostics.Log.Write("restore-failed", new { error = ex.Message, remainingHooks, pendingToggleRestores });
        }
        finally
        {
            RefreshRemoteMemorySummary();
            RefreshFeatureSummary();
            RaiseCommandStates();
        }
    }

    public ReinforcementSettings GetReinforcementSettings()
    {
        return ReinforcementSettings.Parse(
            ReinforcementUnitIdText,
            ReinforcementCountText,
            ReinforcementRankText);
    }

    public ResourceValueSettings GetResourceValueSettings()
    {
        return ResourceValueSettings.Parse(MoneyAmountText, PowerValueText, ScPointValueText);
    }

    public void WriteResourceValuesIfNeeded(TrainerFeature feature)
    {
        if (!IsResourceValueFeature(feature) || FeatureController is null)
        {
            return;
        }

        FeatureController.WriteResourceValues(GetResourceValueSettings());
    }

    /// <summary>
    /// 只读诊断：扫游戏内存里出现的"单位内部名"字符串，再顺着指向它的指针读出附近 dword，
    /// 用已知代号当场校准（★），用来把隐藏单位（将军刽子手 / JapanMechaKing）和日冕自研单位的代号挖出来。
    /// 结果写到 exe 旁边的 unit-type-scan.txt。
    /// </summary>
    public async Task ScanUnitTypesAsync()
    {
        if (_isScanningUnits || _targetProcessId is not int processId) return;
        var token = SessionCancellation;
        _isScanningUnits = true;
        ScanUnitTypesCommand.RaiseCanExecuteChanged();
        StatusMessage = "正在读取游戏里的单位名（只读，可能要十几秒）…";
        try
        {
            // The worker owns its independent read-only handle until scanning exits.
            var lines = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using var memory = new Win32ProcessMemory(processId, readOnly: true);
                var units = UnitInternalNameCatalog.LoadBuiltIn();
                var scanner = new UnitTypeNameScanner(memory, memory);
                return scanner.Scan(units.Select(unit => unit.InternalName),
                    UnitInternalNameCatalog.KnownCodes(), cancellationToken: token);
            }, token);
            token.ThrowIfCancellationRequested();
            var path = TrainerPaths.Resolve(AppContext.BaseDirectory, "unit-type-scan.txt");
            await File.WriteAllLinesAsync(path, lines, System.Text.Encoding.UTF8, token);
            token.ThrowIfCancellationRequested();
            var hitLine = lines.FirstOrDefault() ?? string.Empty;
            var starred = lines.Count(line => line.StartsWith('★'));
            StatusMessage = $"扫描完成：{hitLine}；校准命中 {starred} 处 → 结果写到 {path}";
            _diagnostics.Log.Write("unit-type-name-scan", new { summary = hitLine, starred, path, lines = lines.Take(400).ToArray() });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The new session owns the status text now.
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) StatusMessage = $"扫描单位名表失败：{ex.Message}";
        }
        finally
        {
            _isScanningUnits = false;
            ScanUnitTypesCommand.RaiseCanExecuteChanged();
        }
    }
    /// <summary>
    /// 你在游戏里选中/用到过的单位，会被默默记进**当前 mod** 的单位库 units-&lt;mod&gt;.txt。
    /// 内置库（日冕官网 630 条 + 人工 84 条）里已有的不重复记；只记 mod 独有的新单位。
    /// </summary>
    private void SampleSelectedUnit()
    {
        if (FeatureController is null) return;
        uint code;
        try { code = FeatureController.ReadSelectedUnitCode(); }
        catch { return; }                                              // 没进游戏/读不到：安静跳过，不打扰
        if (code == 0) return;
        if (ReinforcementUnits.Any(unit => unit.Code == code)) return;  // 内置/已有 ⇒ 不用记
        if (_sampledUnits.Contains(code)) return;

        // 名字从**选中对象自己的模板**里读（只读）：内部名 → 内置表中文名 → 内部名 → 「未命名」。
        // 早先我写成 "新单位 0x代号"，结果代号被当成名字抄了第二遍（units-corona.txt 里那 7 条就是这个坏格式，
        // 同样的错犯过两次）；只写「未命名」又等于没信息 ⇒ 2026-10-11 用户："能不能用模板里的内部名去查中文名？"
        string? internalName = null;
        try { internalName = FeatureController?.TryReadSelectedUnitInternalName(); }
        catch { /* 只读路径自己不抛；这里再兜一层，绝不影响记录流程 */ }
        var displayName = UnitNaming.DisplayNameFor(internalName, _modNameTable.Names);

        var path = ReinforcementUnitCatalog.UserCatalogPathForMod(AppContext.BaseDirectory, LauncherPath);
        var placeholder = $"0x{code:X8} {UnitNaming.UnknownName}";
        var line = $"0x{code:X8} {displayName}";
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllLines(path);
                if (existing.Any(l => string.Equals(l.Trim(), line, StringComparison.OrdinalIgnoreCase)))
                {
                    return;                                       // 已经有这条（同样名字）⇒ 不重复写
                }

                // 老记录是「未命名」占位、这次终于查到了真名字 ⇒ **只替换那一行**（其余一行不动，原子写）。
                if (displayName != UnitNaming.UnknownName)
                {
                    var index = Array.FindIndex(existing, l => string.Equals(l.Trim(), placeholder, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0)
                    {
                        existing[index] = line;
                        var staging = path + ".tmp" + Environment.ProcessId.ToString("X");
                        File.WriteAllLines(staging, existing, System.Text.Encoding.UTF8);
                        File.Move(staging, path, overwrite: true);
                        _sampledUnits.Add(code);
                        SetPanelResult($"补上名字 0x{code:X8} → {displayName} ✓");
                        return;
                    }

                    if (existing.Any(l => l.TrimStart().StartsWith($"0x{code:X8}", StringComparison.OrdinalIgnoreCase)))
                    {
                        return;                                   // 已有该代号（名字他自己改过）⇒ 绝不覆盖用户的字
                    }
                }
                else if (existing.Any(l => l.TrimStart().StartsWith($"0x{code:X8}", StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }
            }

            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (Exception ex) { SetPanelResult($"采样落盘失败：{ex.Message}"); return; }

        _sampledUnits.Add(code);
        SetPanelResult($"自动记录 0x{code:X8} {displayName} ✓ 本 mod 已记 {_sampledUnits.Count} 条");
    }

    public void ReadSelectedUnitCode()
    {
        if (FeatureController is null)
        {
            StatusMessage = "先点「连接游戏」，再点「开启修改」。";
            return;
        }

        try
        {
            var unitCode = FeatureController.ReadSelectedUnitCode();
            ReinforcementUnitIdText = $"0x{unitCode:X8}";
            StatusMessage = $"已读取选中单位：{ReinforcementUnitIdText}";

            // 诊断：顺便在附近找对象名指针，攒够数据就能把 mod 单位也自动翻译成中文名（只读）
            var candidates = FeatureController.ProbeSelectedUnitNameCandidates();
            _diagnostics.Log.Write("selected-unit-name-probe", new { code = ReinforcementUnitIdText, candidates });
            if (candidates.Count > 0)
            {
                StatusMessage += $"（名字候选 {candidates.Count} 个，已记入日志）";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"读取选中单位代码失败：{ex.Message}";
        }
    }

    /// <summary>
    /// “心灵控制（测试）”取证第一步：只读读出当前选中单位的对象地址、类型码、归属候选 [U+0x418]
    /// 与本机玩家指针 [ID] 的对照，并把快照（含时间戳与对象地址）落盘到 exe 旁边。
    /// 只读：独立只读句柄（PROCESS_QUERY_INFORMATION|PROCESS_VM_READ），不写内存、不抢焦点、不注入键鼠。
    /// 写入与一键还原尚未启用（[U+0x418] 未证实可安全写；理由见 docs/handoff 里的心灵控制报告）。
    /// </summary>
    private void ProbeSelectedUnitOwnership()
    {
        if (_session?.Resolver is not AddressResolver resolver || _targetProcessId is not int processId)
        {
            StatusMessage = "先点「连接游戏」。";
            return;
        }
        var probeMemory = _probeMemoryFactory(processId);
        try
        {
            var snapshot = new SelectedUnitOwnershipProbe(probeMemory, resolver).Probe(ArePatchesInstalled);
            OwnershipProbeText = DescribeOwnership(snapshot);
            var path = WriteOwnershipSnapshot(snapshot);
            _diagnostics.Log.Write("duoshe-probe", new
            {
                readOnly = true, path, snapshot.Summary,
                unit = snapshot.UnitAddress?.ToString("X"),
                type = snapshot.TypeCode?.ToString("X8"),
                blacklisted = snapshot.TypeInTransferBlacklist,
                owner418 = snapshot.OwnerCandidate?.ToString("X8"),
                localPlayer = snapshot.LocalPlayerPointer?.ToString("X8"),
                ownerIsLocal = snapshot.OwnerEqualsLocalPlayer, snapshot.SelectionCount
            });
            StatusMessage = snapshot.UnitAddress is null
                ? "未取到选中单位对象：先在游戏里选中一个单位再点。（只读，未写内存）"
                : $"已读出选中单位归属候选（只读取证）：{path}";
        }
        catch (Exception ex)
        {
            OwnershipProbeText = "读取失败：" + ex.Message;
            StatusMessage = "归属字段读取失败：" + ex.Message;
        }
        finally
        {
            (probeMemory as IDisposable)?.Dispose();
        }
    }

    private static string DescribeOwnership(SelectedUnitOwnershipSnapshot s)
    {
        var lines = new List<string>
        {
            $"状态：{s.SelectionState}（count={s.SelectionCount}）",
            $"对象 U：0x{(s.UnitAddress is nint u ? ((ulong)u).ToString("X") : "--------")}",
            $"类型码：0x{(s.TypeCode is uint t ? t.ToString("X8") : "--------")}{(s.TypeInTransferBlacklist ? "  ← 引擎转让黑名单（引擎会跳过不转让）" : "")}",
            $"归属候选 [U+0x418]：0x{(s.OwnerCandidate is uint o ? o.ToString("X8") : "不可读")}",
            $"本机玩家 [ID]：0x{(s.LocalPlayerPointer is uint p ? p.ToString("X8") : "未定位（未附加或无选中）")}",
            s.OwnerEqualsLocalPlayer ? "判定：已归属你（等于本机玩家指针）" : "判定：不等于本机玩家指针（非你所有，或无法判等）",
            "本原型只读，未写内存——写入与一键还原尚未启用。"
        };
        return string.Join("\n", lines);
    }

    private static string WriteOwnershipSnapshot(SelectedUnitOwnershipSnapshot s)
    {
        var path = Path.Combine(TrainerPaths.DataRoot, $"duoshe-probe-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        var payload = new
        {
            ts = s.Timestamp.ToString("o"),
            readOnly = true,
            note = "心灵控制取证快照；本原型只读，未写游戏内存",
            selectionState = s.SelectionState,
            selectionCount = s.SelectionCount,
            selectionRoot = s.SelectionRoot?.ToString("X"),
            unit = s.UnitAddress?.ToString("X"),
            typeCode = s.TypeCode?.ToString("X8"),
            typeInTransferBlacklist = s.TypeInTransferBlacklist,
            ownerCandidateAddress = s.OwnerCandidateAddress?.ToString("X"),
            ownerCandidate = s.OwnerCandidate?.ToString("X8"),
            localPlayer = s.LocalPlayerPointer?.ToString("X8"),
            ownerEqualsLocalPlayer = s.OwnerEqualsLocalPlayer,
            window = s.Window.Select(f => new { f.Offset, f.Name, f.Status, value = f.Value?.ToString("X8") }).ToArray()
        };
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(payload,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>
    /// 【心灵控制】把当前选中单位变成自己的 —— **首选路线**：复用引擎自己那段已验证的代码
    /// （<c>Assets/00_bootstrap.aa:966-997</c>，动作码 0x09 `Select Unit Change ID`：
    /// 把选中单位的 [U+0x418] 写成本机玩家 [ID]，带 6 型类型黑名单）。
    /// 我们**不裸写指针**：改为把 0x09 写进动作槽（`iEnable+20`），由游戏线程按引擎逻辑执行。
    /// 流程：高风险闸门 → 只读判定 → 写前落盘快照 → 派发引擎动作 → 写后回读显示「改前→改后」→ 提供「解除心灵控制」。
    /// </summary>
    private async Task ApplyOwnershipTransferAsync()
    {
        if (_session?.Resolver is not AddressResolver resolver || _targetProcessId is not int processId)
        {
            StatusMessage = "先点「连接游戏」。";
            PublishOwnershipToPanel("心灵控制不可用：先点「连接游戏」");
            return;
        }
        var controller = FeatureController;
        var changeIdFeature = FindUiFeature("Select Unit Change ID");
        if (controller is null || changeIdFeature is null)
        {
            StatusMessage = "心灵控制不可用：先点「连接游戏」。";
            PublishOwnershipToPanel("心灵控制不可用：功能清单里没有「Select Unit Change ID」");
            return;
        }
        var probeMemory = _probeMemoryFactory(processId);
        try
        {
            // 闸门：默认关闭，位于「诊断」页「高风险功能」勾选框之后（未开启时这里抛，一个字都不写）。
            FeatureSafetyPolicy.Validate(changeIdFeature, true, AllowUnverifiedWorldPositionFeatures);

            var plan = new SelectedUnitOwnershipTransfer(probeMemory, resolver).Plan(ArePatchesInstalled);
            if (!plan.CanTransfer)
            {
                OwnershipTransferText = DescribeTransferPlan(plan);
                StatusMessage = "心灵控制未执行：" + plan.Reason;
                PublishOwnershipToPanel("心灵控制未执行：" + plan.Reason);
                return;
            }

            // ① 写前落盘快照：对象地址 U、旧 owner、新值、时间戳。
            var snapshot = OwnershipApplySnapshot.FromPlan(plan, DateTimeOffset.Now);
            var snapshotPath = OwnershipApplySnapshotStore.Write(OwnershipSnapshotDirectory, snapshot);
            OwnershipTransferText =
                $"快照已落盘：{snapshotPath}\n" +
                $"对象 U={snapshot.Unit}  类型={snapshot.TypeCode}  owner 地址={snapshot.OwnerAddress}\n" +
                $"改前 owner={snapshot.OldOwner}  →  目标 owner（本机玩家 [ID]）={snapshot.NewOwner}\n" +
                "正在派发引擎动作 0x09（Select Unit Change ID）……";

            // ② 走引擎自己的动作：0x09 进动作槽，游戏侧 MustCode2+800 执行 [U+0x418] = [ID]。
            //    ⚠ 真机实测：动作槽只在游戏"资金更新"那条钩子上被轮询，消费**可能晚于**修改器的等待窗口
            //    （2026-10-07 22:21 实测：等待 1500ms 报 TimedOut，之后游戏才执行，owner 确实变了）。
            //    所以：等待给足；**超时不当失败**，随后用"回读 owner"做延迟确认。
            var result = await controller.TriggerActionAndWaitForConsumptionAsync(
                changeIdFeature,
                timeout: OwnershipDispatchTimeout,
                validateBeforeDispatch: () => FeatureSafetyPolicy.Validate(changeIdFeature, true, AllowUnverifiedWorldPositionFeatures),
                requestId: Guid.NewGuid().ToString("N")).ConfigureAwait(true);
            _lastOwnershipApply = snapshot;

            // ③ 写后回读（带延迟确认）：owner 才是地面真相。
            var transfer = new SelectedUnitOwnershipTransfer(probeMemory, resolver);
            var confirmed = false;
            var waitedMs = 0;
            var deadline = DateTimeOffset.Now + OwnershipConfirmWindow;
            while (true)
            {
                if (plan.OwnerAddress is nint ownerAddress && plan.NewOwner is uint expected &&
                    transfer.ReadOwner(ownerAddress) == expected)
                {
                    confirmed = true;
                    break;
                }
                if (DateTimeOffset.Now >= deadline) break;
                await Task.Delay(OwnershipConfirmPollInterval).ConfigureAwait(true);
                waitedMs += (int)OwnershipConfirmPollInterval.TotalMilliseconds;
            }

            var afterPlan = transfer.Plan(ArePatchesInstalled);
            var afterOwner = afterPlan.OldOwner;
            var afterText = afterOwner is uint a4v ? $"0x{a4v:X8}" : "不可读";
            var slot = controller.ReadActionDispatch();
            // 「超时 ≠ 失败」的口径在这里落地（纯函数，有单测）：owner 回读为准，动作槽只用来区分"还没轮到"和"真的没生效"。
            var verdict = OwnershipWriteConfirmation.Classify(confirmed, slot);
            var verdictText = verdict switch
            {
                OwnershipWriteVerdict.Confirmed => $"（已归属你 ✓；游戏约 {waitedMs / 1000.0:0.#} 秒后执行）",
                OwnershipWriteVerdict.PendingGamePolling =>
                    "（已派发，但游戏还没轮到它：动作槽仍被占着 —— 这期间**其它动作会被挡住**；" +
                    "等游戏消费后再点「读出选中单位归属」复核，**不需要重复点心灵控制**）",
                _ => "（动作槽已被游戏消费，但归属未变：引擎跳过了它，或该对象已被换掉/销毁）"
            };

            // ③′ 账本条目 [U+0x414]：只写 owner 时被心灵控制的建筑会**卡住、不再出单位**；
            //     把条目也写成用户的（= [本机玩家+0xAC]，三次真机验证），建筑继续出单位且产出归用户
            //     （2026-10-08 真机三步确认）。**纯写入**：不走引擎、不调任何游戏函数 ⇒ 无上次那种崩溃风险；
            //     同样有"当前值必须等于预期值"的身份校验 + 写后回读（见 FeatureController.WriteSelectedUnitEntry）。
            var entryText = "条目：本次无可写项（本局条目未取到或字段已一致）";
            if (plan.EntryAddress is nint entryAddress && plan.NewEntry is uint entryTargetValue && entryTargetValue != 0)
            {
                var entryBefore = transfer.ReadOwner(entryAddress);
                if (entryBefore is uint expectedEntry)
                {
                    if (expectedEntry == entryTargetValue)
                    {
                        entryText = $"条目已是 0x{expectedEntry:X8}，无需写入";
                    }
                    else
                    {
                        var entryOutcome = controller.WriteSelectedUnitEntry(entryAddress, expectedEntry, entryTargetValue);
                        entryText = $"条目 改前 0x{entryOutcome.Before:X8} → 改后 0x{entryOutcome.After:X8}"
                            + (entryOutcome.Wrote ? "（已写入 ✓）" : $"（未写入：{entryOutcome.Note}）");
                    }
                }
                else entryText = "条目不可读，未写入（只写了 owner）";
            }
            else entryText = "条目目标未取到（本机玩家条目 [ID+0xAC] 读不到）⇒ 只写了 owner，建筑可能不出单位";

            OwnershipTransferText =
                $"快照：{snapshotPath}\n" +
                $"对象 U={snapshot.Unit}  类型={snapshot.TypeCode}\n" +
                $"owner 改前 {snapshot.OldOwner}  →  改后 {afterText} {verdictText}\n" +
                $"{entryText}\n" +
                $"动作槽：{(slot == 0 ? "已归零" : $"仍为 0x{slot:X2}")}（等待协议结果：{result}）\n" +
                "如需恢复，点「解除心灵控制」——用上面快照把旧 owner 写回。";
            StatusMessage = verdict == OwnershipWriteVerdict.Confirmed
                ? $"心灵控制成功：{snapshot.OldOwner} → {afterText}；快照 {snapshotPath}"
                : $"心灵控制已派发但尚未确认：{snapshot.OldOwner} → {afterText}；快照 {snapshotPath}";
            PublishOwnershipToPanel(verdict switch
            {
                OwnershipWriteVerdict.Confirmed => $"心灵控制成功：单位已归你（游戏约 {waitedMs / 1000.0:0.#} 秒后执行）",
                OwnershipWriteVerdict.PendingGamePolling => "心灵控制已派发，游戏还没轮到（别重复点）",
                _ => "心灵控制没生效：动作槽已消费但归属未变（引擎跳过或对象已换）"
            });
            _diagnostics.Log.Write("duoshe-apply", new
            {
                path = snapshotPath, unit = snapshot.Unit, type = snapshot.TypeCode,
                ownerAddress = snapshot.OwnerAddress, oldOwner = snapshot.OldOwner, newOwner = snapshot.NewOwner,
                afterOwner = afterOwner is uint a4 ? $"0x{a4:X8}" : null,
                confirmed = verdict == OwnershipWriteVerdict.Confirmed, verdict = verdict.ToString(),
                waitedMs, slotAfterDispatch = $"0x{slot:X2}", result = result.ToString(),
                entryAddress = snapshot.EntryAddress, oldEntry = snapshot.OldEntry, newEntry = snapshot.NewEntry,
                entryResult = entryText
            });
        }
        catch (Exception ex)
        {
            OwnershipTransferText = "心灵控制未完成：" + ex.Message;
            PublishOwnershipToPanel("心灵控制失败：" + ex.Message);
            StatusMessage = "心灵控制失败：" + ex.Message;
        }
        finally
        {
            (probeMemory as IDisposable)?.Dispose();
            RestoreOwnershipCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 解除心灵控制：把最近一次心灵控制快照里的旧 owner 写回 [U+0x418]（<see cref="FeatureController.RestoreSelectedUnitOwnership"/>）。
    /// 只有该字段当前仍等于快照记录的新值时才会写（否则拒绝，绝不动已换代/别人的对象）。与写入共用同一道高风险闸门。
    /// </summary>
    private void RestoreOwnership()
    {
        if (_targetProcessId is not int)
        {
            StatusMessage = "先点「连接游戏」。";
            PublishOwnershipToPanel("还原不可用：先点「连接游戏」");
            return;
        }
        var controller = FeatureController;
        var changeIdFeature = FindUiFeature("Select Unit Change ID");
        var snapshot = _lastOwnershipApply;
        if (controller is null || snapshot is null)
        {
            StatusMessage = "没有可还原的心灵控制快照：先做一次心灵控制（写入前会落盘快照）。";
            PublishOwnershipToPanel("还原不可用：还没有心灵控制快照（先控制一次）");
            return;
        }
        try
        {
            var target = snapshot.ToRestoreTarget();
            // 与写入同一道闸门（还原也是对象级写）。
            if (changeIdFeature is not null)
                FeatureSafetyPolicy.Validate(changeIdFeature, true, AllowUnverifiedWorldPositionFeatures);

            // 身份校验（当前值必须仍是我们写进去的新值）+ 写后读回 + 失败回滚都在这一条里面。
            var outcome = controller.RestoreSelectedUnitOwnership(
                target.OwnerAddress, target.ExpectedCurrent, target.RestoreValue);

            // 账本条目一起还原（2026-10-08 起写入）。旧快照没有这组字段 ⇒ 这里为 null，只还原 owner（向后兼容）。
            var entryRestoreNote = "条目：本次快照没有条目字段（旧快照或条目未取到）⇒ 只还原了 owner";
            if (target.EntryAddress is nint entryAddress
                && target.EntryExpectedCurrent is uint entryExpected
                && target.EntryRestoreValue is uint entryRestore)
            {
                try
                {
                    var entryBack = controller.WriteSelectedUnitEntry(entryAddress, entryExpected, entryRestore);
                    entryRestoreNote = $"条目 0x{entryBack.Before:X8} → 0x{entryBack.After:X8}"
                        + (entryBack.Wrote ? "（已写回 ✓）" : $"（未写入：{entryBack.Note}）");
                }
                catch (Exception ex)
                {
                    entryRestoreNote = "条目还原失败（owner 已还原）：" + ex.Message;
                }
            }

            OwnershipTransferText =
                $"解除心灵控制（快照 {snapshot.Timestamp}）\n" +
                $"owner 地址 0x{(ulong)outcome.OwnerAddress:X}：0x{outcome.Before:X8} → 0x{outcome.After:X8}" +
                (outcome.Wrote ? "（已写回快照里的旧 owner ✓）" : $"（未写入：{outcome.Note}）") + "\n" +
                entryRestoreNote;
            _lastOwnershipApply = null;
            StatusMessage = outcome.Wrote ? "归属已还原：1 个单位" : "归属无需还原：" + outcome.Note;
            PublishOwnershipToPanel(outcome.Wrote ? "归属已还原 1 个单位" : "归属无需还原：" + outcome.Note);
            _diagnostics.Log.Write("duoshe-restore", new
            {
                count = 1,
                stamp = snapshot.Timestamp,
                ownerAddress = $"0x{(ulong)outcome.OwnerAddress:X}",
                before = $"0x{outcome.Before:X8}",
                after = $"0x{outcome.After:X8}",
                wrote = outcome.Wrote,
                note = outcome.Note,
                entryAddress = snapshot.EntryAddress, oldEntry = snapshot.OldEntry, newEntry = snapshot.NewEntry,
                entryRestore = entryRestoreNote
            });
        }
        catch (Exception ex)
        {
            OwnershipTransferText = "还原未完成：" + ex.Message;
            StatusMessage = "还原失败：" + ex.Message;
            PublishOwnershipToPanel("还原失败：" + ex.Message);
        }
        finally
        {
            RestoreOwnershipCommand.RaiseCanExecuteChanged();
        }
    }

    private static string DescribeTransferPlan(OwnershipTransferPlan plan)
    {
        return string.Join("\n", new[]
        {
            $"状态：{plan.SelectionState}（count={plan.SelectionCount}）",
            $"对象 U：0x{(plan.UnitAddress is nint u ? ((ulong)u).ToString("X") : "--------")}",
            $"类型码：0x{(plan.TypeCode is uint t ? t.ToString("X8") : "--------")}{(plan.TypeInTransferBlacklist ? "  ← 引擎转让黑名单（跳过不转让）" : "")}",
            $"owner 地址：0x{(plan.OwnerAddress is nint oa ? ((ulong)oa).ToString("X") : "--------")}",
            $"旧 owner：0x{(plan.OldOwner is uint o ? o.ToString("X8") : "不可读")}　本机玩家 [ID]：0x{(plan.NewOwner is uint nw ? nw.ToString("X8") : "未定位")}",
            plan.Reason
        });
    }

    public void OpenReinforcementUnitPicker()
    {
        try
        {
            var picker = new ReinforcementUnitPickerViewModel(_reinforcementUnits, CurrentUnitScope(), ShowAllModUnits);
            var window = new ReinforcementUnitPickerWindow
            {
                Owner = Application.Current?.MainWindow,
                DataContext = picker
            };

            var result = window.ShowWithOwnerHidden();
            _reinforcementUnits = picker.Units.ToArray();

            if (result == true && picker.SelectedUnit is not null)
            {
                ReinforcementUnitIdText = picker.SelectedUnit.CodeText;
                StatusMessage = $"已选择单位：{picker.SelectedUnit.Name} ({picker.SelectedUnit.CodeText})";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开单位列表失败：{ex.Message}";
        }
    }

    private void SaveReinforcementPreset()
    {
        try
        {
            var settings = GetReinforcementSettings();
            PresetNameText = $"{SelectedReinforcementUnit?.Name ?? ReinforcementUnitIdText}×{settings.Count} / {settings.Rank}星";
            var preset = new ReinforcementPreset(PresetNameText, settings.UnitId, settings.Count, settings.Rank);
            var existingIndex = IndexOfPreset(preset.Name);
            if (existingIndex >= 0)
            {
                ReinforcementPresets[existingIndex] = preset;
            }
            else
            {
                ReinforcementPresets.Add(preset);
            }

            SelectedReinforcementPreset = preset;
            PersistSettings();
            StatusMessage = $"已保存增援预设：{preset.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存增援预设失败：{ex.Message}";
        }
    }

    private void ApplySelectedReinforcementPreset()
    {
        if (SelectedReinforcementPreset is null)
        {
            return;
        }

        ApplyPreset(SelectedReinforcementPreset);
        StatusMessage = $"已应用增援预设：{SelectedReinforcementPreset.Name}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _diagnostics.Log.Write("shutdown", new { reason = "app-exit" });
        DisposeSession();
        RunOverlay(overlay => overlay.StartCleanupWorker());
        _targetExitTimer.Tick -= OnTargetExitTimerTick;
        _probeTimer.Tick -= OnProbeTick;
        _keyboardHook.KeyDown -= OnKeyboardHookKeyDown;
        _keyboardHook.KeyUp -= OnKeyboardHookKeyUp;
        PropertyChanged -= OnPanelSourceChanged;
        Navigator.PropertyChanged -= OnNavigatorChanged;
        foreach (var item in Groups.SelectMany(group => group.Features)) item.PropertyChanged -= OnFeatureChanged;
        _sessionCancellation.Cancel();
        _sessionCancellation.Dispose();
        _diagnostics.Dispose();
        _disposed = true;
    }

    private void DisposeSession()
    {
        _overlayTimer.Stop();
        RunOverlay(overlay => overlay.Disconnect());
        StopProbe("断开连接");
        _targetExitTimer.Stop();
        CancelSessionActions();
        StopHotkeys();
        ArePatchesInstalled = false;
        ConnectionState = ConnectionState.Disconnected;
        RestoreAllSessionChanges();
        FeatureController?.Stop();
        FeatureController = null;
        ResetFeatureStates();
        RefreshRemoteMemorySummary();
        if (_session is not null) _diagnostics.Log.Write("remote-memory-retired", _session.GetDiagnosticSnapshot());
        _session = null;
        (_memory as IDisposable)?.Dispose();
        _memory = null;
        _targetProcess?.Dispose();
        _targetProcess = null;
        _targetProcessId = null;
        _targetModulePath = null;
        // 没进程了 ⇒ 硬信号消失，识别落回启动器配置/default（宽容）。
        RefreshModIdentity();
        RefreshDiagnosticsSummary();
    }

    private void RestorePendingToggleBytes()
    {
        // A dead process has no address space to restore. Otherwise keep the controller
        // and its rollback records if retry fails; never replace them with a fresh baseline.
        if (_targetProcess is null || !_targetProcess.HasExited)
            FeatureController?.RetryPendingToggleRestores();
    }

    private void RestoreAllSessionChanges()
    {
        StopProbe("恢复／释放远程块");
        var errors = new List<Exception>();
        FeatureController?.QuiesceActions();
        try
        {
            if (_targetProcess?.HasExited == true)
                _session?.ForgetExitedProcess();
            else
            {
                try { RestorePendingToggleBytes(); }
                catch (Exception ex) { errors.Add(ex); }
                foreach (var item in Groups.SelectMany(group => group.Features).Where(item => item.IsToggle && item.IsActive))
                {
                    try
                    {
                        FeatureController?.SetToggle(item.Feature, false);
                        item.ResetSessionState();
                    }
                    catch (Exception ex) { errors.Add(ex); }
                }
            }
        }
        catch (Exception ex) { errors.Add(ex); }
        // Even a failed toggle restore must not prevent independently restoring hook sites.
        try { _session?.Restore(releaseAllocations: errors.Count == 0); }
        catch (Exception ex) { errors.Add(ex); }
        if (errors.Count > 0)
        {
            _diagnostics.Log.Write("restore-context", CaptureContext("一键全撤未完成"));
            throw new AggregateException("部分修改恢复失败，记录已保留；请重试一键全撤。", errors);
        }
    }

    private void CancelSessionActions()
    {
        _sessionCancellation.Cancel();
        _sessionCancellation.Dispose();
        _sessionCancellation = new CancellationTokenSource();
    }

    private void ResetFeatureStates()
    {
        foreach (var item in Groups.SelectMany(group => group.Features))
        {
            item.ResetSessionState();
        }
        RefreshFeatureSummary();
    }


    private string _featureFilter = string.Empty;

    /// <summary>功能清单筛选词（按显示名/热键/原始名匹配），29 个功能不用肉眼扫。</summary>
    public string FeatureFilter
    {
        get => _featureFilter;
        set
        {
            if (_featureFilter == value)
            {
                return;
            }

            _featureFilter = value ?? string.Empty;
            OnPropertyChanged();
            ApplyFeatureFilter();
        }
    }

    /// <summary>筛选后的分组视图；无筛选时与 Groups 一致。</summary>
    public ObservableCollection<FeatureGroupViewModel> VisibleGroups { get; } = new();

    public RelayCommand ClearFeatureFilterCommand { get; }

    /// <summary>启动方式候选：mod 启动器 / 平台启动器 / 游戏本体（玩 mod 的人点的不是 RA3.exe）。</summary>
    /// <summary>兵种下拉按"阵营 · 类型"分组（官方库 630 条，不分组没法用）。</summary>
    public string ReinforcementUnitsSummary
    {
        get
        {
            if (ReinforcementUnits.Count == 0) return "兵种清单为空";

            // 用户 2026-10-06 提的缺口：换 mod 之后界面不告诉你"现在用的是哪个 mod 的库"。
            // RA3 不止日冕一个 mod（官方 630 + 人工 84 是含原版全单位的底座，各 mod 的单位只有一部分通用），
            // 所以这一行必须写清：哪个 mod、总共多少条、其中多少是这个 mod 独有的。
            // 2026-10-07：「哪个 mod」现在来自多信号识别（命令行 > 已加载模块 > 启动器配置），自带"依据"。
            var modKey = _modIdentity.ModKey;
            var builtIn = ReinforcementUnitCatalog.LoadBuiltIn().Count;
            var modSpecific = ReinforcementUnitCatalog
                .LoadUserCatalog(ReinforcementUnitCatalog.UserCatalogPathForModKey(AppContext.BaseDirectory, modKey))
                .Count;
            var split = modSpecific > 0 ? $"内置 {builtIn} + {modKey} {modSpecific}" : $"内置 {builtIn}";
            return $"{_modIdentity.DisplayText} · 单位库：{modKey} · 共 {ReinforcementUnits.Count} 条（{split}）· 换 mod 自动切换";
        }
    }

    public ObservableCollection<LauncherCandidate> LauncherCandidates { get; }

    public RelayCommand RescanLaunchersCommand { get; }

    /// <summary>选中的启动方式；与 LauncherPath 双向联动。</summary>
    public LauncherCandidate? SelectedLauncher
    {
        get => _selectedLauncher;
        set
        {
            if (ReferenceEquals(_selectedLauncher, value))
            {
                return;
            }

            _selectedLauncher = value;
            OnPropertyChanged();

            if (!_syncingLauncher && value is not null)
            {
                _syncingLauncher = true;
                try
                {
                    LauncherPath = value.Path;
                    StatusMessage = $"启动方式已切换：{value.DisplayName}";
                }
                finally
                {
                    _syncingLauncher = false;
                }
            }
        }
    }

    /// <summary>重新扫描启动方式（换了 mod / 装了新启动器时用）。</summary>
    public void RescanLaunchers()
    {
        var candidates = _scanLaunchers(LauncherPath);
        LauncherCandidates.Clear();
        foreach (var candidate in candidates)
        {
            LauncherCandidates.Add(candidate);
        }

        SyncSelectedLauncher();

        StatusMessage = LauncherCandidates.Count == 0
            ? "没扫到启动器：点「浏览…」指定你的 mod 启动器（例如日冕的 CoronaLauncher.exe）。"
            : $"已找到 {LauncherCandidates.Count} 个启动方式。";
    }

    /// <summary>把 LauncherPath 对应到候选列表里的那一项（手动浏览选的也能显示出来）。</summary>
    private void SyncSelectedLauncher()
    {
        if (_syncingLauncher)
        {
            return;
        }

        _syncingLauncher = true;
        try
        {
            var path = (LauncherPath ?? string.Empty).Trim().Trim('"');
            var match = LauncherCandidates.FirstOrDefault(
                candidate => string.Equals(candidate.Path, path, StringComparison.OrdinalIgnoreCase));

            if (match is null && path.Length > 0 && File.Exists(path))
            {
                // 手动浏览选的、不在候选里的程序：补一条"自定义"，让下拉能显示当前值
                match = new LauncherCandidate(
                    path,
                    $"自定义 · {Path.GetFileName(path)}",
                    LauncherScanner.Classify(Path.GetFileName(path)));
                LauncherCandidates.Add(match);
            }

            // 没配置过 / 配置的路径已经不存在：自动预选最优的那个（mod 启动器优先），
            // 免得下拉框一片空白、还得手动去挑。
            if (match is null && LauncherCandidates.Count > 0)
            {
                match = LauncherCandidates[0];
                LauncherPath = match.Path;
            }

            _selectedLauncher = match;
            OnPropertyChanged(nameof(SelectedLauncher));
        }
        finally
        {
            _syncingLauncher = false;
        }
    }

    private void ApplyFeatureFilter()
    {
        var query = _featureFilter.Trim();
        VisibleGroups.Clear();
        foreach (var group in Groups)
        {
            var features = query.Length == 0
                ? group.Features
                : group.Features.Where(item => MatchesFilter(item, query)).ToArray();
            if (features.Count > 0)
            {
                VisibleGroups.Add(new FeatureGroupViewModel(group.Name, features));
            }
        }
    }

    private static bool MatchesFilter(FeatureItemViewModel item, string query)
    {
        return item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (item.Hotkey?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || item.Feature.RawName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public RelayCommand InspectHooksCommand { get; }

    public RelayCommand ExportEvidenceCommand { get; }

    /// <summary>打开用户数据目录（设置 / 单位清单 / 日志 / 现场记录都在这里）。</summary>
    public RelayCommand OpenDataFolderCommand { get; }

    /// <summary>「关于」一行：产品名与版本（版本号来自项目文件，不在代码里重复写）。</summary>
    public static string ProductVersionLine => $"{AppInfo.DisplayName} v{AppInfo.Version}";

    /// <summary>「关于」说明：数据放哪里，出问题去哪找。</summary>
    public static string ProductDataLine => $"数据目录（设置、单位清单、日志、现场记录）：{Ra3Trainer.Core.Runtime.TrainerPaths.DataRoot}";

    public RelayCommand ExportProbeCommand { get; }
    /// <summary>
    /// 只读采样的现状。2026-10-11 文案评审：原来写的是"已采样本数 N；约 2 Hz。zoom 根链未定位；坐标副本可能陈旧。"
    /// —— 全是内部词，用户看不懂也不知道要不要管。改成"有没有在用、要不要管"。
    /// </summary>
    public string ProbeSummary
    {
        get
        {
            var count = _probeLog?.SampleCount ?? 0;
            return count == 0
                ? "只读采样：未开始（平时不用开；只有排查问题时才需要）。"
                : $"只读采样：已采 {count} 条（只读，不影响游戏）。";
        }
    }

    private string _ownershipProbeText = "未运行。点「读出选中单位归属」——只读，不写内存、不动你的操作。";
    /// <summary>“心灵控制（测试）”只读取证结果（含对象地址、归属候选 [U+0x418] 与本机玩家 [ID] 的对照）。</summary>
    public string OwnershipProbeText
    {
        get => _ownershipProbeText;
        private set { _ownershipProbeText = value; OnPropertyChanged(); }
    }

    private string _ownershipTransferText = "未运行。默认关闭：先勾上面的「开启高风险功能」，游戏里选中一个单位，再点下面的按钮（或直接按 Ctrl+D）。";
    /// <summary>心灵控制结果：快照路径、对象 U、owner 改前→改后、以及是否可还原。</summary>
    public string OwnershipTransferText
    {
        get => _ownershipTransferText;
        private set { _ownershipTransferText = value; OnPropertyChanged(); }
    }

    /// <summary>最近一次心灵控制落盘的快照；「解除心灵控制」用它回写。null = 没有可还原的快照。</summary>
    private OwnershipApplySnapshot? _lastOwnershipApply;

    /// <summary>心灵控制快照目录（exe 旁边的 artifacts\；与仓库产物命名一致）。</summary>
    private static string OwnershipSnapshotDirectory => TrainerPaths.ArtifactsDirectory;

    /// <summary>
    /// 心灵控制的动作槽等待窗口。游戏只在"资金更新"那条钩子上轮询动作槽，别处都在等，所以给足 8 秒
    /// （原先用默认 1500ms，真机上会被误判成失败 —— 见 OwnershipConfirmWindow 的延迟确认）。
    /// </summary>
    private static readonly TimeSpan OwnershipDispatchTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// 延迟确认窗口：动作槽协议超时 ≠ 没执行。`[U+0x418]` 回读才是地面真相，
    /// 所以写后在这个窗口内反复回读 owner，变了才算成功。
    /// </summary>
    private static readonly TimeSpan OwnershipConfirmWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan OwnershipConfirmPollInterval = TimeSpan.FromMilliseconds(500);

    private string ProbeDirectory => Path.Combine(_diagnostics.LogDirectory, "probe");
    public bool SafeSamplingEnabled
    {
        get => _safeSamplingEnabled;
        set
        {
            if (value == _safeSamplingEnabled) return;
            if (!value) { StopProbe("手动关闭"); return; }
            try
            {
                if (ConnectionState != ConnectionState.Connected || _session?.CanUseFeatures != true || _session.Resolver is null ||
                    _targetProcessId is not int processId || _targetProcess?.HasExited != false)
                    throw new InvalidOperationException("请先连接游戏，再开启安全采样。");
                _probeMemory = _probeMemoryFactory(processId);
                _probe = new ReadOnlyProbe(_probeMemory, _session.Resolver, _attachedIdentity);
                _probeLog = new ProbeLog(ProbeDirectory);
                _safeSamplingEnabled = true;
                _diagnostics.Log.Write("probe-start", new { path = _probeLog.FilePath, intervalMs = 500, readOnly = true });
                _probeTimer.Start();
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProbeSummary));
            }
            catch (Exception ex)
            {
                StopProbe("开启失败");
                _diagnostics.Log.Write("probe-start-failed", new { error = ex.Message });
                StatusMessage = "安全采样未开启：" + ex.Message;
                OnPropertyChanged();
            }
        }
    }

    private long _lastActionTrailQpc;
    private void OnProbeTick(object? sender, EventArgs e)
    {
        if (!_safeSamplingEnabled || _probe is null || _probeLog is null) return;
        try
        {
            if (ConnectionState != ConnectionState.Connected || _session?.CanUseFeatures != true || _targetProcess?.HasExited != false)
            { StopProbe("目标已断开"); return; }
            _probeLog.Append(_probe.Capture(ArePatchesInstalled, DateTimeOffset.Now) with
            {
                AttachSessionId = _diagnostics.ActionIdentity?.AttachSessionId,
                LastActionRequestId = FeatureController?.LastRequestId,
                EnabledFeatures = Groups.SelectMany(group => group.Features)
                    .Where(item => item.IsActive).Select(item => item.DisplayName).ToArray()
            });
            // Additional fixed protocol reads only after failure, only with the existing opt-in probe,
            // at most once per 2 seconds. Normal 2 Hz probe fields and reads are untouched.
            try
            {
                var controller = FeatureController;
                if (controller?.TrailingRequestId is { } requestId &&
                    Stopwatch.GetElapsedTime(_lastActionTrailQpc).TotalSeconds >= 2)
                {
                    _lastActionTrailQpc = Stopwatch.GetTimestamp();
                    var snapshot = controller.CaptureTrailingEvidence();
                    _diagnostics.Log.Write("action-trailing", new { requestId, phase = "trailing",
                        attachSessionId = controller.AttachSessionId, snapshot, readOnly = true, intervalMs = 2000,
                        lastActionRequestId = controller.LastRequestId,
                        interveningRequest = controller.LastRequestId != requestId,
                        status = snapshot is null ? "unavailable-or-dispatch-active" : "captured" });
                }
            }
            catch { /* Trailing evidence failure cannot stop the original probe. */ }
            OnPropertyChanged(nameof(ProbeSummary));
        }
        catch (Exception ex)
        {
            _diagnostics.Log.Write("probe-failed", new { error = ex.Message });
            StopProbe("采样失败");
            StatusMessage = "安全采样已停止：" + ex.Message;
        }
    }

    private void StopProbe(string reason)
    {
        _probeTimer.Stop();
        if (_safeSamplingEnabled)
            _diagnostics.Log.Write("probe-stop", new { reason, samples = _probeLog?.SampleCount ?? 0 });
        _safeSamplingEnabled = false;
        _probe = null;
        (_probeMemory as IDisposable)?.Dispose();
        _probeMemory = null;
        OnPropertyChanged(nameof(SafeSamplingEnabled));
        OnPropertyChanged(nameof(ProbeSummary));
    }

    private void ExportProbe()
    {
        try
        {
            LastEvidencePath = ProbeLog.Export(ProbeDirectory, _diagnostics.EvidenceDirectory);
            _diagnostics.Log.Write("probe-export", new { path = LastEvidencePath });
            StatusMessage = "采样已导出：" + LastEvidencePath;
        }
        catch (Exception ex) { StatusMessage = "导出采样失败：" + ex.Message; }
    }

    public RelayCommand OpenLogFolderCommand { get; }

    public RelayCommand OpenEvidenceFolderCommand { get; }

    /// <summary>目标进程 / 版本 / 模块基址，一行看完。</summary>
    public string DiagnosticsSummary
    {
        get => _diagnosticsSummary;
        private set
        {
            _diagnosticsSummary = value;
            OnPropertyChanged();
        }
    }

    /// <summary>最近一次 hook 点检查的结果（基于 patch 清单，与 mod 无关）。</summary>
    public string HookSummary
    {
        get => _hookSummary;
        private set
        {
            _hookSummary = value;
            OnPropertyChanged();
        }
    }

    public string FeatureSummary
    {
        get => _featureSummary;
        private set
        {
            _featureSummary = value;
            OnPropertyChanged();
        }
    }

    public string LastEvidencePath
    {
        get => _lastEvidencePath;
        private set
        {
            _lastEvidencePath = value;
            OnPropertyChanged();
        }
    }

    public string LogDirectory => _diagnostics.LogDirectory;

    public string RemoteMemorySummary { get; private set; } = "尚未申请远程块。";

    private void RefreshRemoteMemorySummary()
    {
        if (_session is null) return; // Keep released-block metadata visible after detach.
        if (_targetProcess?.HasExited == false) _session.RefreshAddressSpace();
        var snapshot = _session.GetDiagnosticSnapshot();
        RemoteMemorySummary = (snapshot.AddressSpace?.Summary ?? "可用地址空间尚未探测。") + "\n" +
            string.Join("\n", (snapshot.Blocks ?? []).Select(block =>
                $"远程块 0x{block.Address:X8} / 0x{block.Size:X}（{block.Size:N0} 字节）：{block.State}；申请 {block.AllocatedAt:HH:mm:ss}；释放 {block.ReleasedAt:HH:mm:ss}；{block.Name}"));
        OnPropertyChanged(nameof(RemoteMemorySummary));
    }

    public string LastExitDescription { get; private set; } = "尚无退出记录。";

    private CrashContextSnapshot CaptureContext(string reason) => new(
        DateTimeOffset.Now,
        _attachedIdentity,
        reason,
        Groups.SelectMany(group => group.Features).Where(item => item.IsActive).Select(item => item.DisplayName).ToArray(),
        _session?.GetDiagnosticSnapshot() ?? new SessionMemorySnapshot([], []),
        _diagnostics.Log.RecentEntries);

    public string EvidenceDirectory => _diagnostics.EvidenceDirectory;

    /// <summary>把每个 hook 点的现存字节与 patch 清单比一遍，再决定能不能信任它。</summary>
    public void InspectHookSites()
    {
        if (_session?.Resolver is null)
        {
            StatusMessage = "先点「连接游戏」。";
            return;
        }

        try
        {
            var report = _session.InspectHookSites();
            _diagnostics.RecordHookReport(report);
            HookSummary = report.Summary;
            StatusMessage = report.Summary;
        }
        catch (Exception ex)
        {
            HookSummary = $"检查失败：{ex.Message}";
            StatusMessage = HookSummary;
        }
    }

    /// <summary>把解释一次崩溃需要的东西打成一个 zip（只认目标进程与文件，不认 mod）。</summary>
    public void ExportEvidence(string reason)
    {
        try
        {
            var request = new CrashEvidenceRequest(
                TargetProcessName: _manifest.TargetProcess,
                ProcessId: _targetProcessId,
                TargetModulePath: _targetModulePath,
                SessionLogPath: _diagnostics.Log.FilePath,
                ExitDescription: reason,
                Notes:
                [
                    $"连接状态：{ConnectionStateText}",
                    $"修改已开启：{(ArePatchesInstalled ? "是" : "否")}",
                    $"已启用功能：{EnabledFeatureCount}",
                    $"改动点检查：{HookSummary}"
                ], Context: CaptureContext(reason), ProbeDirectory: ProbeDirectory);
            var result = _diagnostics.CollectEvidence(request);
            LastEvidencePath = result.ArchivePath;
            StatusMessage = result.Warnings.Count == 0
                ? $"取证包已生成：{result.ArchivePath}"
                : $"取证包已生成（{result.Warnings.Count} 条采集告警，见包内 summary.txt）：{result.ArchivePath}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"取证包生成失败：{ex.Message}";
        }
    }

    private int EnabledFeatureCount =>
        Groups.SelectMany(group => group.Features).Count(item => item.IsActive);

    // Retained queue API; the UI intentionally has no automatic queue button.
    internal Task<IReadOnlyList<ReinforcementQueueResult>> ExecuteReinforcementQueueAsync(
        IEnumerable<ReinforcementQueueEntry> entries, CancellationToken cancellationToken = default)
    {
        var controller = FeatureController ?? throw new InvalidOperationException("请先连接目标进程。");
        var feature = Groups.SelectMany(group => group.Features).Single(item => item.Feature.RawName == "We Need Back").Feature;
        return ReinforcementQueueRunner.ExecuteAsync(entries, controller, feature,
            cancellationToken: cancellationToken, validateBeforeDispatch: () =>
            {
                if (!ReferenceEquals(controller, FeatureController) || !ArePatchesInstalled)
                    throw new InvalidOperationException("会话已变化，停止派发。");
                FeatureSafetyPolicy.Validate(feature, true, AllowUnverifiedWorldPositionFeatures);
            }, onResult: ReportReinforcementQueueResult);
    }

    internal void ReportReinforcementQueueResult(ReinforcementQueueResult result)
    {
        // Isolate presentation and persistence independently; neither controls dispatch.
        try { StatusMessage = result.Message; }
        catch { /* A failing UI observer cannot suppress the log or affect queue behavior. */ }
        try { _diagnostics.Log.Write("reinforcement-queue-result", new
            { name = result.Entry.Name, status = result.Status.ToString(), message = result.Message }); }
        catch { /* Logging failure must not alter the result or restart the queue. */ }
    }

    /// <summary>由功能项调用，让会话日志带上完整的动作时间线。</summary>
    public void OnFeatureExecuted(string rawName, string displayName, string action, string status, string? requestId = null)
    {
        _overlayAction = $"{displayName}：{status}";
        _diagnostics.Log.Write("feature", new { requestId, attachSessionId = _diagnostics.ActionIdentity?.AttachSessionId, name = rawName, display = displayName, action, status });
        RefreshFeatureSummary();
        PublishOverlay();
    }

    private void RefreshFeatureSummary()
    {
        var total = Groups.Sum(group => group.Features.Count);
        if (FeatureController?.PendingToggleRestoreCount is > 0)
        {
            FeatureSummary = $"开关待恢复 {FeatureController.PendingToggleRestoreCount} 处";
            return;
        }
        if (!ArePatchesInstalled && _session?.InstalledHookCount is > 0)
        {
            FeatureSummary = $"有 {_session.InstalledHookCount} 项修改待恢复";
            return;
        }
        FeatureSummary = ArePatchesInstalled
            ? $"已启用 {EnabledFeatureCount}/{total}"
            : $"未开启修改（共 {total} 项功能）";
    }

    private void RefreshDiagnosticsSummary()
    {
        RefreshRemoteMemorySummary();
        RefreshFeatureSummary();
        if (_targetProcessId is null)
        {
            DiagnosticsSummary = "未连接。";
            return;
        }

        var candidate = _locator.Snapshot().FirstOrDefault(item => item.ProcessId == _targetProcessId);
        if (candidate is null)
        {
            DiagnosticsSummary = $"PID {_targetProcessId}（模块信息不可读）";
            return;
        }

        var version = string.IsNullOrWhiteSpace(candidate.FileVersion) ? "无版本信息" : candidate.FileVersion;
        DiagnosticsSummary =
            $"{candidate.ModuleName} · PID {_targetProcessId} · {version} · " +
            $"{(candidate.Is32Bit ? "32 位" : "非 32 位")} · 基址 0x{candidate.ModuleBase:X}";
    }

    /// <summary>
    /// Keeps an eye on the attached process so its exit code is captured the moment it happens: a
    /// non-zero code while patches were installed is the "we need evidence" case.
    /// </summary>
    private void OnTargetExitTimerTick(object? sender, EventArgs e)
    {
        var process = _targetProcess;
        if (process is null)
        {
            _targetExitTimer.Stop();
            return;
        }

        try
        {
            process.Refresh();
            if (!process.HasExited)
            {
                return;
            }

            int? exitCode = null;
            try { exitCode = process.ExitCode; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            { _diagnostics.Log.Write("exit-code-unavailable", new { error = ex.Message }); }
            var wasPatched = ArePatchesInstalled;
            var processId = _targetProcessId;
            _targetExitTimer.Stop();

            // 用户 2026-10-06 报的 bug：从游戏退出来之后，面板没了、进程还在后台、主窗看不见。
            // 这里检测的就是"游戏进程退出"，以前只做了卸 patch + 收面板，没把主窗叫回来。
            // 退出面板模式 ⇒ MainWindow 收到 PropertyChanged 就把主窗显示出来（复用已有的 ShowWithoutActivation）。
            PanelMode = false;
            _diagnostics.Log.Write("target-exit", new
            {
                processId,
                exitCode = exitCode.HasValue ? $"0x{unchecked((uint)exitCode.Value):X8}" : "未知",
                exitCodeSigned = exitCode,
                patchesInstalled = wasPatched,
                attachSessionId = _diagnostics.ActionIdentity?.AttachSessionId,
                trailingRequestId = FeatureController?.TrailingRequestId
            });

            var description = exitCode.HasValue ? $"目标进程已退出，退出码 0x{unchecked((uint)exitCode.Value):X8}（{exitCode}）" : "目标进程已退出，退出码未知";
            // Capture the original module path/PID and patched state before cleanup clears them.
            try
            {
                LastEvidencePath = _diagnostics.SaveExitSnapshot(CaptureContext(description));
                if (exitCode != 0 && _autoCollectEvidenceOnExit)
                    ExportEvidence($"{description}；自动取证");
            }
            finally
            {
                _session?.ForgetExitedProcess();
                DisposeSession();
            }
            ConnectionState = !exitCode.HasValue ? ConnectionState.ExitUnknown : exitCode == 0 ? ConnectionState.Disconnected : ConnectionState.AbnormalExit;
            LastExitDescription = !exitCode.HasValue ? description + "；本地状态已清理，原因待查。"
                : exitCode == 0 ? description + "；正常退出（按退出码）。"
                : description + "；异常退出（含强制终止，原因待查）；本地状态已清理。";
            OnPropertyChanged(nameof(LastExitDescription));
            _diagnostics.PersistExitPath(LastExitDescription, LastEvidencePath);
            StatusMessage = LastExitDescription;

        }
        catch (InvalidOperationException ex)
        {
            HandleUnknownTargetState(ex);
        }
        catch (Exception ex)
        {
            _diagnostics.Log.Write("target-exit-watch-failed", new { error = ex.Message });
            HandleUnknownTargetState(ex);
        }
    }

    private void HandleUnknownTargetState(Exception error)
    {
        _targetExitTimer.Stop();
        LastExitDescription = $"目标状态未知，无法确认退出或清理：{error.Message}；请一键全撤重试清理。";
        LastEvidencePath = _diagnostics.SaveExitSnapshot(CaptureContext(LastExitDescription));
        CancelSessionActions();
        StopHotkeys();
        FeatureController?.QuiesceActions();
        ArePatchesInstalled = false;
        ConnectionState = ConnectionState.ExitUnknown;
        OnPropertyChanged(nameof(LastExitDescription));
        StatusMessage = LastExitDescription;
    }

    private static void OpenFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch (Exception)
        {
            // 打开目录只是顺手便利，绝不能让 UI 崩。
        }
    }

    private void RaiseCommandStates()
    {
        BrowseLauncherCommand.RaiseCanExecuteChanged();
        LaunchAndLoadCommand.RaiseCanExecuteChanged();
        OpenReinforcementUnitPickerCommand.RaiseCanExecuteChanged();
        SaveReinforcementPresetCommand.RaiseCanExecuteChanged();
        ApplyReinforcementPresetCommand.RaiseCanExecuteChanged();
        InstallPatchesCommand.RaiseCanExecuteChanged();
        RestorePatchesCommand.RaiseCanExecuteChanged();
        ReadSelectedUnitCodeCommand.RaiseCanExecuteChanged();
        ProbeSelectedUnitOwnershipCommand.RaiseCanExecuteChanged();
        ApplyOwnershipTransferCommand.RaiseCanExecuteChanged();
        RestoreOwnershipCommand.RaiseCanExecuteChanged();
        ScanUnitTypesCommand.RaiseCanExecuteChanged();

    }

    private void RaiseFeatureCommandStates()
    {
        foreach (var item in Groups.SelectMany(group => group.Features))
        {
            item.RaiseCommandState();
        }
    }

    private IReadOnlyList<FeatureGroupViewModel> CreateGroups(IEnumerable<TrainerFeature> features)
    {
        var items = features.Select(feature => new FeatureItemViewModel(feature, this)).ToArray();
        var groups = new List<FeatureGroupViewModel>
        {
            Group("资源/全局", items, "增加玩家战场资金", "无限电力", "无限秘密协议点数", "解开所有秘密协议技能",
                "快速建造建筑物/单位", "秘密协议技能与超级武器快速冷却", "禁止使用技能", "无限缩放", "消散战争迷雾",
                "禁止电脑建造建筑物/单位", "玩家全建筑物/单位无敌", "一击必杀对方建筑物/单位"),
            Group("选中对象", items, "选择的单位快速升级", "选择的单位高速移动", "选择的单位缓慢移动", "选择的单位暂停", "选择的单位恢复速度",
                "选择的建筑物/单位无限生命值", "选择的建筑物/单位生命值变为1", "选择的建筑物/单位恢复原本的生命值",
                "选择的单位无限弹药/炸弹", "选择的建筑物/单位ID", "摧毁选择的建筑物/单位",
                "威胁等级最大", "威胁等级最高", "威胁等级恢复原状", "选择的矿脉恢复采集矿量"),
            Group("战场动作", items, "建筑物可随地建造", "给玩家基地车", "复制选择的建筑物/单位给玩家", "呼叫战场增援")
        };

        // 兜底：报告里出现的功能，若没有被上面任何分组列到，自动进「其他功能」。
        // 教训：分组曾是手写死名单，"Player God Mode / Player One Kill Mode" 两项因此被静默吞掉，
        // 报告 32 项而界面只有 29 项。任何新功能都不该无声消失。
        var grouped = new HashSet<FeatureItemViewModel>(groups.SelectMany(group => group.Features));
        var orphans = items.Where(item => !grouped.Contains(item)).ToArray();
        if (orphans.Length > 0)
        {
            groups.Add(new FeatureGroupViewModel("其他功能", orphans));
        }

        return groups;
    }

    private static FeatureGroupViewModel Group(string name, IReadOnlyList<FeatureItemViewModel> items, params string[] names)
    {
        var selected = names
            .Select(displayName => items.FirstOrDefault(item => item.DisplayName == displayName))
            .Where(item => item is not null)
            .Cast<FeatureItemViewModel>()
            .ToArray();
        return new FeatureGroupViewModel(name, selected);
    }

    private TrainerAppSettings CurrentSettings()
    {
        return new TrainerAppSettings(
            (LauncherPath ?? string.Empty).Trim(),
            (LauncherArguments ?? string.Empty).Trim(),
            _attachTimeoutSeconds,
            GetResourceValueSettings(),
            ReinforcementPresets.ToArray(),
            _allowNonStandardVersion,
            _inspectHooksBeforeInstall,
            _autoCollectEvidenceOnExit,
            AllowUnverifiedWorldPositionFeatures, AutoFloatAfterLaunch);
    }

    private void PersistSettings()
    {
        _settingsStore.Save(CurrentSettings());
    }

    /// <summary>存设置但不让失败冒到界面上：勾选框的 setter 调它，抛异常会把整个界面搞崩。</summary>
    private void PersistSettingsSafe()
    {
        try
        {
            PersistSettings();
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存设置失败：{ex.Message}";
        }
    }

    private void ApplyPreset(ReinforcementPreset preset)
    {
        PresetNameText = preset.Name;
        ReinforcementUnitIdText = $"0x{preset.UnitId:X8}";
        ReinforcementCountText = preset.Count.ToString();
        ReinforcementRankText = preset.Rank.ToString();
    }

    private int IndexOfPreset(string name)
    {
        for (var index = 0; index < ReinforcementPresets.Count; index++)
        {
            if (ReinforcementPresets[index].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private TrainerFeature? FindUiFeature(string rawName)
    {
        return Groups
            .SelectMany(group => group.Features)
            .Select(item => item.Feature)
            .FirstOrDefault(feature => feature.RawName.Equals(rawName, StringComparison.Ordinal));
    }

    private static bool IsResourceValueFeature(TrainerFeature feature)
    {
        return feature.RawName is "Moeny" or "Power" or "SC POINT";
    }

    // ------------------------------------------------------------------ M4 游戏内面板：小键盘导航

    /// <summary>呼叫增援 = 「We Need Back」这条功能的命令，同一个入口，不复制逻辑。</summary>
    private System.Windows.Input.ICommand? ReinforcementCallCommand =>
        Groups.SelectMany(group => group.Features)
              .FirstOrDefault(item => item.Feature.RawName == "We Need Back")?.Command;

    /// <summary>Rebuilds the panel's row set for the layer the panel is showing.</summary>
    public void RefreshPanelRows() => Navigator.SetRows(_overlayPanelSection, BuildPanelRows());

    private IEnumerable<PanelNavRow> BuildPanelRows()
    {
        return _panelLayer switch
        {
            PanelLayerFaction => FactionPickerRows(),
            PanelLayerDomain => DomainPickerRows(),
            PanelLayerUnit => UnitPickerRows(),
            _ => MainPanelRows()
        };
    }

    /// <summary>
    /// 主层：一格一动作，没有光标、没有翻页。
    /// 8 个收藏槽 + 发兵 + 用游戏里选中的 + 数量/星级 + 找兵 + 结果行 + 位置格。
    /// </summary>
    /// <summary>
    /// 面板的页 = 主窗的四个页签。游戏内面板不是"只有增援"：四页都在，Num4/Num6 换页。
    /// </summary>
    // 面板页序 = 面板页签（游戏内动词）。曾经这里另有一份 {启动,增援,功能,诊断}，
    // 与 OverlayPanelSection 的校验打架 ⇒ 按 Num6 算出的「启动」被拒，页面原地不动。单一份。
    private static readonly string[] PanelPages = ConsoleNavigation.PanelSections.ToArray();

    /// <summary>面板当前页 = 主窗当前页签（一份状态）。主窗默认停在「启动」，面板也一样。</summary>
    private string _panelPage = ConsoleNavigation.DefaultPanelSection;
    public string PanelPage => _panelPage;

    /// <summary>Num4/Num6 = 左右换页（数字键盘的"左右"，通用）。换页同时把主窗页签带走，两边永远一个状态。</summary>
    private void StepPanelPage(int direction)
    {
        var at = Array.IndexOf(PanelPages, _panelPage);
        var next = PanelPages[((at + direction) % PanelPages.Length + PanelPages.Length) % PanelPages.Length];
        _panelLayer = PanelLayerMain;
        _panelPositionMode = false;
        SelectPanelPage(next);          // 写唯一那份状态：面板页 + 主窗页签一起动
        SetPanelResult($"换到「{next}」页");
        RefreshPanelRows();
    }

    private IEnumerable<PanelNavRow> MainPanelRows()
    {
        foreach (var row in PageRows())
        {
            yield return row;
        }

        // 面板位置是**面板的属性**，跟翻到哪一页无关 —— 每页最后一行都在，
        // 否则换页之后"位置"格就消失（曾经就这么丢过）。
        yield return PositionRow();
    }

    private IEnumerable<PanelNavRow> PageRows()
    {
        switch (_panelPage)
        {
            case "功能": return FeatureRows();
            case "选单位": return FactionPickerRows();
            default: return ReinforcementRows();
        }
    }

    /// <summary>
    /// 「功能」页 = 主窗「功能」页签里的**全部游戏功能**（32 项，按主窗那 4 个分组排），
    /// 每行右边标出它自己的快捷键 —— 用户要的就是"能按哪个键直接来"。
    /// 用户的原话："不是有 31 项还是三十几项的功能，你全写上去啊，而且包括可以用什么快捷键直接按，你标注上去。"
    /// 行上带的命令就是主窗那一条（同一个入口，不复制逻辑）：钱/电力是数值行（+/- 调、Num5 执行），
    /// 其余是开关/执行行。行数比视口高没关系：面板会跟着高亮自动滚（FollowPanelCursor）。
    /// </summary>
    /// <summary>
    /// 把心灵控制/还原的结论同时送进**游戏内面板**（"上次"结果行），并重算行集 ——
    /// 「解除心灵控制」行能不能按、心灵控制行为什么不能按，都要跟着变。
    /// </summary>
    private void PublishOwnershipToPanel(string text)
    {
        SetPanelResult(text);
        RefreshPanelRows();
    }

    /// <summary>
    /// 心灵控制行在面板上的状态：把"现在能不能按、为什么不能"**写在行上**。
    /// 面板上按了没反应 = 最坏的一种（用户对静默无反应零容忍），所以状态列必须说真话。
    /// </summary>
    private string OwnershipApplyPanelState() =>
        FeatureController is null || _targetProcessId is null ? "未连接游戏"
        : !ArePatchesInstalled ? "未装 patch"
        : !AllowUnverifiedWorldPositionFeatures ? "需开高风险功能"
        : "可执行";

    private IEnumerable<PanelNavRow> FeatureRows()
    {
        // 页头行宽度固定：图例太长会被 CharacterEllipsis 截掉（2026-10-07 踩过）。
        // 「7/9 换页」这类信息面板页脚本来就有（NUM7/9 换页），所以这里只留「Ctrl+D 心灵控制」。
        yield return StatusRow("page-head", "功能 · 全局开关", "Ctrl+D 心灵控制");
        // 结果行放最上面：面板比视口高时它在最后一行，按完键就看不见反馈了（有实测教训）。
        yield return StatusRow("result", "上次　" + PanelResultText, string.Empty);

        foreach (var group in Groups)
        {
            if (group.Features.Count == 0) continue;
            yield return StatusRow("group:" + group.Name, group.Name, $"{group.Features.Count} 项");
            foreach (var item in group.Features)
            {
                if (item.Command is null) continue;
                var raw = item.Feature.RawName;
                var detail = string.IsNullOrWhiteSpace(item.Hotkey) ? string.Empty : item.Hotkey;
                if (PanelResourceKeys.TryGetValue(raw, out var key))
                {
                    // 钱/电力/秘技点：不占额外的键（一行一个键），按 Num5 = 执行；
                    // 钱和电力每按一次把数值往上加一档再执行 —— 用户要的是"接着按就继续增加"。
                    yield return new PanelNavRow
                    {
                        Kind = PanelRowKind.Action,
                        Action = "feat:" + raw,
                        Label = item.DisplayName,
                        Detail = detail,
                        State = key switch
                        {
                            "money" => MoneyAmountText,
                            "power" => PowerValueText,
                            _ => $"点数 {ScPointValueText}"
                        },
                        Command = item.Command,
                    };
                    continue;
                }

                yield return new PanelNavRow
                {
                    Kind = item.IsToggle ? PanelRowKind.Toggle : PanelRowKind.Action,
                    Action = "feat:" + raw,
                    Label = item.DisplayName,
                    Detail = detail,                                  // ← 他的快捷键，直接印在行上
                    State = item.IsToggle ? (item.IsActive ? "已启用" : "未启用") : string.Empty,
                    Command = item.Command,
                };
            }
        }

        // ===== 心灵控制：把当前选中单位变成自己的（高风险闸门之后）=====
        // 放在**功能页末尾**而不是页首：行的顺序是面板的契约 —— 页首插行会让"高亮往下走 N 行
        // 到钱那一行"的既有走法整体位移（Round37 夹具当场炸过）。末尾追加不动任何既有行。
        // 为什么放**面板**里：桌面点一次要先 alt-tab，游戏失焦期间动作槽可能一直不被轮询
        //（2026-10-07 真机教训：等 1500ms 报 TimedOut、游戏随后才执行）；浮窗/游戏内面板
        // ShowActivated=False **不夺焦**，是"不打断游戏"的那个入口。
        // 闸门不变：高风险功能没开，行上直接写"需开高风险功能"，按了也会明说，绝不静默。
        // 2026-10-07 用户定论：多选心灵控制撤掉（游戏里框选只装自己的单位，选不到别人的）⇒ 面板只留两行：
        // 「心灵控制（按一下就执行，一次一个）」+「解除心灵控制」。一行一按 = 他要在面板里一键完成的那件事。
        yield return new PanelNavRow
        {
            Kind = PanelRowKind.Action,
            Action = "duoshe:apply",
            Label = "心灵控制　把选中的这个变成你的",
            Detail = "Ctrl+D · Num1 · 按 5",
            State = OwnershipApplyPanelState(),
            Command = ApplyOwnershipTransferCommand,
        };
        yield return new PanelNavRow
        {
            Kind = PanelRowKind.Action,
            Action = "duoshe:restore",
            Label = "解除心灵控制　按快照写回旧 owner",
            Detail = "用最近一次心灵控制快照",
            State = _lastOwnershipApply is null ? "无快照（先控制一次）" : "可还原",
            Command = RestoreOwnershipCommand,
        };
    }


    /// <summary>钱/电力/秘技点：面板上显示当前值，可以就地调。</summary>
    private static readonly Dictionary<string, string> PanelResourceKeys = new(StringComparer.Ordinal)
    {
        ["Moeny"] = "money",
        ["Power"] = "power",
        ["SC POINT"] = "scpoint"
    };

    /// <summary>
    /// 「单位」页 = 只在游戏里成立的单位动作：选中对象那一组（血量/弹药/ID/摧毁/威胁等级/矿脉）
    /// 加上战场动作（随地建造 / 基地车 / 复制给别人 / 呼叫增援）。
    /// 面板页签是游戏内动词，不是主窗页签的复制。
    /// </summary>
    private IEnumerable<PanelNavRow> UnitRows()
    {
        yield return StatusRow("page-head", "选单位 · 挑一个兵种", "Num7/9 换页");
        yield return StatusRow("result", "上次　" + PanelResultText, string.Empty);

        foreach (var group in Groups)
        {
            if (group.Name != "选中对象" && group.Name != "战场动作") continue;
            if (group.Features.Count == 0) continue;
            yield return StatusRow("group:" + group.Name, group.Name, $"{group.Features.Count} 项");
            foreach (var item in group.Features)
            {
                if (item.Command is null) continue;
                yield return new PanelNavRow
                {
                    Kind = item.IsToggle ? PanelRowKind.Toggle : PanelRowKind.Action,
                    Action = "feat:" + item.Feature.RawName,
                    Label = item.DisplayName,
                    Detail = string.IsNullOrWhiteSpace(item.Hotkey) ? string.Empty : item.Hotkey,
                    State = item.IsToggle ? (item.IsActive ? "已启用" : "未启用") : string.Empty,
                    Command = item.Command,
                };
            }
        }
    }

    /// <summary>「诊断」页：只读状态 + 紧急动作。</summary>
    private IEnumerable<PanelNavRow> DiagRows()
    {
        yield return StatusRow("page-head", "诊断", "Num4/6 换页");
        yield return StatusRow("diag-overlay", "游戏内面板", OverlayStatus);
        yield return StatusRow("diag-guidance", "指引", OverlayGuidance);
        yield return StatusRow("diag-result", "上次", PanelResultText);
        yield return ActionRow("refresh", "连接游戏", "只是找到并连上游戏", RefreshCommand);
        yield return ActionRow("install", "开启修改", "把修改装进游戏", InstallPatchesCommand);
        yield return ActionRow("restore", "一键全撤", "紧急还原", RestorePatchesCommand);
        // 修改器自己的开关原来占着「功能」页（游戏功能因此一个都看不见）；它们低频、更偏诊断，搬到这里。
        yield return ToggleRow("overlay", "游戏内面板", OverlayEnabled);
        yield return ToggleRow("panelmode", "面板模式（小键盘接管）", PanelMode);
        yield return ToggleRow("autofloat", "启动后自动开面板", AutoFloatAfterLaunch);
        yield return ToggleRow("inspecthooks", "装前先自检 hook", InspectHooksBeforeInstall);
        yield return ToggleRow("nonstandard", "允许非标准版本", AllowNonStandardVersion);
        yield return ToggleRow("autoevidence", "退出时自动收证据", AutoCollectEvidenceOnExit);
        yield return ToggleRow("safesampling", "安全取样", SafeSamplingEnabled);
    }

    private IEnumerable<PanelNavRow> ReinforcementRows()
    {
        var current = SelectedReinforcementUnit;
        // 顶部只报"送什么"：数量/星级下面各有自己的行，这里再来一遍就是同一信息出现两次。
        // 口径（用户 2026-10-06「面板的增援你看着不乱吗」）：原来这里是**两行碎句**
        // （"增援 · 兵种名" + "上次 结果"），合并成一行：左边报送什么，右边报上次结果 ——
        // 反馈仍然在最上面（面板比视口高时它是最后一行，看不到反馈是实测过的老问题）。
        yield return StatusRow("head", $"增援 · {current?.Name ?? "未选择"}", PanelResultText, _modIdentity.DisplayText);

        // 动作区：主操作紧跟状态，不再压在四个槽行下面（那是"乱"的主要来源之一）。
        // 主操作自带参数（几个 · 几星）：这一行本身就说明"会发生什么"，不用回头找上面两行。
        yield return ActionRow("send", "呼叫增援",
            $"{ReinforcementCountText} 个 · ★{ReinforcementRankText}", ReinforcementCallCommand);
        yield return ActionRow("usegame", "用游戏里选中的", string.Empty, ReadSelectedUnitCodeCommand);   // 口径B：0 归「返回」
        yield return ValueActionRow("count", "数量", ReinforcementCountText, key: "+-");
        yield return ValueActionRow("rank", "星级", ReinforcementRankText, key: "*/");

        // 收藏槽单独分组：和动作行分开，槽位空不空一眼看得出。
        var slots = PanelSlotUnits();
        yield return StatusRow("group:slots", "收藏槽", $"{slots.Count()} 个有兵");
        // 空槽不列：分组头已经写了"N 个有兵"，下面再摆一排"（空槽）"自相矛盾，也占掉视口。
        foreach (var slot in Enumerable.Range(0, PanelSlotKeys.Length).Where(i => slots.ElementAtOrDefault(i) is not null))
        {
            var unit = slots.ElementAtOrDefault(slot);
            yield return new PanelNavRow
            {
                Kind = PanelRowKind.Action,
                KeyLabel = PanelSlotKeys[slot],   // 派发用（显示已被 KeyDisplay 屏蔽掉数字）
                Action = "slot",
                Label = unit?.Name ?? "（空槽）",
                Detail = string.Empty,   // 面板只求快：槽行一行一个兵种名，代号/分类去「找兵」那一层看
                Payload = slot,
                // 当前兵种所在的槽单独标（绿色竖条）；导航高亮是另一回事，别混用同一个标志。
                IsCurrentUnit = unit is not null && current is not null && unit.Code == current.Code
            };
        }
    }

    /// <summary>
    /// 找兵第 1 层：**真阵营**（盟军/苏联/帝国/神州 …），键 1..N。
    /// 阵营不是我们编的，是数据里"分类 = 阵营 · 类型"的阵营段。用户 2026-10-07 口径 B：
    /// 第一层要是帝国/苏联/盟军/神州这种**字面阵营**，不是"陆军步兵/空军"那种军种。
    ///
    /// <para>排序（<see cref="UnitTaxonomy.OrderFactions"/>）：配置里的已知阵营在前（顺序可配），
    /// 其余阵营**从数据里自动出现**（按出现次数降序）—— 以后再多的 mod 阵营都自动进表、不丢、不混进「其他」；
    /// 「其他」恒最后。行上显示的是**显示名**（可配），Payload 仍是数据里的原始阵营名（匹配用）。</para>
    ///
    /// <para>按 mod 分层：只统计 <see cref="ScopedReinforcementUnits"/>（当前 mod + 原版通用）；其他 mod 的阵营
    /// 默认不出现，打开"显示全部 mod 单位"才出现。原版环境（直接开 RA3.exe）下神州这类 mod 阵营一个都不出现。</para>
    /// </summary>
    private IEnumerable<PanelNavRow> FactionPickerRows()
    {
        var counts = ScopedReinforcementUnits()
            .GroupBy(unit => unit.Faction)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var ordered = UnitTaxonomy.OrderFactions(counts.Keys, faction => counts[faction], _factionDisplay);

        yield return StatusRow("find-head", "找兵 · 选阵营", "Num8/2 选 · Num5 执行", "0 返回");
        // 当前 mod 也写在这一页 —— 这是唯一会按 mod 过滤的页。识别结果必须看得见：
        // 认不出时明说"未过滤（避免藏掉你的单位）"，否则单位"凭空少了"没人解释得清。
        yield return StatusRow("find-mod", _modIdentity.DisplayText, string.Empty, _modIdentity.EvidenceText);
        var index = 1;
        foreach (var display in ordered)
        {
            var count = counts[display.Faction];
            if (count == 0) continue;
            yield return ActionRow("findfaction", display.Label, $"{count} 个", null, display.Faction, key: index.ToString());
            index++;
        }

        // 按 mod 分层的开关：默认只显当前 mod + 原版通用；打开后放出其他 mod 的单位（一律带来源标注）。
        yield return ToggleRow("showallmods", "显示全部 mod 单位", ShowAllModUnits);
        yield return ActionRow("find-back", "← 返回", "回到面板", null, key: ".");
    }

    /// <summary>
    /// 找兵第 2 层：该阵营下的**游戏自带分类**（步兵/车辆/飞行器/舰船/建筑/特殊），键 1..N。
    /// 分类来自游戏内部名（editor_name，如 AlliedScoutInfantry → 步兵），见 <see cref="UnitTaxonomy.Domain"/>。
    /// **本阵营没有的分类不列**（不显示空页）：官方库 4×6 每种组合都 >0，但玩家自己导入的 mod 单位可能只占某几类，
    /// 空页只会让人以为坏了 —— 所以直接跳过（和"单位数为 0 的省/组不显示"是同一套处理）。
    /// </summary>
    private IEnumerable<PanelNavRow> DomainPickerRows()
    {
        if (_panelFaction is null) { yield return StatusRow("find-head", "找兵 · 先选阵营", "0 返回"); yield break; }
        // 按 mod 分层：只统计当前作用域内的单位（其它 mod 的单位不参与，所以它不会凭空出现在某个阵营下）。
        var scoped = ScopedReinforcementUnits().Where(unit => string.Equals(unit.Faction, _panelFaction, StringComparison.Ordinal));
        yield return StatusRow("find-head", "找兵 · " + FactionDisplayName(_panelFaction), "Num8/2 选分类 · Num5 执行", "0 返回");
        var counts = scoped.GroupBy(unit => unit.Domain).ToDictionary(group => group.Key, group => group.Count());
        var index = 1;
        foreach (var domain in UnitTaxonomy.DomainOrder)
        {
            // 本阵营没有这个分类 ⇒ 不列（不显示空页）。
            if (!counts.TryGetValue(domain, out var count) || count == 0) continue;
            yield return ActionRow("finddomain", UnitTaxonomy.ShortDomainLabel(domain), $"{count} 个", null, domain, key: index.ToString());
            index++;
        }

        yield return ActionRow("find-back", "← 返回阵营", "回到上一层", null, key: ".");
    }

    /// <summary>
    /// 找兵第 3 层：一屏 10 个，Num8/2 走到 + Num5 执行；超过一屏用 Num+ / Num- 翻屏（7/9 换页），**不截断**。
    /// 用户 2026-10-07 定案：不再有「用途」层 —— 选定（阵营 + 游戏分类）后直接就是单位表。
    /// </summary>
    private IEnumerable<PanelNavRow> UnitPickerRows()
    {
        if (_panelFaction is null) { yield return StatusRow("find-head", "找兵 · 先选阵营", "0 返回"); yield break; }
        if (_panelDomain is null) { yield return StatusRow("find-head", "选单位 · 先选分类", "0 返回"); yield break; }
        var scoped = ScopedReinforcementUnits()
            .Where(unit => string.Equals(unit.Faction, _panelFaction, StringComparison.Ordinal))
            .Where(unit => unit.Domain == _panelDomain.Value)
            .OrderByDescending(unit => UsageOf(unit))
            .ThenBy(unit => unit.Name, StringComparer.Ordinal)
            .ToArray();
        var screens = Math.Max(1, (scoped.Length + PanelScreenSize - 1) / PanelScreenSize);
        _panelScreen = Math.Clamp(_panelScreen, 0, screens - 1);
        var page = scoped.Skip(_panelScreen * PanelScreenSize).Take(PanelScreenSize).ToArray();
        yield return StatusRow("find-head",
            $"{FactionDisplayName(_panelFaction)} · {UnitTaxonomy.ShortDomainLabel(_panelDomain.Value)}",
            $"第 {_panelScreen + 1}/{screens} 屏");

        for (var i = 0; i < page.Length; i++)
        {
            var unit = page[i];
            yield return new PanelNavRow
            {
                Kind = PanelRowKind.Action,
                KeyLabel = PanelPickKeys[i],      // 口径 B：编号不再印，靠 8/2 选
                Action = "findunit",
                Label = unit.Name,
                // 不摊原始代号，也不再挂「用途」标签（用户 2026-10-07：用途那套标签从面板流程去掉，更清爽）。
                Detail = string.Empty,
                // D/C 口径：来自 mod 的单位带来源标注（"仅日冕"），原版单位为空 —— 换 mod 时一眼能分。
                // 放 State（行右侧信息值），Detail 保持空（既有断言钉着 Detail 为空）。
                State = unit.ModBadge,
                Payload = unit,
                IsCurrent = SelectedReinforcementUnit is not null && unit.Code == SelectedReinforcementUnit.Code
            };
        }

        yield return ActionRow("find-prev", "上一屏", screens > 1 ? $"第 {Math.Max(1, _panelScreen)} 屏" : "只有一屏", null, key: "-");
        yield return ActionRow("find-next", "下一屏", screens > 1 ? $"第 {Math.Min(screens, _panelScreen + 2)} 屏" : "只有一屏", null, key: "+");
        yield return ActionRow("find-first", "第一屏", string.Empty, null, key: "/");
        yield return ActionRow("find-last", "最后一屏", string.Empty, null, key: "*");
        yield return ActionRow("find-back", "← 返回分类", "回到上一层", null, key: ".");
    }

    /// <summary>最近用过的兵种排前面：常用兵永远在头一屏，不用翻。</summary>
    private int UsageOf(ReinforcementUnitEntry unit)
    {
        var at = _panelRecent.FindIndex(item => item.Code == unit.Code);
        return at < 0 ? 0 : _panelRecent.Count - at;
    }


    private IEnumerable<PanelNavRow> StartRows()
    {
        yield return ActionRow("launch", "启动游戏并开启修改", "本页主操作", LaunchAndLoadCommand);
        yield return ActionRow("refresh", "连接游戏", "只是找到并连上游戏", RefreshCommand);
        yield return ActionRow("install", "开启修改", "把修改装进游戏", InstallPatchesCommand);
        yield return ActionRow("restore", "一键全撤", "紧急还原", RestorePatchesCommand);
        yield return StatusRow("launcher", "启动器", SelectedLauncher?.DisplayName ?? (string.IsNullOrWhiteSpace(LauncherPath) ? "未选择" : LauncherPath));
        yield return ActionRow("rescan", "重新扫描启动器", string.Empty, RescanLaunchersCommand);
        yield return ActionRow("browse", "浏览启动器…", string.Empty, BrowseLauncherCommand);
        yield return ActionRow("savelauncher", "保存启动器设置", string.Empty, SaveLauncherSettingsCommand);
        yield return ToggleRow("autofloat", "启动后自动开面板", AutoFloatAfterLaunch);
        yield return ToggleRow("inspecthooks", "装 patch 前校验", InspectHooksBeforeInstall);
        yield return ToggleRow("nonstandard", "允许非标准版本", AllowNonStandardVersion);
        yield return ToggleRow("autoevidence", "出问题自动记录现场", AutoCollectEvidenceOnExit);
        yield return ToggleRow("safesampling", "安全采样（只读）", SafeSamplingEnabled);
    }

    /// <summary>
    /// 位置格。面板上唯一会"切模式"的东西，而且只能鼠标点进来（鼠标现在只吞面板内的点击），
    /// 进来后 Num8/2/4/6 才变成挪动。战斗中没人会把鼠标挪到这个角上点，所以不会误触。
    /// </summary>
    private PanelNavRow PositionRow() => new()
    {
        Kind = PanelRowKind.Position,
        Action = "position-enter",
        Label = _panelPositionMode ? "位置调整中 · Num. 退出" : "面板位置（鼠标点这里调）",
        Detail = _panelPositionMode ? $"{_overlayPanelX} , {_overlayPanelY} · Num8/2/4/6 挪" : $"{_overlayPanelX} , {_overlayPanelY}"
    };

    private static PanelNavRow ActionRow(string action, string label, string detail, System.Windows.Input.ICommand? command, object? payload = null, string key = "") =>
        // 口径 B（2026-10-06 用户选）：**纯数字的编号不再印**（编号直达取消）。
        // 但 "."（找兵）"0"（用游戏里选中的）"+/-""*/"（调数值/切换）必须留着 ——
        // 查行逻辑（Navigator.Rows.FirstOrDefault(KeyLabel.Contains(spec))）就是靠它找行的，
        // 全清会把 + / - / . 一起弄坏（这一轮我踩过，编译直接报 row 不存在）。
        new() { Kind = PanelRowKind.Action, KeyLabel = key, Action = action, Label = label, Detail = detail, Command = command, Payload = payload };

    private static PanelNavRow ValueActionRow(string action, string label, string value, string key) =>
        new() { Kind = PanelRowKind.Value, KeyLabel = key, Action = action, Label = label, Detail = value };

    private static PanelNavRow ValueRow(string action, string label, string value) =>
        new() { Kind = PanelRowKind.Value, Action = action, Label = label, Detail = value };

    private static PanelNavRow ToggleRow(string action, string label, bool on) =>
        new() { Kind = PanelRowKind.Toggle, Action = action, Label = label, State = on ? "已启用" : "未启用" };

    private static PanelNavRow StatusRow(string action, string label, string state, string detail = "") =>
        new() { Kind = PanelRowKind.Status, Action = action, Label = label, Detail = detail, State = state };

    /// <summary>
    /// 心灵控制的面板入口（「心灵控制」行上按 Num5 与 <b>Num1 直通</b> 共用这一段）：按了必须有反应 ——
    /// 真执行，或明说为什么现在不能（高风险功能没开 / 没连游戏 / 没装 patch），绝不静默。
    /// </summary>
    private void RunDuosheFromPanel(string source)
    {
        if (!ApplyOwnershipTransferCommand.CanExecute(null))
        {
            SetPanelResult($"心灵控制不可用（{source}）：" + OwnershipApplyPanelState());
            return;
        }
        SetPanelResult($"心灵控制：正在派发引擎动作…（{source}）");
        ApplyOwnershipTransferCommand.Execute(null);   // 结论由流程自己回填（含"已派发、等游戏"）
    }

    /// <summary>
    /// 按下一个键：在**当前层的行表**里找印着这个键的那一行，执行它。
    /// 行表和派发共用同一份数据，所以"面板上印的键"和"真按生效的键"不可能对不上；
    /// 这一层没分配的键会明说一句，不再像以前 Num1/3/7/9 那样静默无反应。
    /// </summary>
    public void ApplyPanelKey(PanelKeyCommand command)
    {
        // 诊断：面板键"到了宿主"之后到底落到哪一行。以前这里全是静默早退，实测无法归因。
        var diagRow = Navigator.Selected;
        _diagnostics.Log.Write("panel-key", new
        {
            command = command.ToString(),
            panelMode = PanelMode,
            page = _panelPage,
            adjustMode = _panelAdjustMode,
            positionMode = _panelPositionMode,
            selectedLabel = diagRow?.Label,
            selectedKind = diagRow?.Kind.ToString(),
            selectedAction = diagRow?.Action
        });
        if (command == PanelKeyCommand.None || !PanelMode) return;

        if (_panelPositionMode)
        {
            // 位置调整：只有鼠标点「位置」才进得来，这里只认方向键和 Num. 退出。
            switch (command)
            {
                case PanelKeyCommand.Digit8: MovePanel(0, -1); break;
                case PanelKeyCommand.Digit2: MovePanel(0, 1); break;
                case PanelKeyCommand.Digit4: MovePanel(-1, 0); break;
                case PanelKeyCommand.Digit6: MovePanel(1, 0); break;
                case PanelKeyCommand.Dot:
                    _panelPositionMode = false;
                    SetPanelResult(null);
                    break;
                default:
                    SetPanelResult("位置调整中：Num8/2/4/6 挪动，Num. 退出（发兵已屏蔽，避免挪面板时误发）");
                    break;
            }

            RefreshPanelRows();
            return;
        }

        // 数字键盘的标准用法，跟谁的私人习惯无关：8/2 上下、4/6 左右、5 确定；高亮的那一行
        // 就是 Num5 要执行的那一行（这也是用户最初的要求）。
        //
        // ★ 所有层一视同仁，不许按层门禁 ★ —— 曾经这里是 `_panelLayer == PanelLayerMain ? command : None`，
        // 理由是"找兵那几层每行都印了 1..N 的编号，按编号直达更快"。口径 B（2026-10-06 用户定：
        // "8 上 · 2 下 · 4 左 · 6 右 · 5 确认 · 0 返回 · 7/9 换页"）把行上的编号撤了（KeyDisplay 对纯数字返回空），
        // 于是那几层**屏幕上一个号都看不见**，数字键却还在按 KeyLabel 偷偷直达 —— 实测（夹具 TRACE）：
        // 在阵营层按 Num2 不是"往下走"，而是直接跳进"陆军·车辆"并一路把单位选掉、0 键还会选中第 10 个单位
        // 而不是返回。这不是功能，是看不见的暗门。所以方向/确定/返回/换页四个键在所有层都归它们自己。
        switch (command)
        {
            // 走开就退出调节态（用户定的口径："方向键走开自动退出"）。这句注释原来就在、代码却没做：
            // 调节态留着 ⇒ 接着按 4/6 会拿**新那一行**去改数，而新那行常常不是数值行 ⇒
            // AdjustPanelValue 什么都匹配不到，用户看到的是"按 4/6 没反应"（OCR 复核抓到）。
            case PanelKeyCommand.Digit8:
            case PanelKeyCommand.Digit2:
                _panelAdjustMode = false;
                MovePanelHighlight(command == PanelKeyCommand.Digit8 ? -1 : 1);
                RefreshPanelRows();
                return;
            // 口径 B：4/6 = 左/右 —— 在「值」行（数量/星级）上就是 −1 / +1。
            // 复用现成的查行机制（KeyLabel 含 "-" / "+" 的那行），不另造"当前高亮"访问器。
            case PanelKeyCommand.Digit4:
            case PanelKeyCommand.Digit6:
            {
                // 4/6 只在「调节态」里生效（先按 5 进数值行）。不在调节态就什么都不做 ——
                // 上一轮我做成全局生效，用户实机觉得别扭：导航时左右键不该偷偷改数。
                if (!_panelAdjustMode)
                {
                    SetPanelResult("先走到数值行按 5 进入调节");
                    RefreshPanelRows();
                    return;
                }

                var adjustSpec = command == PanelKeyCommand.Digit4 ? "-" : "+";
                // 调节态里的高亮**必须**是数值行（进调节态那一步已保证）。万一不是（换页/换层会重建行集），
                // 退回"按行上印的键查行"——原来写成 `Selected ?? 查表`，只要有高亮 Selected 就非空，
                // 后半截永远轮不到，等于把非数值行拿去改数而且一声不响（OCR 复核抓到）。
                var valueRow = Navigator.Selected is { Kind: PanelRowKind.Value } selectedValue
                    ? selectedValue
                    : Navigator.Rows.FirstOrDefault(candidate =>
                        candidate.Kind == PanelRowKind.Value && candidate.KeyLabel.Contains(adjustSpec, StringComparison.Ordinal));
                if (valueRow is null)
                {
                    SetPanelResult($"{DescribeKey(command)} 这层没有可调的数值");
                    RefreshPanelRows();
                    return;
                }

                AdjustPanelValue(valueRow.Action, command == PanelKeyCommand.Digit6 ? 1 : -1);
                RefreshPanelRows();
                return;
            }

            // 换页从 4/6 挪出来（4/6 按直觉就是左右）：7 = 上一屏、9 = 下一屏，PgUp/PgDn 也在 Map 里接了。
            case PanelKeyCommand.Digit7: PagePanelScreen(-1); RefreshPanelRows(); return;
            case PanelKeyCommand.Digit9: PagePanelScreen(1); RefreshPanelRows(); return;

            case PanelKeyCommand.Digit5:
            {
                // 用户 2026-10-06：「选中那个后点5确认，然后可以用4和6调大小，调完了再用5确认下就退出」。
                if (_panelAdjustMode)                       // 调节态里再按 5 = 确认并退出
                {
                    _panelAdjustMode = false;
                    SetPanelResult("已确认");
                    RefreshPanelRows();
                    return;
                }

                var cur = Navigator.Selected;
                // 只有「数值」行进调节态。位置格有自己的鼠标流程，按 5 进调节态的话 4/6 对它完全无效，
                // 用户只会觉得"进了个什么都按不动的状态"（OCR 复核抓到）。
                if (cur is not null && cur.Kind is PanelRowKind.Value)
                {
                    _panelAdjustMode = true;                 // 进入调节态
                    SetPanelResult($"调节 {cur.Label}：4 减 / 6 加 · 5 确认");
                    RefreshPanelRows();
                    return;
                }

                ExecutePanelHighlight();                    // 普通行：照旧执行
                return;
            }

            // ★ Num1 = 心灵控制直通（用户 2026-10-07：「我不希望这个心灵控制分成两个步骤，我只想在面板按下个
            // 快捷键就可以完成心灵控制了」）★ —— 不用先走到那一行、不用先翻到功能页；任何层任何页都认。
            // 闸门照旧（高风险功能没开就明说，不静默）；位置调整态在上面就 return 了，天然屏蔽（挪面板时不会误心灵控制）。
            case PanelKeyCommand.Digit1:
                _panelAdjustMode = false;      // 直通动作不留调节态（方向键本来也会清）
                RunDuosheFromPanel("Num1 直通");
                RefreshPanelRows();
                return;

            // 用户 2026-10-06 定的口径：0 = 返回（上一层）。原来 0 是"用游戏里选中的"，
            // 那个功能改成普通一行（8/2 走到 + 5 执行），把 0 让给"返回"。
            case PanelKeyCommand.Digit0:
                PanelFindBack();
                RefreshPanelRows();
                return;
            default: break;
        }

        var spec = PanelKeyMap.Spec(command);
        // ★ 口径 B（2026-10-06 用户：编号直达整个拆掉）★：纯数字的行标签不再当入口。
        // 行上根本不印编号（KeyDisplay 对纯数字返回空），数字键却有暗门按 KeyLabel 直达 ——
        // 用户以为"按 2 是往下走"，实际是跳去第 2 行（夹具 TRACE 实录：在阵营层按 2 直接选中"陆军·车辆"）。
        // 数字键 1/3 没有自己的 case，正好走这条尾巴，所以必须在这里挡住。
        if (spec.Length == 0 || char.IsAsciiDigit(spec[0]))
        {
            SetPanelResult($"{DescribeKey(command)} 这一层没分配（Num8/2 选行 · Num5 执行 · 0 返回）");
            RefreshPanelRows();
            return;
        }

        // 口径 B 后这里只服务 ".", "+/", "*/" —— 纯数字编号已在上一步挡掉。
        var row = Navigator.Rows.FirstOrDefault(candidate => candidate.KeyLabel.Contains(spec, StringComparison.Ordinal));
        if (row is null)
        {
            SetPanelResult($"{DescribeKey(command)} 在当前这层没分配");
            RefreshPanelRows();
            return;
        }

        if (row.Kind == PanelRowKind.Value)
        {
            // 数值行一行印两个键（"+−" / "＊／"）：是加还是减，看按的是哪个键。
            AdjustPanelValue(row.Action, spec is "+" or "*" ? 1 : -1);
            RefreshPanelRows();
            return;
        }

        ActivatePanelRow(row);
    }

    /// <summary>只有"能执行的行"才接受高亮：表头和结果行跳过去，不然 8/2 会卡在不能用的行上。</summary>
    private static bool IsActionable(PanelNavRow row) =>
        row.Kind is PanelRowKind.Action or PanelRowKind.Toggle or PanelRowKind.Value or PanelRowKind.Position;

    /// <summary>Num8/Num2 = 上下移动高亮（到头就停，不绕圈）。</summary>
    private void MovePanelHighlight(int direction)
    {
        var rows = Navigator.Rows;
        if (rows.Count == 0) return;
        var at = Navigator.SelectedIndex;
        if (at < 0)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var candidate = direction > 0 ? i : rows.Count - 1 - i;
                if (IsActionable(rows[candidate])) { Navigator.Select(candidate); return; }
            }

            return;
        }

        for (var next = at + direction; next >= 0 && next < rows.Count; next += direction)
        {
            if (IsActionable(rows[next])) { Navigator.Select(next); return; }
        }
    }

    /// <summary>Num4/Num6 = 左右：找兵的单位屏之间翻。</summary>
    private void PagePanelScreen(int direction)
    {
        // 主层：Num4/Num6 = 左右换页（启动 / 增援 / 功能 / 诊断）。
        StepPanelPage(direction);
    }

    /// <summary>Num5 = 执行高亮那一行（数字键盘中间那个键，标准含义就是"确定"）。</summary>
    private void ExecutePanelHighlight()
    {
        var row = Navigator.Selected;
        if (row is null || !IsActionable(row))
        {
            _diagnostics.Log.Write("panel-execute", new { result = "no-actionable-row", label = row?.Label, kind = row?.Kind.ToString(), rows = Navigator.Rows.Count });
            SetPanelResult("先用 Num8/Num2 选一行，再按 Num5");
            RefreshPanelRows();
            return;
        }

        // 位置格**只能鼠标点**（用户定的口径：战斗中不会有人把鼠标挪到那个角上去点）。
        // 键盘能进的话：8/2 一路走到底再按一下 5，面板就切成"挪位置"，从此 8/2/4/6 全变成挪面板 ——
        // 正是当初要防的误触。
        if (row.Kind == PanelRowKind.Position)
        {
            SetPanelResult("面板位置要用鼠标点那一格（键盘不进，防误触）");
            RefreshPanelRows();
            return;
        }

        ActivatePanelRow(row);
    }

    private static string DescribeKey(PanelKeyCommand command)
    {
        var spec = PanelKeyMap.Spec(command);
        return spec.Length == 0 ? "这个键" : $"Num{spec}";
    }

    private void MovePanel(int dx, int dy)
    {
        OverlayPanelX = Math.Clamp(_overlayPanelX + dx, 0, 8192);
        OverlayPanelY = Math.Clamp(_overlayPanelY + dy, 0, 8192);
    }

    private void AdjustPanelValue(string which, int delta)
    {
        switch (which)
        {
            case "count":
                ReinforcementCountText = AdjustPanelNumber(ReinforcementCountText, delta, ReinforcementSettings.MinCount, ReinforcementSettings.MaxCount);
                SetPanelResult($"数量 = {ReinforcementCountText}");
                break;
            case "rank":
                ReinforcementRankText = AdjustPanelNumber(ReinforcementRankText, delta, ReinforcementSettings.MinRank, ReinforcementSettings.MaxRank);
                SetPanelResult($"星级 = {ReinforcementRankText}");
                break;
        }
    }

    /// <summary>面板上调钱/电力/秘技点：按 step 加减，夹在合法区间里。</summary>
    private static string AdjustPanelMoney(string text, int delta, int step)
    {
        var current = long.TryParse(text, out var parsed) ? parsed : 0;
        var next = Math.Clamp(current + ((long)delta * step), 0, 999_999_999);
        return next.ToString();
    }

    private static string AdjustPanelNumber(string text, int delta, int min, int max)
    {
        var current = int.TryParse(text, out var parsed) ? Math.Clamp(parsed, min, max) : min;
        return Math.Clamp(current + delta, min, max).ToString();
    }

    /// <summary>
    /// 执行面板上的一行。鼠标点它、和按它印着的那个键，走的是**同一个入口**，不复制逻辑。
    /// </summary>
    public void ActivatePanelRow(PanelNavRow? row)
    {
        var resultBefore = _panelResult;
        if (row is not null)
        {
            switch (row.Kind)
            {
                case PanelRowKind.Position:
                    // 唯一会切模式的格子，而且键盘没有入口 —— 只有鼠标点到它才进，
                    // 用户抱怨的"爬到它上面，上下左右就变成挪面板"从根上没了。
                    _panelPositionMode = !_panelPositionMode;
                    SetPanelResult(_panelPositionMode ? "位置调整：Num8/2/4/6 挪动，Num. 退出" : null);
                    break;
                case PanelRowKind.Toggle:
                    // ★ 2026-10-11 修（仪表实锤）★ 功能型开关的 action 是 "feat:<RAWNAME>"，而
                    // TogglePanelRow 只认修改器自己的开关（autofloat/panelmode/…），没有一个 feat 分支 ⇒
                    // 掉出 switch、静默什么都不做：面板上「走到"快速建造"那行按 5」永远没反应，
                    // 而同一场的 Action 行（钱）正常。功能型开关一律交给 RunPanelAction（它有功能命令的
                    // 分派与 IsToggle 显示），只有它认不出来时才回落到修改器开关表。
                    if (!TogglePanelRow(row.Action)) RunPanelAction(row);
                    break;
                case PanelRowKind.Action:
                case PanelRowKind.Value:
                    RunPanelAction(row);
                    break;
                default:
                    break;   // 表头/结果行：点它没用，也不该有用
            }

            // 面板执行完动作后把结果收进面板结果（动作自己没设过才收，别覆盖更精确的回报）。
            // 面板只认"它自己那次操作"的结果，就不会漏出桌面的陈旧消息 ——
            // 曾出现"已找到 4 个启动方式"（桌面扫描结果）跑到游戏画面里。
            if (row.Kind is PanelRowKind.Action or PanelRowKind.Value
                && string.Equals(_panelResult, resultBefore, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(StatusMessage))
            {
                SetPanelResult(StatusMessage);
            }
        }

        RefreshPanelRows();
    }

    /// <summary>
    /// 面板上真实的动作。发兵走的就是主窗那条「We Need Back」命令，不复制逻辑。
    /// </summary>
    private void RunPanelAction(PanelNavRow row)
    {
        if (RunLiteralPanelAction(row.Action) || RunFindAction(row)) return;
        switch (row.Action)
        {
            // 面板上按一次 = 在现有数值上再加一档，然后照旧执行这条功能：
            // 按一下加一次，接着按就接着加（用户对这个功能的预期就是这样）。
            case "feat:Moeny":
                MoneyAmountText = AdjustPanelMoney(MoneyAmountText, 1, 10000);
                SetPanelResult(null);        // 结果由功能自己回报
                break;
            case "feat:Power":
                PowerValueText = AdjustPanelMoney(PowerValueText, 1, 10000);
                SetPanelResult(null);
                break;
            case "send":
                if (ReinforcementCallCommand is { } call && call.CanExecute(null))
                {
                    SetPanelResult(null);        // 结果由功能自己回报（成功/失败原因都进结果行）
                    call.Execute(null);
                }
                else
                {
                    SetPanelResult("发兵入口不可用：先装 patch 并连上游戏");
                }

                return;
            case "usegame":
                SetPanelResult(null);
                if (ReadSelectedUnitCodeCommand.CanExecute(null)) ReadSelectedUnitCodeCommand.Execute(null);
                return;
            // 心灵控制：按了必须**有反应**（真执行 / 或明说为什么不能），绝不静默。
            case "duoshe:apply":
                RunDuosheFromPanel("Num5");
                return;
            case "duoshe:restore":
                if (RestoreOwnershipCommand.CanExecute(null))
                {
                    SetPanelResult("解除心灵控制：正在写回快照里的旧 owner…");
                    RestoreOwnershipCommand.Execute(null);
                }
                else
                {
                    SetPanelResult(_lastOwnershipApply is null
                        ? "还原不可用：还没有心灵控制快照（先控制一次）"
                        : "还原不可用：先点「连接游戏」");
                }
                return;
            case "slot" when row.Payload is int slot:
                ActivatePanelSlot(slot);
                return;
            case "unit-prev":
                StepPanelUnit(-1);
                return;
            case "unit-next":
                StepPanelUnit(1);
                return;
            case "find":
            {
                // 阵营**必须由玩家自己选**（2026-10-06 他否掉了我做的"自动判定"）：
                // 我并不知道他这一局玩哪个阵营，而且他要的很可能就是**别人阵营的单位**
                //（复制/缴获过来的）。所以固定从阵营层进：阵营 → 游戏分类 → 单位。
                _panelLayer = PanelLayerFaction;
                _panelFaction = null;
                _panelDomain = null;
                _panelScreen = 0;
                SetPanelResult("选单位：Num8/2 选阵营，Num5 执行");
                return;
            }
            case "find-back":
                PanelFindBack();
                return;
            default:
                break;
        }

        if (row.Command is { } command && command.CanExecute(row.Payload))
        {
            _diagnostics.Log.Write("panel-execute", new { result = "command", action = row.Action, label = row.Label });
            command.Execute(row.Payload);
        }
        else
        {
            // 静默点（2026-10-10 实锤）：命令为空或 CanExecute 为假时，以前一个字都不留 ——
            // 用户看到的就是"面板按了没反应"，而代码里根本不是没走到。
            _diagnostics.Log.Write("panel-execute", new
            {
                result = row.Command is null ? "no-command" : "can-execute-false",
                action = row.Action,
                label = row.Label,
                patchesInstalled = ArePatchesInstalled
            });
        }
    }

    /// <summary>找兵三层的走法：阵营 → 游戏分类 → 一屏 10 个（8/2 走到 + 5 执行，Num+/Num- 翻屏）。</summary>
    private bool RunFindAction(PanelNavRow row)
    {
        switch (row.Action)
        {
            case "findfaction" when row.Payload is string faction:
                _panelFaction = faction;
                _panelDomain = null;
                _panelScreen = 0;
                _panelLayer = PanelLayerDomain;
                SetPanelResult("Num8/2 走到要用的阵营，Num5 执行：" + faction);
                return true;
            case "finddomain" when row.Payload is UnitDomain domain:
                _panelDomain = domain;
                _panelScreen = 0;
                _panelLayer = PanelLayerUnit;
                SetPanelResult("Num8/2 走到要用的分类，Num5 执行：" + UnitTaxonomy.ShortDomainLabel(domain));
                return true;
case "find-prev":
                _panelScreen = Math.Max(0, _panelScreen - 1);
                SetPanelResult($"第 {_panelScreen + 1} 屏");
                return true;
            case "find-next":
                _panelScreen++;
                SetPanelResult($"第 {_panelScreen + 1} 屏");
                return true;
            case "find-first":
                _panelScreen = 0;
                return true;
            case "find-last":
                _panelScreen = int.MaxValue;   // 单位层会把它夹到最后一屏
                return true;
            case "findunit" when row.Payload is ReinforcementUnitEntry unit:
                SelectPanelUnit(unit);
                return true;
            default:
                return false;
        }
    }

    private void PanelFindBack()
    {
        switch (_panelLayer)
        {
            case PanelLayerUnit:
                _panelLayer = PanelLayerDomain;
                break;
            case PanelLayerDomain:
                _panelLayer = PanelLayerFaction;
                _panelDomain = null;
                break;
            default:
                _panelLayer = PanelLayerMain;
                _panelFaction = null;
                _panelDomain = null;
                break;
        }

        SetPanelResult(null);
    }

    /// <summary>选中一个兵种：与主窗「选择单位」同一个写入路径，只是入口换成小键盘。</summary>
    private void SelectPanelUnit(ReinforcementUnitEntry unit)
    {
        SelectedReinforcementUnit = unit;
        RememberPanelUnit(unit);
        _panelLayer = PanelLayerMain;
        _panelFaction = null;
        _panelDomain = null;
        // ★ 选完兵回「增援」页 ★ —— 面板页 = 主窗页签（一份状态）。留在「选单位」页的话，那一页的行集
        // 就是阵营列表，选中的结果一个字都看不见（夹具量过：那页没有结果行），而"选兵"本来就是为"发兵"服务的：
        // 增援页抬头正好报「送什么 + 上次结果」，收藏槽/数量/星级/呼叫增援也都在那儿，接着按 Num5 就能发。
        SelectPanelPage("增援");
        SetPanelResult($"已选：{unit.Name}（{unit.Faction} · {UnitTaxonomy.ShortDomainLabel(unit.Domain)}）");
    }

    /// <summary>
    /// Num8/Num2 = 上一个/下一个兵种。在"收藏槽 + 最近用过"里循环；一个都没有时
    /// 就从目录里先给一个起点 —— 关键是按键**必须有看得见的反应**。
    /// </summary>
    private void StepPanelUnit(int direction)
    {
        var list = PanelSlotUnits();
        if (list.Count == 0)
        {
            var seed = ReinforcementUnits.OrderBy(unit => unit.Name, StringComparer.Ordinal).FirstOrDefault();
            if (seed is null) { SetPanelResult("目录是空的"); return; }
            SelectedReinforcementUnit = seed;
            RememberPanelUnit(seed);
            SetPanelResult($"当前兵种：{seed.Name}（还是空的收藏槽，按 Num. 找兵挑）");
            return;
        }

        var at = SelectedReinforcementUnit is null
            ? -1
            : list.FindIndex(unit => unit.Code == SelectedReinforcementUnit.Code);
        var next = list[((at + direction) % list.Count + list.Count) % list.Count];
        SelectedReinforcementUnit = next;
        RememberPanelUnit(next);
        SetPanelResult($"当前兵种：{next.Name}（第 {list.IndexOf(next) + 1}/{list.Count} 个收藏 · ×{ReinforcementCountText} ★{ReinforcementRankText}）");
    }

    /// <summary>按槽号换兵：槽里是空的就说明一句，不静默。</summary>
    private void ActivatePanelSlot(int slot)
    {
        var unit = PanelSlotUnits().ElementAtOrDefault(slot);
        if (unit is null)
        {
            SetPanelResult($"第 {slot + 1} 个槽是空的：用 Num. 找兵，选中就会自动填进来");
            return;
        }

        SelectedReinforcementUnit = unit;
        RememberPanelUnit(unit);
        SetPanelResult($"当前兵种：{unit.Name}（×{ReinforcementCountText} ★{ReinforcementRankText}）");
    }

    /// <summary>最近用过的排最前，最多留 8 个（槽的容量）。</summary>
    private void RememberPanelUnit(ReinforcementUnitEntry unit)
    {
        _panelRecent.RemoveAll(item => item.Code == unit.Code);
        _panelRecent.Insert(0, unit);
        if (_panelRecent.Count > PanelSlotCount)
        {
            _panelRecent.RemoveRange(PanelSlotCount, _panelRecent.Count - PanelSlotCount);
        }
    }

    /// <summary>
    /// 8 个槽：用户存过的预设优先（那份是持久化的），剩下的用最近用过的兵补满。
    /// 按需算，不做缓存 —— 删了预设、换了兵，下一次刷新就是新的。
    /// </summary>
    private List<ReinforcementUnitEntry> PanelSlotUnits()
    {
        var slots = new List<ReinforcementUnitEntry>();
        foreach (var preset in ReinforcementPresets)
        {
            if (slots.Count >= PanelSlotCount) break;
            var unit = ReinforcementUnitCatalog.FindByCode(ReinforcementUnits, preset.UnitId);
            if (unit is not null && slots.All(item => item.Code != unit.Code)) slots.Add(unit);
        }

        foreach (var unit in _panelRecent)
        {
            if (slots.Count >= PanelSlotCount) break;
            if (slots.All(item => item.Code != unit.Code)) slots.Add(unit);
        }

        return slots;
    }

    /// <summary>结果行最多显示这么多字（面板只有 360×440，长了会把下面的行挤出屏幕）。</summary>
    private const int PanelResultMaxChars = 44;

    /// <summary>面板底部那一行：面板自己最近一次结果优先，否则显示主窗状态（发兵失败原因就在那儿）。</summary>
    public string PanelResultText
    {
        get
        {
            // 没有面板自己的结果时回退到全局状态：夹具钉着"发兵失败原因必须进面板结果行"
            // （用户以前只看到"没反应"）。桌面遗留的陈旧消息由 PanelMode 开启时清掉，见下。
            var text = string.IsNullOrWhiteSpace(_panelResult)
                ? (string.IsNullOrWhiteSpace(StatusMessage) ? "选一行按 Num5" : StatusMessage)
                : _panelResult!;
            return text.Length <= PanelResultMaxChars ? text : text[..PanelResultMaxChars] + "…";
        }
    }

    private void SetPanelResult(string? text)
    {
        _panelResult = text;
        OnPropertyChanged(nameof(PanelResultText));
    }

    /// <summary>
    /// Rows that write a literal number (快捷数量/星级). Uses the same clamp as
    /// every other panel number, so a hand-edited literal can never go out of range.
    /// </summary>
    private bool RunLiteralPanelAction(string action)
    {
        if (action.StartsWith("setcount:", StringComparison.Ordinal))
        {
            ReinforcementCountText = AdjustPanelNumber(action["setcount:".Length..], 0, ReinforcementSettings.MinCount, ReinforcementSettings.MaxCount);
            return true;
        }
        if (action.StartsWith("setrank:", StringComparison.Ordinal))
        {
            ReinforcementRankText = AdjustPanelNumber(action["setrank:".Length..], 0, ReinforcementSettings.MinRank, ReinforcementSettings.MaxRank);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 面板已经不再分页（它只做打仗的事：发兵/换兵/调数值/找兵），这里只把主窗的页签名
    /// 同步过去，不再改面板的行集合。
    /// </summary>
    public void SelectPanelPage(string? page)
    {
        // 必须是面板页签（游戏内动词），不是主窗四页 —— 用错会让新页被静默拒绝，
        // 表现为按 Num6 页面停在原地（夹具 M7 抓到过）。非法值什么都不动。
        if (page is null || !ConsoleNavigation.IsPanelSection(page)) return;

        // 进「选单位」页把选择器重置回第一层（阵营）：不重置会带着上次那层进去，
        // 夹具 M7「进选单位页是第 1 层」抓到过。这是用户要的"每次从头挑"。
        // ★ 换到**别的**页必须把层**拉回主层**：以前只写了上面那一个分支，于是
        //   "从选单位点回功能页"会留在阵营层 —— 页签写着「功能」，行板却列着几个阵营。
        //   键盘换页走 StepPanelPage（它自己把层置回 Main），所以只有鼠标点页签这一条路会踩到。
        _panelLayer = page == "选单位" ? PanelLayerFaction : PanelLayerMain;
        _panelFaction = null;
        _panelDomain = null;
        _panelScreen = 0;

        // 入口只有这一个：写唯一的页状态，面板页/行集合在属性里级联。
        OverlayPanelSection = page;
    }

    /// <summary>返回 true = 这条 action 是修改器自己的开关，已处理；false = 不认（交给功能分派）。</summary>
    private bool TogglePanelRow(string action)
    {
        switch (action)
        {
            case "autofloat": AutoFloatAfterLaunch = !AutoFloatAfterLaunch; break;
            case "inspecthooks": InspectHooksBeforeInstall = !InspectHooksBeforeInstall; break;
            case "nonstandard": AllowNonStandardVersion = !AllowNonStandardVersion; break;
            case "autoevidence": AutoCollectEvidenceOnExit = !AutoCollectEvidenceOnExit; break;
            case "safesampling": SafeSamplingEnabled = !SafeSamplingEnabled; break;
            case "panelmode": PanelMode = !PanelMode; break;
            case "overlay": OverlayEnabled = !OverlayEnabled; break;
            case "showallmods": ShowAllModUnits = !ShowAllModUnits; break;
            default: return false;
        }

        return true;
    }

    private static readonly HashSet<string> PanelSourceProperties = new(StringComparer.Ordinal)
    {
        nameof(ReinforcementCountText), nameof(ReinforcementRankText), nameof(ReinforcementUnitIdText),
        nameof(SelectedReinforcementUnit), nameof(SelectedLauncher), nameof(LauncherPath), nameof(LauncherArguments),
        nameof(OverlayPanelX), nameof(OverlayPanelY), nameof(OverlayPanelSection), nameof(PanelMode),
        nameof(SafeSamplingEnabled), nameof(AllowNonStandardVersion), nameof(AllowUnverifiedWorldPositionFeatures),
        nameof(InspectHooksBeforeInstall), nameof(AutoCollectEvidenceOnExit), nameof(AutoFloatAfterLaunch),
        nameof(OverlayEnabled), nameof(HookSummary), nameof(FeatureSummary), nameof(DiagnosticsSummary),
        // 单位按 mod 分层：开关/启动器一变，找兵行集要重建（当前 mod 变了，可选单位/阵营随之收敛）。
        nameof(ShowAllModUnits),
        nameof(LastExitDescription), nameof(ConnectionState), nameof(ProbeSummary),
        // 结果行显示的就是主窗状态：发兵成功/失败（"游戏未解析单位类型"）从这里进面板。
        nameof(StatusMessage), nameof(PanelResultText)
    };

    private void OnPanelSourceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not null && PanelSourceProperties.Contains(e.PropertyName)) RefreshPanelRows();
    }

    private void OnFeatureChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RefreshPanelRows();

    private void OnNavigatorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PanelNavigator.Selected) or nameof(PanelNavigator.SelectedIndex))
        {
            OnPropertyChanged(nameof(PanelSelectionLabel));
            OnPropertyChanged(nameof(PanelPageLabel));
        }
    }

    private void StartHotkeys()
    {
        var generation = ++_hotkeyGeneration;
        // 一个功能可以挂多个手势（用 "|" 分隔）：有的键盘上 F5 之类的键发的是媒体键，
        // 只认一个手势就等于那条功能永远点不动（2026-10-11 实测踩到）。
        var bindings = Groups
            .SelectMany(group => group.Features)
            .SelectMany(item => (item.Hotkey ?? string.Empty)
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(text => (item, text)))
            .Select(pair => HotkeyGesture.TryParse(pair.text, out var gesture)
                ? new HotkeyFeatureBinding(gesture, pair.item.Feature, () =>
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (generation == _hotkeyGeneration && IsTargetGameForeground())
                        {
                            pair.item.ExecuteFromHotkey();
                        }
                    }), DispatcherPriority.Background))
                : null)
            .Where(binding => binding is not null)
            .Cast<HotkeyFeatureBinding>()
            .ToArray();

        // 引擎动作接口（发兵/给钱等）的**独立触发键**：F8。
        // 用户明确指出：不能再借用他已有的功能键（J=他自己的呼叫增援），否则两者互相干扰、
        // 他也分不清是谁起的作用。这里按 F8 只做一件事：写一个请求文件（带递增序号），
        // 游戏侧的覆盖层 DLL 读到序号变化就去调用引擎动作。失败也不影响任何现有功能。
        // 键位是自查后定的：#106 = 小键盘 `*`（VK 0x6A）。
        // 已核对两边：修改器 32 条功能占的是 Ctrl+F1~F12 / 符号键 / Delete / PgUp / PgDn；
        // 游戏自己（Profiles\h\hotkeys.ini）占了 60 个键（F1~F12 = 侧边栏、J/K/L = 视角书签、字母几乎全占）。
        // 只有小键盘 * 两边都空着 —— F8 之类会同时触发游戏的侧边栏按钮，所以不能用。
        if (HotkeyGesture.TryParse("#106", out var engineGesture))
        {
            var carrier = Groups.SelectMany(group => group.Features).FirstOrDefault();
            if (carrier is not null)
            {
                bindings = bindings.Append(new HotkeyFeatureBinding(engineGesture, carrier.Feature, () =>
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (generation != _hotkeyGeneration || !IsTargetGameForeground()) return;
                        WriteEngineRequestFile();
                    }), DispatcherPriority.Background))).ToArray();
            }
        }

        // ★ 心灵控制的键盘快捷键（用户 2026-10-07：「我是说是键盘的快捷键…就和我们之前那些功能一样的快捷键啊」）★
        // 走的是**按钮/面板同一条路**：高风险闸门 → 落盘快照 → 引擎动作 0x09 → 写后回读 → 状态与面板结果回填（可一键还原）。
        // 刻意**不用**清单里那条裸触发：裸触发不落快照，事后没有「解除心灵控制」可点。
        if (HotkeyGesture.TryParse(OwnershipHotkey, out var ownershipGesture))
        {
            var carrier = Groups.SelectMany(group => group.Features).FirstOrDefault();
            if (carrier is not null)
            {
                bindings = bindings.Append(new HotkeyFeatureBinding(ownershipGesture, carrier.Feature, () =>
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (generation != _hotkeyGeneration || !IsTargetGameForeground()) return;
                        // 记一条**能归因**的日志：不然事后分不清这次心灵控制是快捷键、面板行还是主窗按钮触发的。
                        _diagnostics.Log.Write("duoshe-hotkey", new
                        {
                            source = OwnershipHotkey,
                            canExecute = ApplyOwnershipTransferCommand.CanExecute(null),
                            state = OwnershipApplyPanelState()
                        });
                        if (!ApplyOwnershipTransferCommand.CanExecute(null))
                        {
                            // 按了必须有反应：闸门没开/没连游戏/没装 patch 时明说，绝不静默。
                            StatusMessage = $"心灵控制（{OwnershipHotkey}）不可用：" + OwnershipApplyPanelState();
                            PublishOwnershipToPanel($"心灵控制不可用（{OwnershipHotkey}）：" + OwnershipApplyPanelState());
                            return;
                        }
                        StatusMessage = $"心灵控制（{OwnershipHotkey}）：正在派发引擎动作…";
                        ApplyOwnershipTransferCommand.Execute(null);   // 结论由流程自己回填
                    }), DispatcherPriority.Background))).ToArray();
            }
        }

        _hotkeyDispatcher.Update(bindings, enabled: true);
        _keyboardHook.Install();
    }

    /// <summary>
    /// 引擎路（走游戏自己的动作接口）的触发请求：只写"序号"，由游戏侧 DLL 决定这一发做什么。
    /// 这样调整"下一步试什么"不用改这里、也不用重新发布修改器。
    /// </summary>
    private static void WriteEngineRequestFile()
    {
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ra3-engine-request.txt");
            var next = 1;
            if (System.IO.File.Exists(path) &&
                int.TryParse(System.IO.File.ReadAllText(path).Trim(), out var current) && current > 0 && current < int.MaxValue)
            {
                next = current + 1;
            }
            System.IO.File.WriteAllText(path, next.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch
        {
            // 写不进去只是少一次请求，绝不影响主流程。
        }
    }

    private void StopHotkeys()
    {
        _hotkeyGeneration++;
        _hotkeyDispatcher.Update([], enabled: false);
        _keyboardHook.Uninstall();
    }

    /// <summary>诊断入口：功能执行被静默拦下时留痕（三条输入路径共用）。</summary>
    public void LogFeatureBlocked(string rawName, string reason) =>
        _diagnostics.Log.Write("feature-blocked", new { feature = rawName, reason });

    private void OnKeyboardHookKeyDown(object? sender, KeyboardHookEventArgs e)
    {
        // 诊断：F 键 / 带修饰键的按键才留痕（普通字母会刷屏）。
        var isHotkeyShape = e.Modifiers != HotkeyModifiers.None || e.VirtualKey is >= 0x70 and <= 0x87;
        if (!IsTargetGameForeground())
        {
            if (isHotkeyShape)
            {
                _diagnostics.Log.Write("hotkey-not-foreground",
                    new { vk = $"0x{e.VirtualKey:X2}", modifiers = e.Modifiers.ToString(), note = "目标进程不在前台，整条热键入口被早退" });
            }
            return;
        }

        // 看门狗：进任何按键前先清"物理上已松开却还记着按下"的键，否则那个热键会静默失效一整个会话。
        var stuck = _hotkeyDispatcher.ResetStuckKeys(IsKeyPhysicallyDown);
        if (stuck > 0)
        {
            _diagnostics.Log.Write("hotkey-stuck-cleared", new { count = stuck });
        }

        // The hook runs on its installing UI thread. Decide suppression immediately,
        // while bindings enqueue the actual memory writes after this callback returns.
        // M2a: a trainer-level toggle key that is not a game feature. Consumes only this
        // exact gesture; no second hook and no input capture beyond the existing one.
        // M2a/M4: a trainer-level toggle key that is not a game feature. Ins is the primary
        // toggle (owner's pick); Ctrl+F9 stays as a fallback for muscle memory. Consumes
        // only these gestures: no second hook, no input capture beyond the existing one.
        // NumLock off: the numpad reports its navigation twins instead of digits, and the numpad's 0
        // becomes the same key code as Insert. The dedicated Insert/arrows/Home/End/Del are extended
        // while the numpad's are not, so the flag separates them. Without this, one numpad press both
        // missed the panel and switched it off - measured live: the panel went dark at 21:41, right
        // after numpad use, and never came back in that session.
        if (PanelMode && e.Modifiers == HotkeyModifiers.None)
        {
            var numpadCommand = PanelKeyMap.MapNumpadNavigation(e.VirtualKey, e.IsExtended);
            if (numpadCommand != PanelKeyCommand.None)
            {
                ApplyPanelKey(numpadCommand);
                e.Handled = true;
                return;
            }
        }

        // Ins 开关面板模式，但**只有真 Insert（扩展键）**能把它关掉：NumLock 关掉时小键盘的 0 就是同一个
        // 键码，那个形式只能开、不能关（关面板只能是有意为之）。
        if ((e.VirtualKey == PanelKeyMap.Insert && e.Modifiers == HotkeyModifiers.None
                && (e.IsExtended || !PanelMode))
            || (e.VirtualKey == 0x78 /* F9 */ && e.Modifiers == HotkeyModifiers.Control))
        {
            TogglePanelMode();
            e.Handled = true;
            return;
        }

        e.Handled = _hotkeyDispatcher.TryDispatch(e.VirtualKey, e.Modifiers);
        if (isHotkeyShape)
        {
            _diagnostics.Log.Write("hotkey-key", new
            {
                vk = $"0x{e.VirtualKey:X2}",
                modifiers = e.Modifiers.ToString(),
                handled = e.Handled,
                outcome = _hotkeyDispatcher.LastOutcome,
                panelMode = PanelMode,
                patchesInstalled = ArePatchesInstalled
            });
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private static bool IsKeyPhysicallyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private void OnKeyboardHookKeyUp(object? sender, int virtualKey)
    {
        _hotkeyDispatcher.Release(virtualKey);
    }

    private bool IsTargetGameForeground()
    {
        return _targetProcessId is int targetProcessId &&
            _foregroundWindowProcess.GetForegroundProcessId() == targetProcessId;
    }
}