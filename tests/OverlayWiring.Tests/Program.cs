using System.IO;
using System.Reflection;
using Ra3Trainer.App.Diagnostics;
using Ra3Trainer.App.ViewModels;
using Ra3Trainer.Core.Patching;
using Ra3Trainer.Core.Runtime;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var manifest = TrainerRuntimeAssets.LoadManifest();
        var suspender = new SmokeSuspender();
        var memory = new SmokeSessionResources(suspender.Events);
        using var session = new TrainerSession(manifest, memory,
            new TrainerTarget(manifest.TargetProcess, (nint)0x400000, true, true, 1234), memory,
            bootstrapLines: TrainerRuntimeAssets.ReadBootstrapLines(), processSuspender: suspender, targetProcessState: memory);
        if (!session.Attach().Success) return 1;
        var plans = PatchHookPlanner.CreatePlans(manifest.PatchManifest);
        foreach (var plan in plans) memory.WriteBytes(session.Resolver!.Resolve(plan.Address), plan.OriginalBytes);
        session.InstallPatches();
        if (session.InstalledHookCount != 22) return 2;
        var overlay = new BrokenOverlay();
        string settings = Path.Combine(Path.GetTempPath(), "ra3-overlay-wiring-" + Guid.NewGuid() + ".json");
        try
        {
            using var vm = new MainViewModel(manifest, new TrainerAppSettingsStore(settings), new SmokeLauncher(),
                new SmokeLocator(), _ => memory, _ => memory, suspender, _ => [], overlay: overlay);
            typeof(MainViewModel).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, session);
            vm.RestorePatches();
            if (session.InstalledHookCount != 0 || overlay.Stops != 1 || memory.Frees != 1) return 3;
            foreach (var plan in plans)
            {
                // 用同一套 resolver 解析，别手算 0x400000+偏移：地址一变成符号形式这里就会假红。
                var address = session.Resolver!.Resolve(plan.Address);
                if (!memory.ReadBytes(address, plan.OriginalBytes.Length).SequenceEqual(plan.OriginalBytes)) return 4;
            }
            vm.OverlayEnabled = true; // Publish failure also remains isolated.
            vm.Dispose(); // Disconnect/cleanup failure cannot escape either.
            Console.WriteLine("PASS OSD stop/publish/disconnect failures isolated; 22 hooks byte-equal; remote block freed");
            return 0;
        }
        finally { if (File.Exists(settings)) File.Delete(settings); }
    }

    private sealed class BrokenOverlay : IProductOverlay
    {
        public string Status => "故障注入";
        public int Stops { get; private set; }
        public void BeforeLaunch(string path, IReadOnlyList<LauncherCandidate> candidates) => throw new IOException("deploy failure");
        public void Attach(int pid, string version) => throw new IOException("attach failure");
        public void Publish(bool enabled, bool connected, int count, string action) => throw new IOException("status failure");
        public void Stop() { Stops++; throw new IOException("stop failure"); }
        public void Disconnect() => throw new IOException("disconnect failure");
        public void Cleanup() => throw new IOException("cleanup failure");
        public void StartCleanupWorker() => throw new IOException("worker failure");
    }
}
