using System.IO;
using System.Text;
using System.Windows;
using Ra3Trainer.App.ViewModels;
using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Runtime;

// 只做一件事：把游戏内面板各页的**行集**按渲染用的字段打印出来，供改前/改后逐行对比。
// 用产品真实的 MainViewModel.BuildPanelRows（不是另造一份行表），所以打印的就是面板会画的东西。
internal static class Program
{
    private static string V(string s) => string.IsNullOrEmpty(s) ? "<empty>" : "\"" + s + "\"";

    [STAThread]
    private static int Main(string[] args)
    {
        string output = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/panelrowdump");
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Ra3Trainer.App;component/Themes/GlobalStyles.xaml", UriKind.Relative) });

        var memory = new SmokeMemory();
        using var vm = new MainViewModel(TrainerRuntimeAssets.LoadManifest(),
            new TrainerAppSettingsStore(Path.Combine(output, "settings.json")),
            new SmokeLauncher(), new SmokeLocator(), _ => memory, _ => memory, new SmokeSuspender(), _ => []);

        var sb = new StringBuilder();

        void DumpRows(string tag)
        {
            sb.AppendLine($"      # layer={vm.PanelLayer} page={V(vm.PanelPage)} overlay={V(vm.OverlayPanelSection)} navigatorPage={V(vm.Navigator.Page)}");
            int i = 0;
            foreach (var row in vm.PanelRows)
                sb.AppendLine($"  [{i++,2}] kind={row.Kind,-8} action={V(row.Action),-14} label={V(row.Label),-24} detail={V(row.Detail),-26} keyLabel={V(row.KeyLabel),-8} keyDisplay={V(row.KeyDisplay),-8} adjustable={row.IsAdjustable}");
        }

        // ① 逐页（直接设页）：看每页的编排
        foreach (var section in new[] { "增援", "功能", "选单位" })
        {
            vm.OverlayPanelSection = section;
            sb.AppendLine($"===== PAGE(直接设) {section} =====");
            DumpRows(section);
        }

        // ② 用户真人的路径：面板从默认页开始，按换页键（口径：7/9 换页）一路走到「选单位」，看**第一屏**到底是什么
        vm.PanelMode = true;                 // ApplyPanelKey 只在面板模式下生效（否则整段是空跑）
        vm.OverlayPanelSection = ConsoleNavigation.DefaultPanelSection;
        sb.AppendLine($"===== 真人路径：Num9 换页（口径 §3：7/9 换页） =====");
        sb.AppendLine($"--- 起点 ---"); DumpRows("start");
        for (int step = 0; step < 2; step++)
        {
            vm.ApplyPanelKey(PanelKeyCommand.Digit9);
            sb.AppendLine($"--- Num9 第 {step + 1} 次 → 页={V(vm.PanelPage)} ---");
            DumpRows("num9");
        }

        // ③ 在「选单位」页逐层下钻：第一屏 → Num5 执行第一行 → 第二屏 → Num5 → 第三屏（单位）
        sb.AppendLine($"===== 在「选单位」页逐层下钻（Num8 顶到第一行 + Num5 执行） =====");
        for (int level = 0; level < 3; level++)
        {
            vm.ApplyPanelKey(PanelKeyCommand.Digit8);   // 顶到第一行（表头/返回行会被 IsActionable 跳过去）
            vm.ApplyPanelKey(PanelKeyCommand.Digit8);
            sb.AppendLine($"  >>> 第 {level + 1} 屏 高亮行 = {V(vm.Navigator.Selected?.Label)} action={V(vm.Navigator.Selected?.Action)}");
            vm.ApplyPanelKey(PanelKeyCommand.Digit5);
            sb.AppendLine($"  --- Num5 之后 结果提示={V(vm.PanelResultText)} ---");
            DumpRows($"drill-{level + 1}");
        }

        // ④ 第 3 层抬头：**四阵营 × 六分类**各一条（改前/改后的权威对照口径）。
        // 抬头只受 ShortDomainLabel 影响；第 2 层列表项文字（DomainLabel）在这里也一并打印以证明未动。
        sb.AppendLine("===== 第 3 层抬头（四阵营 × 六分类） =====");
        vm.PanelMode = true;
        foreach (var faction in UnitTaxonomy.FactionOrder)
        {
            vm.SelectPanelPage("选单位");               // 进页 = 回到阵营层（每次从头挑）
            vm.RefreshPanelRows();                       // PanelRows 是 Navigator.Rows 的缓存，页没变就得手动刷
            var factionRow = vm.PanelRows.FirstOrDefault(row => row.Action == "findfaction" && (string?)row.Payload == faction);
            if (factionRow is null) { sb.AppendLine($"  {faction}: <该阵营不在列表里>"); continue; }
            vm.ActivatePanelRow(factionRow);            // 阵营 → 分类层
            foreach (var domain in UnitTaxonomy.DomainOrder)
            {
                var domainRow = vm.PanelRows.FirstOrDefault(row => row.Action == "finddomain" && row.Payload is UnitDomain d && d == domain);
                if (domainRow is null) { sb.AppendLine($"  {faction,-4} × {UnitTaxonomy.DomainLabel(domain),-12} <该阵营无此分类，不列>"); continue; }
                var level2Label = domainRow.Label;      // 第 2 层列表项文字（应与 DomainLabel 一致，未动）
                vm.ActivatePanelRow(domainRow);         // 分类 → 单位层
                var head = vm.PanelRows.FirstOrDefault(row => row.Action == "find-head");
                sb.AppendLine($"  {faction,-4} × {level2Label,-12} → 第3层抬头 {V(head?.Label)}");
                vm.ActivatePanelRow(vm.PanelRows.FirstOrDefault(row => row.Action == "find-back"));   // 回分类层
            }
        }

        string text = sb.ToString();
        Console.OutputEncoding = Encoding.UTF8;
        Console.Write(text);
        string path = Path.Combine(output, "rows.txt");
        File.WriteAllText(path, text, new UTF8Encoding(false));    // 权威证据：UTF-8 文件
        Console.WriteLine($"\nWROTE {path}");
        return 0;
    }
}
