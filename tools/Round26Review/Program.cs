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

internal static class Program
{
    [STAThread] static int Main(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("usage: Round26Review <outputDir> [--check] | compare <dir>"); return 2; }
        if (args[0] == "compare") { if (args.Length < 2) { Console.Error.WriteLine("usage: compare <dir>"); return 2; } Compare(args[1]); return 0; }
        var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Ra3Trainer.App;component/Themes/GlobalStyles.xaml", UriKind.Relative) });
        var memory = new SmokeMemory();
        using var vm = new MainViewModel(TrainerRuntimeAssets.LoadManifest(), new TrainerAppSettingsStore(Path.Combine(output, "settings.json")),
            new SmokeLauncher(), new SmokeLocator(), _ => memory, _ => memory, new SmokeSuspender(), _ => []);
        // 桌面浮窗的三种形态（发丝状态线/迷你条/全条）已删除：该窗口现在只剩"面板渲染器"一种形态，
        // 固定 360×440，永远展开。旧代码在这里设 SelectedFloatWindowForm = FullBar 来强制展开，
        // 现在没有形态可切 ⇒ 改成打开面板模式（产品里"游戏内面板"就是这个状态）。
        vm.PanelMode = true;
        vm.ReinforcementPresets.Clear();
        vm.ReinforcementPresets.Add(new ReinforcementPreset("常用增援", ReinforcementSettings.DefaultUnitId, 20, 3));
        typeof(App).GetField("_sharedViewModel", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, vm);
        var foreground = GetForegroundWindow();
        var focus = GetFocus();
        var activations = 0;
        Render(new MainWindow(), "main");
        Render(new FloatingWindow(vm), "float");
#if !BASELINE
        if (args.Contains("--check")) ReviewChecks.Actions(vm);
#endif
        if (foreground != GetForegroundWindow() || focus != GetFocus() || activations != 0)
            throw new Exception("Foreground/focus changed or window activated");
        Console.WriteLine("PASS offscreen real WPF rendering; foreground/focus unchanged; Activated=0");
        return 0;

        void Render(Window window, string name)
        {
            window.ShowActivated = false; window.Left = -30000; window.Top = -30000;
            window.Activated += (_, _) => activations++;
            window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle).AddHook(KeepOffscreen);
            window.Loaded += (_, _) => { window.Left = -30000; window.Top = -30000; };
            window.Show(); window.Dispatcher.Invoke(() => {}, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            // 旧代码在这里点浮窗上的 "ToggleButton"（展开/收起）把浮窗撑开再截图。
            // 面板没有展开/收起这个开关（它只有一种形态），也没有 ToggleButton 这个控件了 ⇒ 整段删除。
            // 需要"面板正在游戏里跑"的样子时用 vm.PanelMode = true（上面已设）。
            var content = (FrameworkElement)window.Content;
#if !BASELINE
            if (args.Contains("--check")) ReviewChecks.Window(window, vm);
#endif
            var bitmap = new RenderTargetBitmap(Math.Max(1, (int)content.ActualWidth), Math.Max(1, (int)content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
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
        foreach (var name in new[] { "main", "float" })
        {
            var before = new BitmapImage(new Uri(Path.GetFullPath(Path.Combine(root, "before", name + ".png"))));
            var after = new BitmapImage(new Uri(Path.GetFullPath(Path.Combine(root, "after", name + ".png"))));
            var width = before.PixelWidth + after.PixelWidth + 30;
            var height = Math.Max(before.PixelHeight, after.PixelHeight) + 45;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.DimGray, null, new Rect(0, 0, width, height));
                dc.DrawImage(before, new Rect(10, 35, before.PixelWidth, before.PixelHeight));
                dc.DrawImage(after, new Rect(before.PixelWidth + 20, 35, after.PixelWidth, after.PixelHeight));
                dc.DrawText(new FormattedText("改动前", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), 18, Brushes.White, 1), new Point(10, 5));
                dc.DrawText(new FormattedText("改动后 · 上限待采样验证", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), 18, Brushes.White, 1), new Point(before.PixelWidth + 20, 5));
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(root, name + "-compare.png")); encoder.Save(file);
        }
    }
}
