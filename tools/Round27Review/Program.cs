using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ra3Trainer.App;
using Ra3Trainer.App.Diagnostics;
using Ra3Trainer.App.ViewModels;
using Ra3Trainer.Core.Runtime;

internal static class Program
{
    [STAThread] static int Main(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("usage: Round27Review <outputDir>"); return 2; }
        var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        CleanupChecks(Path.Combine(output, "cleanup"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Ra3Trainer.App;component/Themes/GlobalStyles.xaml", UriKind.Relative) });
        var memory = new SmokeMemory(); var overlay = new FixtureOverlay();
        using var vm = new MainViewModel(TrainerRuntimeAssets.LoadManifest(), new TrainerAppSettingsStore(Path.Combine(output, "settings.json")),
            new SmokeLauncher(), new SmokeLocator(), _ => memory, _ => memory, new SmokeSuspender(), _ => [], overlay: overlay);
        typeof(App).GetField("_sharedViewModel", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, vm);
        var foreground = GetForegroundWindow(); var focus = GetFocus(); var activations = 0;
        foreach (var module in new[] { "nvspcap.dll", "EnhancerCorona.dll", "unidentified" })
        {
            overlay.Status = OverlayFeedback.Parse("osd-session 000000000000007B\nosd-entry slot=Present address=123 reason="
                + (module == "unidentified" ? "unidentified" : "foreign-module") + " module=" + module
                + "\nosd-entry slot=Reset address=456 reason=verified-system module=d3d9.dll\nosd-install-refused foreign-entry\nosd-install-refused retain-disabled\n", 123)!;
            var window = new MainWindow();
            window.ShowActivated = false; window.Left = -30000; window.Top = -30000;
            window.Activated += (_, _) => activations++;
            window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle).AddHook(KeepOffscreen);
            window.Show(); window.Dispatcher.Invoke(() => {}, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Save((FrameworkElement)window.Content, module + "-main");
            // 「维护」页已改名「诊断」（主窗四页：启动/增援/功能/诊断）。旧写法设的是不存在的页名，
            // ConsoleNavigation.Selected 会把无效值静默忽略 ⇒ 窗口留在「启动」页，
            // 诊断页里的 OverlayHelpPanel 从没被实例化、ActualWidth=0，下面 Save 直接炸在 pixelWidth 上。
            window.Navigation.Selected = "诊断";
            window.Dispatcher.Invoke(() => {}, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var status = (System.Windows.Controls.TextBlock)window.FindName("OverlayStatusText");
            var guidance = (System.Windows.Controls.TextBlock)window.FindName("OverlayGuidanceText");
            if (!status.Text.Contains("本局未安装") || !status.Text.Contains(module == "unidentified" ? "未识别" : module)
                || !guidance.Text.Contains("Highlights") || !guidance.Text.Contains("重启游戏")) throw new Exception("Real binding failed");
            // Render the actual expanded product panel, including content below the viewport.
            Save((FrameworkElement)window.FindName("OverlayHelpPanel"), module + "-guidance");
            window.Close();
            if (module == "nvspcap.dll")
            {
                // 桌面浮窗形态已删除（窗口只剩"面板渲染器"一种形态）⇒ 用面板模式表达"游戏内面板"。
                vm.PanelMode = true;
                var floating = new FloatingWindow(vm) { ShowActivated = false, Left = -30000, Top = -30000 };
                floating.Activated += (_, _) => activations++;
                floating.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(floating).Handle).AddHook(KeepOffscreen);
                floating.Show(); floating.Dispatcher.Invoke(() => {}, DispatcherPriority.ApplicationIdle); floating.UpdateLayout();
                // 旧代码：点浮窗上的 "ToggleButton" 展开 + 切「维护」页。
                // 面板没有展开/收起（只有一种形态），也没有「维护」这个页签 ——
                // 面板页签是游戏内动词（功能/增援/选单位）；OSD 状态印在「功能」页上 ⇒ 切到「功能」。
                vm.SelectPanelPage("功能");
                floating.Dispatcher.Invoke(() => {}, DispatcherPriority.ApplicationIdle); floating.UpdateLayout();
                Save((FrameworkElement)floating.Content, "nvspcap.dll-floating");
                floating.Close();
            }
        }
        if (foreground != GetForegroundWindow() || focus != GetFocus() || activations != 0) throw new Exception("Focus changed");
        Console.WriteLine("PASS 3 real WPF refusal/module/guidance fixtures; foreground/focus unchanged; Activated=0");
        return 0;
        void Save(FrameworkElement content, string name)
        {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
            {
                dc.DrawRectangle((Brush)app.FindResource("PlateBrush"), null, new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
                dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            }
            bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
        }
    }
    static void CleanupChecks(string root)
    {
        root = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "deployment.json");
        var target = Path.Combine(root, "d3d9.dll");
        if (File.Exists(journal) || File.Exists(target)) throw new Exception("Unexpected pre-existing cleanup fixture");
        var method = typeof(ProductOverlay).GetMethod("RunCleanupWorkerCore", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Exception("test wiring broken: ProductOverlay.RunCleanupWorkerCore (static, non-public) was not found");
        void Run(int owner)
        {
            var task = (Task)method.Invoke(null, new object[] { owner, journal })!;
            if (!task.Wait(TimeSpan.FromSeconds(3))) throw new Exception("Cleanup did not exit promptly");
            task.GetAwaiter().GetResult();
        }
        Run(Environment.ProcessId); // No journal must return before waiting for this live owner.
        using var resource = typeof(ProductOverlay).Assembly.GetManifestResourceStream("Ra3Overlay.dll")!;
        using var data = new MemoryStream(); resource.CopyTo(data); var payload = data.ToArray();
        File.WriteAllBytes(target, payload);
        File.WriteAllText(journal, System.Text.Json.JsonSerializer.Serialize(new OverlayDeploymentRecord(target,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)))));
        Run(0);
        if (File.Exists(journal) || File.Exists(target)) throw new Exception("Owned cleanup incomplete");
        // Unknown content at a journal target is preserved, never deleted to make the test pass.
        File.WriteAllText(target, "round27 test-owned foreign-content fixture");
        File.WriteAllText(journal, System.Text.Json.JsonSerializer.Serialize(new OverlayDeploymentRecord(target,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)))));
        Run(0);
        if (!File.Exists(journal) || File.ReadAllText(target) != "round27 test-owned foreign-content fixture") throw new Exception("Foreign content changed");
        File.Move(target, Path.Combine(root, "preserved-foreign-fixture.txt"));
        File.Move(journal, Path.Combine(root, "preserved-journal.json"));
        Console.WriteLine("PASS cleanup: no-work ignores live parent; owned cleanup completes immediately; foreign file preserved and error exits");
    }
    sealed class FixtureOverlay : IProductOverlay
    {
        public string Status { get; set; } = "";
        public void BeforeLaunch(string p, IReadOnlyList<LauncherCandidate> c) { }
        public void Attach(int p, string v) { }
        public void Publish(bool e, bool c, int n, string a) { }
        public void Stop() { } public void Disconnect() { } public void Cleanup() { } public void StartCleanupWorker() { }
    }
    [StructLayout(LayoutKind.Sequential)] struct WindowPos { public IntPtr Hwnd, After; public int X, Y, Width, Height; public uint Flags; }
    static IntPtr KeepOffscreen(IntPtr h, int m, IntPtr w, IntPtr l, ref bool handled)
    {
        if (m == 0x46) { var p = Marshal.PtrToStructure<WindowPos>(l); p.X = -30000; p.Y = -30000; p.Flags &= ~2u; p.Flags |= 0x10; Marshal.StructureToPtr(p, l, false); }
        return IntPtr.Zero;
    }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetFocus();
}
