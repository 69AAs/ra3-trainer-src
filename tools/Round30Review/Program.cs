extern alias Tests;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ra3Trainer.App;
using Ra3Trainer.App.ViewModels;
using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Runtime;
using QueueStopTests = Tests::Ra3Trainer.Tests.QueueStopTests;
using System.Text.Json;

internal static class Program
{
    [STAThread] static int Main(string[] args)
    {
        if (args[0] == "compare") { Compare(args[1]); return 0; }
        var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Ra3Trainer.App;component/Themes/GlobalStyles.xaml", UriKind.Relative) });
        var memory = new SmokeMemory();
        using var vm = new MainViewModel(TrainerRuntimeAssets.LoadManifest(), new TrainerAppSettingsStore(Path.Combine(output, "settings.json")),
            new SmokeLauncher(), new SmokeLocator(), _ => memory, _ => memory, new SmokeSuspender(), _ => []);
        // 桌面浮窗形态已删除（窗口只剩"面板渲染器"一种形态）⇒ 用面板模式表达"游戏内面板"。
        vm.PanelMode = true;
        vm.ReinforcementPresets.Clear();
        vm.ReinforcementPresets.Add(new ReinforcementPreset("常用增援", ReinforcementSettings.DefaultUnitId, 20, 3));
        typeof(App).GetField("_sharedViewModel", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, vm);
        var foreground = GetForegroundWindow();
        var focus = GetFocus();
        var activations = 0;
        if (args.Contains("--queue"))
        {
            foreach (var mode in new[] { "late-clear", "partial" })
            foreach (var version in new[] { "before", "after" })
            {
                var memoryFixture = new QueueStopTests.QueueMemory(mode);
                var controller = new FeatureController(memoryFixture, memoryFixture.Resolver);
                var feature = TrainerRuntimeAssets.LoadManifest().Features.Single(f => f.RawName == "We Need Back");
                var run = Task.Run(async () => version == "before"
                    ? await Round30Baseline.BaselineQueueRunner.ExecuteAsync(Entries(), controller, feature,
                        TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(1))
                    : await ReinforcementQueueRunner.ExecuteAsync(Entries(), controller, feature,
                        TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(1),
                        onResult: result => app.Dispatcher.Invoke(() => typeof(MainViewModel)
                            .GetMethod("ReportReinforcementQueueResult", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [result]))));
                var frame = new DispatcherFrame();
                _ = run.ContinueWith(_ => app.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
                Dispatcher.PushFrame(frame);
                var results = run.GetAwaiter().GetResult();
                if (version == "before") vm.StatusMessage = string.Join("；", results.Select(r => r.Message));
                var evidence = new { source = "离线夹具 / 真实WPF离屏；非真实对局", mode, version,
                    generationDispatches = memoryFixture.GenerationDispatches,
                    conversionDispatches = memoryFixture.ConversionDispatches, results, panel = vm.StatusMessage, logDirectory = vm.LogDirectory };
                File.WriteAllText(Path.Combine(output, $"{mode}-{version}.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
                if (memoryFixture.GenerationDispatches != (version == "before" ? 2 : 1)) throw new Exception("Queue dispatch comparison failed");
                if (version == "after" && !vm.StatusMessage.Contains("队列已停止")) throw new Exception("Chinese stop not visible");
                Render(new MainWindow(), $"{mode}-{version}");
                Console.WriteLine($"PASS {mode}/{version}: generationDispatches={memoryFixture.GenerationDispatches}; {vm.StatusMessage}");
                IEnumerable<ReinforcementQueueEntry> Entries()
                {
                    yield return QueueStopTests.Entries[0];
                    memoryFixture.CompleteLate();
                    yield return QueueStopTests.Entries[1];
                }
            }
            File.WriteAllText(Path.Combine(output, "log-directory.txt"), vm.LogDirectory);
            // Exercise the retained App bridge, including its current-session/permission guards.
            var bridgeMemory = new QueueStopTests.QueueMemory("partial");
            var bridgeController = new FeatureController(bridgeMemory, bridgeMemory.Resolver);
            typeof(MainViewModel).GetProperty("FeatureController")!.SetValue(vm, bridgeController);
            typeof(MainViewModel).GetField("_arePatchesInstalled", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, true);
            vm.AllowUnverifiedWorldPositionFeatures = true;
            var bridge = typeof(MainViewModel).GetMethod("ExecuteReinforcementQueueAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var bridgeTask = (Task<IReadOnlyList<ReinforcementQueueResult>>)bridge.Invoke(vm, [QueueStopTests.Entries, CancellationToken.None])!;
            if (!bridgeTask.IsCompleted)
            {
                var bridgeFrame = new DispatcherFrame();
                _ = bridgeTask.ContinueWith(_ => app.Dispatcher.BeginInvoke(new Action(() => bridgeFrame.Continue = false)));
                Dispatcher.PushFrame(bridgeFrame);
            }
            if (bridgeTask.GetAwaiter().GetResult().Count != 2 || bridgeMemory.GenerationDispatches != 1 || !vm.StatusMessage.Contains("队列已停止：上一批已派发后失败"))
                throw new Exception("App queue bridge failed");
            vm.AllowUnverifiedWorldPositionFeatures = false;
            var rejected = (Task<IReadOnlyList<ReinforcementQueueResult>>)bridge.Invoke(vm, [QueueStopTests.Entries, CancellationToken.None])!;
            try { rejected.GetAwaiter().GetResult(); throw new Exception("Revoked permission should reject"); }
            catch (InvalidOperationException) { }
            if (bridgeMemory.GenerationDispatches != 1) throw new Exception("Revoked bridge dispatched");
            typeof(MainViewModel).GetProperty("FeatureController")!.SetValue(vm, null);
            typeof(MainViewModel).GetField("_arePatchesInstalled", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, false);
            Console.WriteLine("PASS retained App queue bridge reports Chinese panel/log; revoked permission dispatches zero");
        }
        Render(new MainWindow(), "main");
        Render(new FloatingWindow(vm), "float");
        if (args.Contains("--states"))
        {
            Render(new MainWindow(), "main-50", "50");
            Render(new FloatingWindow(vm), "float-50", "50");
            Render(new MainWindow(), "main-100", "100");
            Render(new FloatingWindow(vm), "float-100", "100");
        }
#if !BASELINE
        if (args.Contains("--check")) ReviewChecks.Actions(vm);
#endif
        if (foreground != GetForegroundWindow() || focus != GetFocus() || activations != 0)
            throw new Exception("Foreground/focus changed or window activated");
        Console.WriteLine("PASS offscreen real WPF rendering; foreground/focus unchanged; Activated=0");
        return 0;

        void Render(Window window, string name, string? requestedCount = null)
        {
            window.ShowActivated = false; window.Left = -30000; window.Top = -30000;
            window.Activated += (_, _) => activations++;
            window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle).AddHook(KeepOffscreen);
            window.Loaded += (_, _) => { window.Left = -30000; window.Top = -30000; };
            window.Show(); window.Dispatcher.Invoke(() => {}, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            // 旧代码在这里点浮窗上的 "ToggleButton" 把浮窗从"发丝条"展开成全条再截图。
            // 桌面浮窗形态与那个开关都已删除（面板只有一种形态、永远展开，也没有 ToggleButton 这个控件）
            // ⇒ 整段删除；"面板正在游戏里跑"的样子由 vm.PanelMode = true 表达（见 Main）。
            var content = (FrameworkElement)window.Content;
            if (requestedCount is not null)
            {
                var editor = window is FloatingWindow f ? f.ReinforcementEditor : ((MainWindow)window).ReinforcementEditor;
                editor.SetCountCommand.Execute("50");
                editor.SetCountCommand.Execute(requestedCount);
                if (vm.ReinforcementCountText != "50") throw new Exception("Quantity must stay 50 after setting 50 / rejecting 100");
                Console.WriteLine($"PASS {name}: requested={requestedCount}; effective={vm.ReinforcementCountText}; {editor.Feedback}");
                window.Dispatcher.Invoke(() => {}, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            }
#if !BASELINE
            if (args.Contains("--check") && !name.EndsWith("-before") && !name.EndsWith("-after")) ReviewChecks.Window(window, vm);
#endif
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(output, name + ".png"))) encoder.Save(file);
            window.Close();
        }
    }
    [StructLayout(LayoutKind.Sequential)] struct WindowPos { public IntPtr Hwnd, After; public int X, Y, Width, Height; public uint Flags; }
    static IntPtr KeepOffscreen(IntPtr h, int m, IntPtr w, IntPtr l, ref bool handled)
    {
        if (m == 0x46) { var p = Marshal.PtrToStructure<WindowPos>(l); p.X = -30000; p.Y = -30000; p.Flags &= ~2u; p.Flags |= 0x10; Marshal.StructureToPtr(p, l, false); }
        return IntPtr.Zero;
    }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetFocus();
    static void Compare(string root)
    {
        foreach (var name in new[] { "late-clear", "partial" })
        {
            var before = new BitmapImage(new Uri(Path.GetFullPath(Path.Combine(root, name + "-before.png"))));
            var after = new BitmapImage(new Uri(Path.GetFullPath(Path.Combine(root, name + "-after.png"))));
            var width = before.PixelWidth + after.PixelWidth + 30;
            var height = Math.Max(before.PixelHeight, after.PixelHeight) + 45;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.DimGray, null, new Rect(0, 0, width, height));
                dc.DrawImage(before, new Rect(10, 35, before.PixelWidth, before.PixelHeight));
                dc.DrawImage(after, new Rect(before.PixelWidth + 20, 35, after.PixelWidth, after.PixelHeight));
                dc.DrawText(new FormattedText("改前 · 离线夹具 · 两项都派发", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), 18, Brushes.White, 1), new Point(10, 5));
                dc.DrawText(new FormattedText("改后 · 真实WPF离屏 · 第二项零派发（非真实对局）", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), 18, Brushes.White, 1), new Point(before.PixelWidth + 20, 5));
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(root, name + "-compare.png")); encoder.Save(file);
        }
    }
}
