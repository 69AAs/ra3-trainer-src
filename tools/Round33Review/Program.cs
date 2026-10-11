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
        if(args.Length==0) { Console.Error.WriteLine("usage: Round33Review <outputDir>"); return 2; }
        var output=Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source=new Uri("/Ra3Trainer.App;component/Themes/GlobalStyles.xaml",UriKind.Relative) });
        var memory=new SmokeMemory(); var overlay=new FixtureOverlay();
        using var vm=new MainViewModel(TrainerRuntimeAssets.LoadManifest(),new TrainerAppSettingsStore(Path.Combine(output,"settings.json")),
            new SmokeLauncher(),new SmokeLocator(),_=>memory,_=>memory,new SmokeSuspender(),_=>[],overlay:overlay);
        typeof(App).GetField("_sharedViewModel",BindingFlags.NonPublic|BindingFlags.Static)!.SetValue(null,vm);
        var foreground=GetForegroundWindow(); var focus=GetFocus(); int activated=0;
        foreach(var version in new[]{"before","after"})
        {
            var log="osd-session 000000000000007B\nosd-entry slot=Present reason=verified-system module=d3d9.dll\n"
                +"osd-entry slot=Reset reason="+(version=="before"?"relocation-or-image-mismatch":"verified-system")+" transfer=none module=d3d9.dll\n"
                +(version=="before"?"osd-install-refused foreign-entry\n":"osd-installed\nosd-frame-presented\n");
            overlay.Status=version=="before"?Round33Baseline.OverlayFeedback.Parse(log,123)!:OverlayFeedback.Parse(log,123)!;
            File.WriteAllText(Path.Combine(output,version+"-fixture.log"),log);
            var window=new MainWindow { ShowActivated=false,Left=-30000,Top=-30000 };
            window.Activated+=(_,_)=>activated++;
            window.SourceInitialized+=(_,_)=>HwndSource.FromHwnd(new WindowInteropHelper(window).Handle).AddHook(KeepOffscreen);
            window.Show(); window.Navigation.Selected="维护";
            window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var status=(System.Windows.Controls.TextBlock)(window.FindName("OverlayStatusText") ?? throw new Exception("OverlayStatusText not found in the rendered window"));
            if(status.Text!=overlay.Status) throw new Exception("WPF binding differs from parsed verdict");
            var content=(FrameworkElement)(window.FindName("OverlayHelpPanel") ?? throw new Exception("OverlayHelpPanel not found in the rendered window"));
            int width=Math.Max(1,(int)Math.Ceiling(content.ActualWidth)),height=Math.Max(1,(int)Math.Ceiling(content.ActualHeight))+42;
            var visual=new DrawingVisual(); using(var dc=visual.RenderOpen()) {
                dc.DrawRectangle((Brush)app.FindResource("PlateBrush"),null,new Rect(0,0,width,height));
                dc.DrawText(new FormattedText((version=="before"?"改前":"改后")+" · 真实 WPF 离屏 / 合成日志夹具 · 非真机验收",
                    System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Microsoft YaHei"),14,Brushes.White,1),new Point(4,6));
                dc.DrawRectangle(new VisualBrush(content),null,new Rect(0,42,width,content.ActualHeight));
            }
            var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32); bitmap.Render(visual);
            var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using(var file=File.Create(Path.Combine(output,version+".png"))) png.Save(file);
            File.WriteAllText(Path.Combine(output,version+"-panel.txt"),status.Text);
            window.Close(); Console.WriteLine("PASS "+version+" real WPF binding and offscreen render");
        }
        if(foreground!=GetForegroundWindow() || focus!=GetFocus() || activated!=0) throw new Exception("Focus changed");
        Console.WriteLine("PASS foreground/focus unchanged; Activated=0; no injected input; skipped=0"); return 0;
    }
    sealed class FixtureOverlay:IProductOverlay {
        public string Status { get;set; }="";
        public void BeforeLaunch(string p,IReadOnlyList<LauncherCandidate> c) {} public void Attach(int p,string v) {}
        public void Publish(bool e,bool c,int n,string a) {} public void Stop() {} public void Disconnect() {} public void Cleanup() {} public void StartCleanupWorker() {}
    }
    [StructLayout(LayoutKind.Sequential)] struct WindowPos { public IntPtr Hwnd,After; public int X,Y,Width,Height; public uint Flags; }
    static IntPtr KeepOffscreen(IntPtr h,int m,IntPtr w,IntPtr l,ref bool handled) {
        if(m==0x46) { var p=Marshal.PtrToStructure<WindowPos>(l); p.X=-30000;p.Y=-30000;p.Flags&=~2u;p.Flags|=0x10;Marshal.StructureToPtr(p,l,false); } return IntPtr.Zero;
    }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetFocus();
}
