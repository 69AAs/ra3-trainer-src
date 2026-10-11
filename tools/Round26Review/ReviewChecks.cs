#if !BASELINE
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ra3Trainer.App;
using Ra3Trainer.App.ViewModels;
using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Memory;
using Ra3Trainer.Core.Runtime;

internal static class ReviewChecks
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
    static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => {}, DispatcherPriority.ApplicationIdle);
    static IEnumerable<DependencyObject> Tree(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    public static void Window(Window window, MainViewModel vm)
    {
        var tree = Tree(window).ToArray();
        // 主窗页签在重构后是四页（启动/增援/功能/诊断），旧断言钉的是三页版（增援/功能/维护）。
        // 面板页签是另一套（游戏内动词）——两者故意不同，一起钉住，谁被改回去都会当场炸。
        Check(ConsoleNavigation.Sections.SequenceEqual(new[] { "启动", "增援", "功能", "诊断" }), "four main navigation destinations");
        Check(ConsoleNavigation.PanelSections.SequenceEqual(new[] { "功能", "增援", "选单位" }), "panel tabs are the in-game verbs");
        Check(window.FindName("PresetsPage") is null && window.FindName("Nav预设") is null, "no separate preset page");
        Check(!tree.OfType<Button>().Any(b => b.Content?.ToString()?.Contains("连发") == true), "no burst controls");
        if (window is FloatingWindow)
            Check(!tree.Any(c => c is TextBox or ComboBox or Popup), "float contains no keyboard inputs or popup selectors");
        else
        {
            var input = (TextBox)window.FindName("CountValue");
            input.Text = "37"; input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            Check(vm.ReinforcementCountText == "37", "main accepts typed quantity immediately");
        }
        var editor = window is FloatingWindow f ? f.ReinforcementEditor : ((MainWindow)window).ReinforcementEditor;
        if (window is MainWindow console)
        {
            // 主窗默认停在「启动」页，而增援控件在「增援」页里：不切页的话可视树里根本没有那些按钮
            // （FindName 走 XAML 名字域照样找得到，VisualTreeHelper 找不到 —— 默认页从"增援"改成"启动"
            //   之后，下面那些 Single() 就是在这里当场炸的）。按用户真实操作先切到「增援」页。
            console.Navigation.Selected = "增援"; Pump();
            tree = Tree(window).ToArray();
            Check(tree.OfType<Button>().Any(b => ReferenceEquals(b.Command, editor.SetCountCommand)), "增援页的快捷数量按钮已实例化");
        }
        if (window is not FloatingWindow)
        {
        foreach (var value in new[] { 1, 5, 10, 20, 50 })
        {
            var button = tree.OfType<Button>().Single(b => ReferenceEquals(b.Command, editor.SetCountCommand) && b.CommandParameter?.ToString() == value.ToString());
            button.Command!.Execute(button.CommandParameter);
            Check(vm.ReinforcementCountText == value.ToString(), "one click sets " + value);
        }
        editor.SetCountCommand.Execute("100");
        Check(vm.ReinforcementCountText == "50" && editor.Feedback.Contains("尚未"), "unverified 100 rejected without silently changing quantity");
        vm.ReinforcementCountText = "20";
        var count = (UIElement)window.FindName("CountValue");
        count.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
        Check(vm.ReinforcementCountText == "25", "real routed wheel behavior adjusts by five without OS input injection");
        var repeat = (RepeatButton)window.FindName("IncreaseCountButton");
        Check(!repeat.Focusable && repeat.Delay == 350 && repeat.Interval == 75, "quantity uses native RepeatButton hold semantics without focus");
        repeat.Command!.Execute(null);
        Check(vm.ReinforcementCountText == "26", "repeat command adjusts and shares main state");
        var presets = (ItemsControl)window.FindName("PresetSelector");
        var presetButton = Tree(presets).OfType<Button>().First(b => b.CommandParameter is ReinforcementPreset);
        var preset = (ReinforcementPreset)presetButton.CommandParameter;
        presetButton.Command!.Execute(preset);
        Check(vm.ReinforcementCountText == preset.Count.ToString() && vm.ReinforcementRankText == preset.Rank.ToString()
            && vm.ReinforcementUnitIdText == $"0x{preset.UnitId:X8}", "one preset click applies all three settings");
        vm.ReinforcementCountText = "10";
        var oldCount = vm.ReinforcementPresets.Count;
        vm.SaveReinforcementPresetCommand.Execute(null); Pump();
        Check(vm.ReinforcementPresets.Count == oldCount + 1 || vm.ReinforcementPresets.Any(p => p.Count == 10), "save current preset without naming workflow");
        }
        else
        {
            // 桌面浮窗的形态（发丝/迷你条/全条）与数字键盘（NumberPad）已随桌面浮窗一起删除：
            // 这个窗口现在只剩"面板渲染器"一种形态，资源入口变成面板「功能」页上的行。
            // 旧断言点的是形态里的 Tag("X:+") 按钮 —— 那个按钮不存在了，改成点亮面板上**真实那一行**
            // （ActivatePanelRow 就是游戏里整行点击的入口，与 Num5 走同一条执行路径）。
            var wasPanelMode = vm.PanelMode;
            vm.PanelMode = true;
            vm.SelectPanelPage("功能"); Pump();
            Check(vm.PanelRows.Count > 10, "panel feature page exposes rows: " + vm.PanelRows.Count);
            foreach (var (property, action, expected) in new[]
            {
                ("MoneyAmountText", "feat:Moeny", "10001"),
                ("PowerValueText", "feat:Power", "10001"),
                // 秘技点**故意不进**"按一次加一档"那条分支：RunPanelAction 只对钱/电力调 AdjustPanelMoney。
                // 旧浮窗上那三个 Tag("X:+") 步进按钮已随桌面浮窗删除，面板这一行只执行功能本身、不动数值。
                // 钉住"不动"才是可区分的断言 —— 哪天有人给秘技点加上步进，这里会当场炸。
                ("ScPointValueText", "feat:SC POINT", "1")
            })
            {
                typeof(MainViewModel).GetProperty(property)!.SetValue(vm, "1"); Pump();
                var row = vm.PanelRows.FirstOrDefault(item => item.Action == action)
                    ?? throw new Exception($"面板「功能」页没有 {action} 这一行；现有：{string.Join(",", vm.PanelRows.Select(item => item.Action))}");
                vm.ActivatePanelRow(row); Pump();
                Check((string)typeof(MainViewModel).GetProperty(property)!.GetValue(vm)! == expected,
                    $"panel row {action} leaves {property} = {expected}");
            }
            vm.SelectPanelPage("增援"); Pump();
            // 面板上没有"一键设成 N"的快捷数量按钮（1/5/10/20/50 那一排在主窗上，而且 Value 行点了不执行、
            // 只有按键能调）。游戏里的真路径是：Num8/2 把高亮走到「数量」行 → Num5 进调节态 → Num6 加一。
            // 旧断言在浮窗上点 SetCountCommand 按钮，那个按钮已不在浮窗上 ⇒ 换成真路径（与 Round37 夹具同一套走法）。
            vm.ReinforcementCountText = "8"; Pump();
            var countRow = vm.PanelRows.FirstOrDefault(item => item.Action == "count")
                ?? throw new Exception("面板「增援」页没有「数量」这一行");
            Check(countRow.Detail == "8", "panel quantity row tracks shared state: " + countRow.Detail);
            for (var guard = 0; guard < 40 && vm.Navigator.Selected?.Action != "count"; guard++)
                vm.ApplyPanelKey(PanelKeyCommand.Digit2);
            Check(vm.Navigator.Selected?.Action == "count", "panel highlight reached the quantity row: " + vm.Navigator.SelectionLabel);
            vm.ApplyPanelKey(PanelKeyCommand.Digit5); Pump();
            Check(vm.PanelResultText.Contains("调节"), "panel quantity row entered adjust mode: " + vm.PanelResultText);
            vm.ApplyPanelKey(PanelKeyCommand.Digit6); Pump();
            Check(vm.ReinforcementCountText == "9", "panel key stepped quantity 8 -> 9: " + vm.ReinforcementCountText);
            vm.PanelMode = wasPanelMode; Pump();
        }
    }
    public static void Actions(MainViewModel vm)
    {
        var memory = new CoordinateMemory();
        var controller = new FeatureController(memory, memory.Resolver);
        typeof(MainViewModel).GetProperty(nameof(vm.FeatureController))!.SetValue(vm, controller);
        typeof(MainViewModel).GetProperty(nameof(vm.ArePatchesInstalled))!.SetValue(vm, true);
        vm.AllowUnverifiedWorldPositionFeatures = true;
        var action = vm.Groups.SelectMany(g => g.Features).Single(f => f.Feature.RawName == "Get Me Base");
        var reinforcement = vm.Groups.SelectMany(g => g.Features).Single(f => f.Feature.RawName == "We Need Back");
        var progress = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.CoordinateCountdownText)) progress.Add(vm.CoordinateCountdownText); };
        var watch = Stopwatch.StartNew();
        action.ExecuteFromHotkey(); Pump();
        Check(watch.Elapsed < TimeSpan.FromSeconds(1) && memory.Dispatches.SequenceEqual(new byte[] { 14, 11 }), "valid hotkey immediately runs unchanged conversion/generation protocol");
        Check(!progress.Any(t => t.StartsWith("把鼠标")) && !vm.IsCoordinateActionPending, "valid hotkey never creates countdown");
        memory.SetCoordinates(0); memory.Dispatches.Clear(); progress.Clear();
        action.ExecuteFromHotkey(); Pump();
        Check(memory.Dispatches.SequenceEqual(new byte[] { 14, 11 }) && !vm.IsCoordinateActionPending, "first hotkey with zero cached copy still performs fresh validated conversion");
        memory.SetCoordinates(0); memory.Dispatches.Clear(); progress.Clear(); memory.ConversionCoordinate = 0;
        action.ExecuteFromHotkey(); Pump();
        Check(action.Status == "失败" && memory.Dispatches.SequenceEqual(new byte[] { 14 }) && !vm.IsCoordinateActionPending && !progress.Any(t => t.StartsWith("把鼠标")), "invalid fresh hotkey coordinates reject generation without countdown or parameter writes");
        memory.ConversionCoordinate = 200; memory.Dispatches.Clear();
        action.Command.Execute(null); Pump();
        Check(vm.IsCoordinateActionPending && vm.CoordinateCountdownText.EndsWith("3") && memory.Dispatches.Count == 0, "invalid panel coordinates enter cancellable countdown without writes");
        action.Command.Execute(null); reinforcement.Command.Execute(null);
        Check(progress.Count(t => t.StartsWith("把鼠标")) == 1, "duplicate and competing clicks cannot start extra countdown");
        vm.CancelCoordinateActionCommand.Execute(null); WaitIdle(vm);
        Check(memory.Dispatches.Count == 0, "cancel countdown does not dispatch");
        action.Command.Execute(null); vm.AllowUnverifiedWorldPositionFeatures = false; vm.AllowUnverifiedWorldPositionFeatures = true; WaitIdle(vm);
        Check(memory.Dispatches.Count == 0, "revoked then reenabled permission cannot revive pending request");
        progress.Clear(); watch.Restart(); action.Command.Execute(null); WaitIdle(vm);
        Console.WriteLine($"Countdown evidence: elapsed={watch.Elapsed.TotalSeconds:F3}, status={action.Status}, message={vm.StatusMessage}, progress={string.Join(" | ", progress)}");
        Check(watch.Elapsed >= TimeSpan.FromSeconds(3) && progress.Where(t => t.StartsWith("把鼠标")).Select(t => t[^1]).SequenceEqual(new[] { '3', '2', '1' }), "panel countdown reports 3/2/1 before fresh conversion");
        Check(memory.Dispatches.SequenceEqual(new byte[] { 14, 11 }), "countdown completion still uses complete two-stage protocol");
        memory.Dispatches.Clear(); progress.Clear(); watch.Restart(); action.Command.Execute(null); Pump();
        Check(watch.Elapsed < TimeSpan.FromSeconds(1) && memory.Dispatches.SequenceEqual(new byte[] { 14, 11 }) && !progress.Any(t => t.StartsWith("把鼠标")), "valid panel coordinates also dispatch immediately");
        typeof(MainViewModel).GetProperty(nameof(vm.FeatureController))!.SetValue(vm, null);
        typeof(MainViewModel).GetProperty(nameof(vm.ArePatchesInstalled))!.SetValue(vm, false);
    }
    static void WaitIdle(MainViewModel vm)
    {
        var watch = Stopwatch.StartNew();
        while (vm.IsCoordinateActionPending && watch.Elapsed < TimeSpan.FromSeconds(5)) { Pump(); Thread.Sleep(10); }
        Check(!vm.IsCoordinateActionPending, "action lifecycle returned idle");
    }
    private sealed class CoordinateMemory : IProcessMemory, IProbeMemory
    {
        readonly Dictionary<nint, byte> bytes = new();
        public readonly List<byte> Dispatches = [];
        public float ConversionCoordinate = 200;
        public AddressResolver Resolver { get; } = new(0x400000, new Dictionary<string, nint> { ["iEnable"] = 0x10000, ["ID"] = 0x11000, ["MustCode"] = 0x120000 });
        public CoordinateMemory()
        {
            Word(Resolver.Resolve("ra3_1.12.game+8DAEFC"), 0xF0000);
            Word(Resolver.Resolve("ID"), 0xD0000);
            Word(Resolver.Resolve("ra3_1.12.game+8E6C58"), 0xE0000);
            SetCoordinates(200);
        }
        void Word(nint a, uint v) { var b = BitConverter.GetBytes(v); for (int i=0;i<4;i++) bytes[a+i]=b[i]; }
        public void SetCoordinates(float x) { foreach (var offset in new[] {0,4,8}) Word(Resolver.Resolve(RemoteStateLayout.WorldCoordinates)+offset, BitConverter.SingleToUInt32Bits(x)); }
        public Win32MemoryRegion? Query(nint a) => new(0x10000, 0xFFFE0000, 4, 0x1000, 4, 0x20000);
        public byte[] ReadBytes(nint a, int count) => Enumerable.Range(0,count).Select(i => bytes.GetValueOrDefault(a+i)).ToArray();
        public void WriteBytes(nint a, ReadOnlySpan<byte> data)
        {
            for (int i=0;i<data.Length;i++) bytes[a+i]=data[i];
            if (a != Resolver.Resolve(RemoteStateLayout.ActionDispatch)) return;
            Dispatches.Add(data[0]);
            if (data[0] == 14) { SetCoordinates(ConversionCoordinate); Increment(RemoteStateLayout.ConversionEpoch); }
            Increment(RemoteStateLayout.DispatchEpoch);
            Word(a,0);
        }
        void Increment(string name) { var a=Resolver.Resolve(name); Word(a,BitConverter.ToUInt32(ReadBytes(a,4))+1); }
    }
}
#endif
