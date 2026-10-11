using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ra3Trainer.App;
using Ra3Trainer.App.Diagnostics;
using Ra3Trainer.App.ViewModels;
using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Runtime;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string label) { _checks++; if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }    // A plain Button inside a ToggleButton template is not the control a press
    // actually reaches: the hit test walks up to the ToggleButton. Exclude those.
    private static bool InsideToggle(System.Windows.DependencyObject node, System.Windows.DependencyObject root)
    {
        for (var n = node; n is not null && !ReferenceEquals(n, root); n = System.Windows.Media.VisualTreeHelper.GetParent(n))
            if (n is System.Windows.Controls.Primitives.ToggleButton) return true;
        return false;
    }

    private static System.Collections.Generic.IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }
    private static void InvokeButton(System.Windows.Controls.Button button)
    {
        // 和产品自己的点击路径一样：走控件的 automation peer，Command 与 Click 都按真实语义执行。
        if (new System.Windows.Automation.Peers.ButtonAutomationPeer(button) is System.Windows.Automation.Provider.IInvokeProvider invoker)
            invoker.Invoke();
    }

    [STAThread] private static int Main(string[] args)
    {
        string output = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/round37/panel");
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Ra3Trainer.App;component/Themes/GlobalStyles.xaml", UriKind.Relative) });
        var memory = new SmokeMemory();
        // 老板机上的 mod 别名（日冕 + ARMod）—— 面板夹具按真实配置读；别的键缺省，不影响既有断言。
        File.WriteAllText(Path.Combine(output, "settings.json"),
            """{ "ModSourceAliases": { "corona": "日冕", "armod": "ARKMOD" } }""");
        // 「当前玩的是哪个 mod」的硬信号（进程命令行 + 已加载模块）：夹具默认**不碰真机器**，
        // 用一个可替换的假快照；M8b 段按需换成假模块列表/假命令行，走真实 vm+面板路径验证。
        var liveSignals = ModSignalSnapshot.Empty;
        Func<string?, int?, ModSignalSnapshot> modSignals = (path, _) => liveSignals with { LauncherPath = path };
        using var vm = new MainViewModel(TrainerRuntimeAssets.LoadManifest(), new TrainerAppSettingsStore(Path.Combine(output,"settings.json")),
            new SmokeLauncher(),new SmokeLocator(),_=>memory,_=>memory,new SmokeSuspender(),_=>[], modSignals: modSignals);
        var foreground = GetForegroundWindow(); var focus = GetFocus(); int activated = 0;
        using var panel = new OffscreenPanel(vm);
        var window = app.Windows.OfType<FloatingWindow>().Single(); window.Activated += (_,_) => activated++;
        Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero && window.Opacity == 0, "offscreen host never shown or allocated HWND");
        byte[]? reinforcement = null;
        foreach (string section in ConsoleNavigation.Sections)
        {
            vm.OverlayPanelSection = section;
            // Apply the real binding engine / templates, never draw a replacement panel.
            panel.Render(); window.Dispatcher.Invoke(()=>{}, DispatcherPriority.ApplicationIdle);
            var bitmap = panel.Render();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string basename = section switch
            {
                "启动" => "start",
                "增援" => "reinforcement",
                "功能" => "features",
                "诊断" => "diagnostics",
                _ => "page"
            };
            using (var png = File.Create(Path.Combine(output, basename + ".png"))) encoder.Save(png);
            byte[] pixels = new byte[360*440*4]; bitmap.CopyPixels(pixels,1440,0);
            Check(pixels.Where((_,i)=>i%4==3).Any(a=>a>0), section+" nonempty WPF alpha");
            Check(pixels.Distinct().Count()>40, section+" actual text/control pixel variation");

            // 用户报的 bug：面板「功能」页写的是修改器开关，游戏功能（加钱/电力/秘技点）一个都没有。
            // 这一页现在必须列出它们；哪天再被改掉，这里当场炸。
            if (section == "功能")
            {
                while (vm.PanelPage != "功能") panel.DispatchPanelKey(PanelKeyMap.Numpad6);
                var labels = vm.PanelRows.Select(row => row.Label).ToArray();
                Check(labels.Any(text => text.Contains("增加玩家战场资金")), "面板功能页列出「增加玩家战场资金」");
                Check(labels.Any(text => text.Contains("无限电力")), "面板功能页列出「无限电力」");
                Check(labels.Any(text => text.Contains("无限秘密协议点数")), "面板功能页列出「无限秘密协议点数」");
                Check(vm.PanelRows.Count(row => row.Kind == PanelRowKind.Position) == 1, "功能页只有一行「面板位置」");
                // 用户的要求：31/32 项全都要在面板上，而且每项旁边要标出快捷键。
                var featureRows = vm.PanelRows.Where(row => row.Action.StartsWith("feat:", StringComparison.Ordinal)).ToArray();
                var catalogCount = vm.Groups.Sum(group => group.Features.Count);
                Check(featureRows.Length == catalogCount,
                    $"面板功能页列全了所有功能 {featureRows.Length}/{catalogCount}");
                var moneyRow = featureRows.Single(row => row.Action == "feat:Moeny");
                Check(moneyRow.Detail.Contains("Ctrl+F1", StringComparison.Ordinal),
                    "钱那一行标出了它的快捷键: " + moneyRow.Detail);
                Check(featureRows.Count(row => row.Detail.Length > 0) >= 28,
                    $"绝大多数功能行都标了快捷键 ({featureRows.Count(row => row.Detail.Length > 0)}/{featureRows.Length})");
                foreach (var group in vm.Groups)
                {
                    Check(vm.PanelRows.Any(row => row.Kind == PanelRowKind.Status && row.Label == group.Name),
                        "功能页有分组标题: " + group.Name);
                }
                var specs = vm.PanelRows.Where(row => row.KeyLabel.Length > 0).Select(row => row.KeyLabel).ToArray();
                Check(specs.Length == specs.Distinct().Count(), "功能页没有两行抢同一个键: " + string.Join(" ", specs));
                // 按键只在面板模式生效（这是设计要求），夹具中途可能是关的：记下原值、临时打开、用完还原。
                var panelModeWasOn = vm.PanelMode;
                vm.PanelMode = true;
                panel.DispatchPanelKey(PanelKeyMap.Decimal);   // 万一还留在「位置调整」，Num. 退出
                // 用户的预期：钱那一行"接着按就继续增加"。高亮先走到那一行，再连按两下 Num5。
                var moneyFeatureRow = vm.PanelRows.First(row => row.Action == "feat:Moeny");
                for (var guard = 0; guard < 80 && !ReferenceEquals(vm.Navigator.Selected, moneyFeatureRow); guard++)
                    panel.DispatchPanelKey(PanelKeyMap.Numpad2);
                Check(ReferenceEquals(vm.Navigator.Selected, moneyFeatureRow), "高亮走到了钱那一行");
                var moneyBefore = vm.MoneyAmountText;
                panel.DispatchPanelKey(PanelKeyMap.Numpad5);
                var afterFirst = vm.MoneyAmountText;
                Check(afterFirst != moneyBefore, $"按一次 Num5 资金往上加 {moneyBefore} -> {afterFirst}");
                panel.DispatchPanelKey(PanelKeyMap.Numpad5);
                Check(vm.MoneyAmountText != afterFirst, $"接着按继续增加 {afterFirst} -> {vm.MoneyAmountText}");
                vm.MoneyAmountText = moneyBefore;
                vm.PanelMode = panelModeWasOn;
            }
            if(section=="增援") reinforcement=pixels;
        }
        if(reinforcement is null) throw new Exception("fixture wiring broken: no 增援 section frame was captured");
        vm.OverlayPanelSection = "增援";panel.Render();window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        var baseline=panel.Pixels(); vm.ReinforcementCountText="23";
        panel.Render();window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        var changed=panel.Pixels();Check(!baseline.SequenceEqual(changed),"existing view-model live numeric change changes panel bitmap");
         vm.TogglePanelMode();
         panel.Render();window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
         Console.WriteLine("DEBUG PanelMode=" + vm.PanelMode + " text='" + vm.PanelModeText + "' diff=" + changed.Zip(panel.Pixels()).Count(p=>p.First!=p.Second));
         { var b2=panel.Render(); var e2=new PngBitmapEncoder(); e2.Frames.Add(BitmapFrame.Create(b2)); using var f2=File.Create(Path.Combine(output,"mode-on.png")); e2.Save(f2); }
         { var b3=new PngBitmapEncoder(); b3.Frames.Add(BitmapFrame.Create(panel.Render())); using var f3=File.Create(Path.Combine(output,"mode-on2.png")); b3.Save(f3); }
         Check(!changed.SequenceEqual(panel.Pixels()),"panel mode mark changes panel bitmap");
         vm.TogglePanelMode();
         panel.Render();window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
         Check(changed.SequenceEqual(panel.Pixels()),"panel mode off restores the same bitmap");
        // ---- M7: 面板 = 一格一动作（没有光标，每行印自己的键） ----
        // 用户实测：9 分钟里按了 209 次 Num2 在爬行、Num1/Num7 是死键、爬到"位置"行就误入挪面板。
        // 这一块钉住新模型：每行印自己的键、按编号直达、位置调整只能鼠标点进。
        {
            var navigator = vm.Navigator;
            PanelNavRow RowByKey(string key)
            {
                for (var i = 0; i < navigator.RowCount; i++) if (navigator.Rows[i].KeyLabel == key) return navigator.Rows[i];
                return null!;
            }

            int IndexOfRow(Func<PanelNavRow, bool> predicate)
            {
                for (var i = 0; i < navigator.RowCount; i++) if (predicate(navigator.Rows[i])) return i;
                return -1;
            }

            vm.PanelMode = false;
            panel.Render(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var outsidePixels = panel.Pixels();
            var frozenSection = vm.OverlayPanelSection;
            var frozenCount = vm.ReinforcementCountText;
            panel.DispatchPanelKey(PanelKeyMap.Numpad5);
            panel.DispatchPanelKey(PanelKeyMap.Add);
            Check(vm.OverlayPanelSection == frozenSection && vm.ReinforcementCountText == frozenCount,
                "M7 keys outside panel mode do nothing at all");

            vm.PanelMode = true;
            panel.Render(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Check(!outsidePixels.SequenceEqual(panel.Pixels()), "M7 entering panel mode redraws the panel");
            Check(vm.PanelLayer == MainViewModel.PanelLayerMain, "M7 entering panel mode lands on the main layer");
            // 面板页和主窗页签是一份状态：进面板先把页切回增援（下面的断言都基于增援页）
            while (vm.PanelPage != "增援") panel.DispatchPanelKey(PanelKeyMap.Numpad6);
            Check(vm.PanelPage == "增援", "M7 面板页能切回增援: " + vm.PanelPage);

            var keys = navigator.Rows.Where(row => row.KeyLabel.Length > 0).Select(row => row.KeyLabel).ToArray();
            var claimed = keys.SelectMany(key => key.Select(ch => ch.ToString())).ToArray();
            Check(claimed.Length == claimed.Distinct().Count(), "M7 no key is claimed by two rows");
            // 口径 B（2026-10-06 用户选 B）：8/2/4/6/5 是方向/执行，**编号直达整个拆掉**。
            // 所以行上只剩 "0"（用游戏里选中的）"."（找兵）"+ - * /"（调数值/切换）——
            // 1/3/7/9 不再占键（收藏槽改用 8/2 走到 + 5 执行）。这条从"必须占 10 个"反向加严为"数字编号一个都不许占"。
            // 口径演进：`0` 归「返回」、`.`（选兵种行）已删、空槽不再列 ⇒ 行上只剩数值行的 `+ - * /`。
            Check(claimed.Length == 4 && claimed.Distinct().Count() == 4,
                "M7 口径 行表只剩 4 个键（+ - * /，都属数值行）: " + claimed.Length);
            Check(!claimed.Contains("1") && !claimed.Contains("3") && !claimed.Contains("7") && !claimed.Contains("9")
                && !claimed.Contains("2") && !claimed.Contains("4") && !claimed.Contains("5")
                && !claimed.Contains("6") && !claimed.Contains("8"),
                "M7 口径B 行上不许再印任何数字编号（编号直达已取消）");
            Check(claimed.Contains("+") && claimed.Contains("/") && !claimed.Contains("0") && !claimed.Contains("."),
                "M7 口径 行上只留运算符；0 已归「返回」（全局键，不占行）、. 随选兵种行一起删掉");
            // 口径 B：只有 0 / . / + - * / 这些**非编号**键对应行；
            // 1 3 7 9 这类数字编号一个都不许对应（编号直达已取消，收藏槽改用 8/2 走到 + 5 执行）。
            foreach (var spec in new[] { "+", "-", "*", "/" })
            {
                Check(navigator.Rows.Count(row => row.KeyLabel.Contains(spec, StringComparison.Ordinal)) == 1,
                    "M7 口径 Num" + spec + " 恰好对应一行");
            }

            foreach (var spec in new[] { "1", "3", "7", "9" })
            {
                Check(navigator.Rows.All(row => !row.KeyLabel.Contains(spec, StringComparison.Ordinal)),
                    "M7 口径B Num" + spec + " 不许再对应任何行（编号直达已拆）");
            }

            // 数字键盘的通用语义：8/2 上下、4/6 左右、5 确定 —— 这几个键不许被某一行占掉，
            // 换谁上来都是这么按的（不是给某个人定制的映射）。
            foreach (var spec in new[] { "8", "2", "4", "6", "5" })
            {
                Check(navigator.Rows.All(row => !row.KeyLabel.Contains(spec, StringComparison.Ordinal)),
                    "M7 Num" + spec + " 是方向/确定键，不占行");
            }

            navigator.ClearSelection();
            panel.DispatchPanelKey(PanelKeyMap.Numpad2);
            Check(navigator.SelectedIndex >= 0 && navigator.Rows[navigator.SelectedIndex].Kind != PanelRowKind.Status,
                "M7 Num2 把高亮落到第一个能执行的行: " + navigator.SelectedIndex);
            var firstHighlight = navigator.SelectedIndex;
            panel.DispatchPanelKey(PanelKeyMap.Numpad2);
            Check(navigator.SelectedIndex > firstHighlight, "M7 再按 Num2 往下走一行");
            panel.DispatchPanelKey(PanelKeyMap.Numpad8);
            Check(navigator.SelectedIndex == firstHighlight, "M7 Num8 回到上一行");

            // Num5 = 确定：先用 Num8/Num2 选到「呼叫增援」那一行再按。
            // 夹具里没装 patch，所以它必须"说清为什么发不出去"，不能装作发出去了
            navigator.Select(IndexOfRow(row => row.Action == "send"));
            panel.DispatchPanelKey(PanelKeyMap.Numpad5);
            Check(vm.PanelResultText.Contains("不可用"), "M7 发兵失败会说明原因，不再静默: " + vm.PanelResultText);

            vm.ReinforcementCountText = "8";
            vm.ReinforcementRankText = "1";   // 星级上限是 3，用 1 起步才验得出 ±1（而不是撞上限）
            panel.DispatchPanelKey(PanelKeyMap.Add);
            Check(vm.ReinforcementCountText == "9", "M7 Num+ 数量加一");
            panel.DispatchPanelKey(PanelKeyMap.Subtract);
            panel.DispatchPanelKey(PanelKeyMap.Subtract);
            Check(vm.ReinforcementCountText == "7", "M7 Num- 数量减一");
            panel.DispatchPanelKey(PanelKeyMap.Multiply);
            Check(vm.ReinforcementRankText == "2", "M7 Num* 星级加一");
            panel.DispatchPanelKey(PanelKeyMap.Divide);
            Check(vm.ReinforcementRankText == "1", "M7 Num/ 星级减一");
            vm.ReinforcementRankText = ReinforcementSettings.MaxRank.ToString();
            panel.DispatchPanelKey(PanelKeyMap.Multiply);
            Check(vm.ReinforcementRankText == ReinforcementSettings.MaxRank.ToString(), "M7 星级夹在文档写的上限");
            vm.ReinforcementRankText = "3";
            var valueRows = navigator.Rows.Where(row => row.Kind == PanelRowKind.Value && row.IsAdjustable).ToArray();
            Check(valueRows.Length == 2, "M7 数值行（数量/星级）两行都在，可调");
            // 口径（HANDOFF-2026-10-07 §3）：数值行靠「5 进调节态 + 4/6 增减」，**行上不挂按键提示**。
            // 旧口径在行上印 Num+/- / Num*// （KeyDisplay 非空）—— 用户 2026-10-07 实机又看到那一对键，就是这条。
            Check(valueRows.All(row => row.KeyDisplay.Length == 0),
                "M7 数值行行上不印按键提示（数量/星级 KeyDisplay 为空）: " +
                string.Join(" ", valueRows.Select(row => row.Action + "=" + (row.KeyDisplay.Length == 0 ? "<empty>" : row.KeyDisplay))));
            Check(valueRows.All(row => row.KeyLabel.Length > 0),
                "M7 数值行的 KeyLabel 仍在（派发依据，Num+/- 、Num*// 还得能派到这两行）");
            vm.ReinforcementCountText = ReinforcementSettings.MaxCount.ToString();
            panel.DispatchPanelKey(PanelKeyMap.Add);
            Check(vm.ReinforcementCountText == ReinforcementSettings.MaxCount.ToString(), "M7 数量夹在文档写的上限");

            // ---- OCR 复核（2026-10-07）抓到的三个面板缺陷的回归钉 ----
            // ① 走开必须退出调节态（口径原话："方向键走开自动退出"）。原代码里注释这么写着、行为却没有：
            //    调节态留着 ⇒ 接着按 4/6 拿**新那一行**去改数，而那行常常不是数值行 ⇒ 一声不响什么都没发生。
            {
                vm.ReinforcementCountText = "8";
                WalkToAndExecute(row => row.Action == "count");     // 末尾自带一次 Num5 = 进调节态
                Check(vm.PanelResultText.Contains("调节"), "OCR① 数值行按 5 进调节态: " + vm.PanelResultText);
                panel.DispatchPanelKey(PanelKeyMap.Numpad2);        // 走开一行
                panel.DispatchPanelKey(PanelKeyMap.Numpad6);        // 再按 +：不许"改了别的行"，也不许静默
                Check(vm.ReinforcementCountText == "8", "OCR① 走开之后按 6 不改任何数: " + vm.ReinforcementCountText);
                Check(vm.PanelResultText.Contains("先走到数值行按 5"),
                    "OCR① 走开即退出调节态（并提示怎么回去）: " + vm.PanelResultText);
                WalkToAndExecute(row => row.Action == "count");
                panel.DispatchPanelKey(PanelKeyMap.Numpad6);
                Check(vm.ReinforcementCountText == "9", "OCR① 重新进调节态后 6 照样能加: " + vm.ReinforcementCountText);
                panel.DispatchPanelKey(PanelKeyMap.Numpad5);        // 确认退出调节态
            }
            // ② 位置格：键盘不许进（只有鼠标点那一格才进）。否则 8/2 一路走到底再按一下 5，
            //    面板就切成"挪位置"，从此 8/2/4/6 全变成挪面板 —— 正是当初要防的误触。
            {
                navigator.Select(IndexOfRow(row => row.Kind == PanelRowKind.Position));
                panel.DispatchPanelKey(PanelKeyMap.Numpad5);
                Check(!vm.PanelPositionMode, "OCR② 位置行按 5 不进位置调整（只能鼠标点）");
                Check(vm.PanelResultText.Contains("鼠标"), "OCR② 并且说清要用鼠标点: " + vm.PanelResultText);
                panel.DispatchPanelKey(PanelKeyMap.Numpad6);
                // 注意：提示语本身含"…按 5 进入调节"这几个字，所以不能用"含调节"当判据，
                // 要判"当前没有正在调的数值行" —— 那句提示正好就是"不在调节态"的标志。
                Check(!vm.PanelPositionMode && vm.PanelResultText.StartsWith("先走到数值行按 5", StringComparison.Ordinal),
                    "OCR② 位置行上 6 既不动面板也没进调节态: " + vm.PanelResultText);
            }
            // ③ 关面板再开：调节态必须一起清掉（原来只清了层/位置模式，调节态跨模式残留）
            {
                WalkToAndExecute(row => row.Action == "count");
                Check(vm.PanelResultText.Contains("调节"), "OCR③ 先进入调节态: " + vm.PanelResultText);
                vm.PanelMode = false;
                vm.PanelMode = true;
                panel.DispatchPanelKey(PanelKeyMap.Numpad6);
                Check(vm.PanelResultText.Contains("先走到数值行按 5"),
                    "OCR③ 关面板再开不残留调节态: " + vm.PanelResultText);
            }

            // 口径 B 的通用助手：从当前位置走到满足条件的行，再按 Num5（执行）。
            // 编号直达已取消，所以夹具只能用方向走 —— 和用户真人按键完全一致（IsCurrent 就是高亮行）。
            //
            // ★ 换层时导航器的高亮索引是**保留**的 ★：SetRows 只做夹取（`_index >= Count ⇒ Count-1`），
            // 不重置。所以进新层后目标行很可能在当前高亮**上方**，只往下走会一路顶到列表底然后停住，
            // 永远到不了目标（夹具就卡在这一步，不是产品坏）。所以先按 Num8 把高亮顶回第一行（到顶即停、不绕圈），
            // 再往下走；走不到就打印真实行表当证据，别靠猜。
            void WalkToAndExecute(Func<PanelNavRow, bool> predicate)
            {
                if (navigator.SelectedIndex < 0) panel.DispatchPanelKey(PanelKeyMap.Numpad2);   // 没高亮：先落到第一行
                for (var step = 0; step < 64; step++)
                {
                    var at = navigator.SelectedIndex;
                    panel.DispatchPanelKey(PanelKeyMap.Numpad8);
                    if (navigator.SelectedIndex >= at) break;   // 到顶了（8 已经走不动）
                }

                for (var step = 0; step < 64; step++)
                {
                    var hit = navigator.Rows.FirstOrDefault(predicate);
                    if (hit is not null && hit.IsCurrent) break;
                    panel.DispatchPanelKey(PanelKeyMap.Numpad2);   // 小键盘 2 = 下
                }

                var target = navigator.Rows.FirstOrDefault(predicate);
                if (target is null || !target.IsCurrent)
                {
                    Console.WriteLine("WALK-MISS 高亮=" + navigator.SelectedIndex + " 目标存在=" + (target is not null)
                        + " 行表=" + string.Join(" | ", navigator.Rows.Select((row, i) =>
                            $"{i}:{row.Kind}/{row.Action}/{row.Label}")));
                }

                panel.DispatchPanelKey(PanelKeyMap.Numpad5);       // 小键盘 5 = 执行
            }

            // 找兵：**真阵营** → 游戏分类 → 单位（一屏 10 个，8/2 走到 + 5 执行，Num+/Num- 翻屏）
            // 用户 2026-10-07 口径 B：第一层从"选军种"（陆/空/海/建筑/特殊）改成**字面阵营**（盟军/苏联/帝国/神州）。
            // 口径演进：`.`（选兵种行）已删 —— 选择器现在是独立页「03 选单位」。
            vm.SelectPanelPage("选单位");
            Check(vm.PanelLayer == MainViewModel.PanelLayerFaction, "M7 进「选单位」页是第 1 层（真阵营，不再是军种）");
            // 屏幕上不许再出现「军种」字样：抬头文案必须跟实际层级一致（用户要求）。
            Check(!vm.PanelRows.Any(row => row.Label.Contains("军种", StringComparison.Ordinal)
                    || row.State.Contains("军种", StringComparison.Ordinal) || row.Detail.Contains("军种", StringComparison.Ordinal)),
                "M7 第一层屏幕上不再出现「军种」字样: " + string.Join(" | ", vm.PanelRows.Select(row => row.Label)));
            Check(navigator.Rows.First(row => row.Action == "find-head").Label == "找兵 · 选阵营",
                "M7 第一层抬头写的是「找兵 · 选阵营」: " + navigator.Rows.First(row => row.Action == "find-head").Label);
            var factionRows = navigator.Rows.Where(row => row.Action == "findfaction").ToArray();
            // 真阵营四个（盟军/苏联/帝国/神州）一屏放得下 —— 官方库 500 条按分类第 2 列实测就是这四个。
            Check(factionRows.Length == 4, "M7 阵营四个一屏放得下: " + factionRows.Length);
            var factions = factionRows.Select(row => (string)row.Payload!).ToArray();
            Check(factions.SequenceEqual(new[] { "盟军", "苏联", "帝国", "神州" }),
                "M7 第一层就是真阵营（盟军/苏联/帝国/神州，顺序固定）: " + string.Join("/", factions));
            // 各阵营单位数加起来 = 全库（证明没有单位被第一层丢掉）。
            var factionSum = factionRows.Sum(row => int.Parse(row.Detail.Replace(" 个", string.Empty)));
            Check(factionSum == vm.ReinforcementUnits.Count,
                $"M7 四个阵营单位数合计 = 全库 {factionSum}/{vm.ReinforcementUnits.Count}（一个都不丢）");
            // 口径 B：行上不再印编号（编号直达已拆），阵营改用 8/2 走到 + 5 执行。
            // 正确契约：派发用的 KeyLabel 必须保留（清空它派发全断，用户实机验过"只剩 7/9 有用"），
            // 但显示用的 KeyDisplay 对纯数字必须为空 ⇒ 行上不印编号。这条比原来更严：两个字段都断言。
            Check(factionRows.All(row => row.KeyDisplay.Length == 0),
                "M7 口径 阵营行不印编号（KeyDisplay 对数字为空）");
            Check(factionRows.All(row => row.KeyLabel.Length > 0),
                "M7 口径 阵营行的 KeyLabel 仍在（派发依据，不能清）");

            // 阵营 → 游戏分类（第 2 层）。挑**最大的阵营**（神州，148 个）进第二层。
            // 口径 B：行上没编号了，改成 8/2 走到目标行 + 5 执行（用行自身的 IsCurrent 判断到没到）。
            var biggestFaction = factionRows.OrderByDescending(row => int.Parse(row.Detail.Replace(" 个", string.Empty))).First();
            var biggestFactionName = (string)biggestFaction.Payload!;
            WalkToAndExecute(row => row.Action == "findfaction" && (string)row.Payload! == biggestFactionName);
            Check(vm.PanelLayer == MainViewModel.PanelLayerDomain && vm.PanelFaction == biggestFactionName,
                "M7 选阵营(" + biggestFactionName + ")进游戏分类层");
            // 第二层抬头：文案必须跟层级一致（2026-10-07 统一为「找兵 · <阵营>」，与第 1 层「找兵 · 选阵营」同前缀），
            // 且面板上不许再出现「用途」字样。
            var level2Head = navigator.Rows.First(row => row.Action == "find-head");
            Check(level2Head.Label == "找兵 · " + biggestFactionName,
                "M7 第二层抬头是「找兵 · 阵营」: " + level2Head.Label);
            Check(!level2Head.Label.Contains("选单位", StringComparison.Ordinal),
                "M7 第二层抬头不再写「选单位」: " + level2Head.Label);
            Check(!vm.PanelRows.Any(row => row.Label.Contains("用途", StringComparison.Ordinal)
                    || row.State.Contains("用途", StringComparison.Ordinal) || row.Detail.Contains("用途", StringComparison.Ordinal)),
                "M7 面板流程里不再有「用途」这一层（需求 2026-10-07：去掉更清爽）");
            var domainRows = navigator.Rows.Where(row => row.Action == "finddomain").ToArray();
            Check(domainRows.Length >= 1, "M7 分类层列出该阵营的游戏分类: " + domainRows.Length);
            // 本阵营没有的分类**不列**（不显示空页）：列出的每一类在本阵营下都真的 >0。
            Check(domainRows.All(row => int.Parse(row.Detail.Replace(" 个", string.Empty)) > 0),
                "M7 分类层不列空分类（每一类在本阵营下都 >0）");
            var domainSum = domainRows.Sum(row => int.Parse(row.Detail.Replace(" 个", string.Empty)));
            var factionUnitCount = vm.ReinforcementUnits.Count(unit => unit.Faction == biggestFactionName);
            Check(domainSum == factionUnitCount,
                $"M7 {biggestFactionName} 各分类单位数合计 = 该阵营全库 {domainSum}/{factionUnitCount}（一个都不丢）");
            Check(domainRows.Select(row => row.KeyLabel).Distinct().Count() == domainRows.Length, "M7 分类行编号不重复");
            // 第二层就是**游戏自带分类**（步兵/车辆/飞行器/舰船/建筑/特殊这一维），不是我们自造的用途标签。
            Check(domainRows.All(row => row.Payload is UnitDomain),
                "M7 第二层载荷是游戏分类枚举（UnitDomain）");

            // 分类 → 单位层：走到**最大的那个分类**（列举按 DomainOrder 固定顺序）——
            // 顺带钉住"（阵营 + 分类）超过一屏也必须翻得完、不截断"。
            var biggestDomainRow = domainRows.OrderByDescending(row => int.Parse(row.Detail.Replace(" 个", string.Empty))).First();
            var biggestDomain = (UnitDomain)biggestDomainRow.Payload!;
            var biggestDomainCount = int.Parse(biggestDomainRow.Detail.Replace(" 个", string.Empty));
            WalkToAndExecute(r => r.Action == "finddomain" && r.Payload is UnitDomain d && d == biggestDomain);
            Check(vm.PanelLayer == MainViewModel.PanelLayerUnit && vm.PanelDomain == biggestDomain, "M7 进单位层");
            // 第三层抬头：文案核对。2026-10-07 用户逐字批准简化（原话"可以"）：
            // 抬头 = **阵营 + 细分类型**，去掉「陆军/空军/海军」那段军种前缀 ——
            // 例：盟军 · 陆军 · 步兵 → 盟军 · 步兵；建筑/特殊 本身就是细分类型，原样保留。
            var level3Head = navigator.Rows.First(row => row.Action == "find-head");
            Check(level3Head.Label == biggestFactionName + " · " + UnitTaxonomy.ShortDomainLabel(biggestDomain),
                $"M7 第三层抬头恰好是「阵营 · 细分类型」: {level3Head.Label}");
            Check(level3Head.Label.Count(ch => ch == '·') == 1,
                "M7 第三层抬头只有一个「·」（阵营 · 细分类型，不再带军种前缀）: " + level3Head.Label);
            Check(!level3Head.Label.Contains("陆军", StringComparison.Ordinal)
                    && !level3Head.Label.Contains("空军", StringComparison.Ordinal)
                    && !level3Head.Label.Contains("海军", StringComparison.Ordinal)
                    && !level3Head.Label.Contains("防御/生产", StringComparison.Ordinal),
                "M7 第三层抬头去掉了军种前缀与用途尾巴: " + level3Head.Label);
            var unitRows = navigator.Rows.Where(row => row.Action == "findunit").ToArray();
            Check(unitRows.Length is > 0 and <= 10, "M7 单位层一屏最多 10 个: " + unitRows.Length);
            Check(unitRows.Select(row => row.KeyLabel)
                    .SequenceEqual(new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" }.Take(unitRows.Length)),
                "M7 单位行键位仍是 1..9/0（元数据），但口径 B 下按号不再直达");
            Check(unitRows.All(row => row.KeyDisplay.Length == 0),
                "M7 单位行屏幕上一个编号都不印（口径B 编号直达已拆）");
            // 用途标签已从面板流程去掉：单位行 Detail 不再挂「用途」标签。
            Check(unitRows.All(row => row.Detail.Length == 0),
                "M7 单位行不再挂「用途」标签（Detail 为空）: " + string.Join(" | ", unitRows.Select(row => row.Label + "=" + row.Detail)));
            Check(navigator.Rows.Any(row => row.Action == "find-next") && navigator.Rows.Any(row => row.Action == "find-prev"),
                "M7 单位层有翻屏键");
            // 翻遍所有屏，把这一组（阵营 + 分类）的单位全收齐 —— 证明超过一屏也看得到全部、不截断。
            var screensExpected = (biggestDomainCount + 9) / 10;
            Check(screensExpected >= 2, "M7 这一（阵营+分类）组确实超过一屏（要翻屏才看得全）: " + screensExpected + " 屏");
            var seenCodes = new System.Collections.Generic.HashSet<uint>();
            for (var screen = 0; ; screen++)
            {
                foreach (var row in navigator.Rows.Where(row => row.Action == "findunit"))
                    seenCodes.Add(((ReinforcementUnitEntry)row.Payload!).Code);
                Check(vm.PanelScreen == screen, $"M7 停在预期第 {screen + 1} 屏: {vm.PanelScreen}");
                if (screen + 1 >= screensExpected) break;
                panel.DispatchPanelKey(PanelKeyMap.Add);
            }
            Check(seenCodes.Count == biggestDomainCount,
                $"M7 翻屏把（阵营+分类）的全部单位看全、不截断: {seenCodes.Count}/{biggestDomainCount}");
            while (vm.PanelScreen > 0) panel.DispatchPanelKey(PanelKeyMap.Divide);   // 回第一屏，后面的流程继续

            // 按编号直接选中（不用先挪光标）
            var target = (ReinforcementUnitEntry)navigator.Rows.First(row => row.Action == "findunit").Payload!;
            // 口径 B：编号不再可直按 ⇒ 走到那一行 + Num5（和用户真人按键完全一致）。
            WalkToAndExecute(row => row.Action == "findunit" && ReferenceEquals(row.Payload, target));
            Check(vm.SelectedReinforcementUnit is not null && vm.SelectedReinforcementUnit.Code == target.Code,
                "M7 走到那一行按 Num5 选中: " + target.Name);
            Check(vm.ReinforcementUnitIdText == target.CodeText, "M7 选中的兵种进了代号框: " + target.CodeText);
            Check(vm.PanelLayer == MainViewModel.PanelLayerMain, "M7 选完自动回主层");
            Check(vm.PanelResultText.Contains(target.Name), "M7 结果行回报选了什么: " + vm.PanelResultText);
            // 结果行 2026-10-07 统一为「阵营 · 短分类名」：不得再出现「盟军 · 陆军 · 步兵」这类三段式。
            Check(vm.PanelResultText.Contains(target.Faction + " · " + UnitTaxonomy.ShortDomainLabel(target.Domain)),
                "M7 结果行是「阵营 · 短分类名」: " + vm.PanelResultText);
            Check(!vm.PanelResultText.Contains("陆军 ·", StringComparison.Ordinal)
                    && !vm.PanelResultText.Contains("空军 ·", StringComparison.Ordinal)
                    && !vm.PanelResultText.Contains("海军 ·", StringComparison.Ordinal),
                "M7 结果行去掉了军种前缀: " + vm.PanelResultText);
            Check(vm.PanelResultText.Count(ch => ch == '·') == 1,
                "M7 结果行只剩阵营与分类之间一个「·」: " + vm.PanelResultText);

            // 收藏槽：选过的兵自动进槽，之后一键换回来
            Check(vm.PanelSlots.Any(unit => unit.Code == target.Code), "M7 选过的兵自动进收藏槽");
            var slotIndex = vm.PanelSlots.Select((unit, index) => (unit, index)).First(pair => pair.unit.Code == target.Code).index;
            var slotKey = new[] { "1", "3", "7", "9" }[slotIndex];
            var slotRow = RowByKey(slotKey);
            Check(slotRow is not null && slotRow.IsCurrentUnit,
                "M7 当前兵种所在的槽被标出来（IsCurrentUnit，和导航高亮是两回事）: Num" + slotKey);
            // 不光看模型：那一行的竖条**真的**画出颜色了吗 —— 读视觉树里的 CurrentBar 和一个普通行比。
            panel.Render(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var rowsControl = (System.Windows.Controls.ItemsControl)window.FindName("PanelRowsControl")!;
            string BarColour(PanelNavRow row)
            {
                var container = (System.Windows.DependencyObject?)rowsControl.ItemContainerGenerator.ContainerFromItem(row);
                if (container is null) return "no-container";
                return Descendants(container).OfType<System.Windows.Controls.Border>()
                    .First(candidate => candidate.Name == "CurrentBar").Background?.ToString() ?? "null";
            }
            var markedBar = BarColour(slotRow!);
            var plainBar = BarColour(navigator.Rows.First(row => row.Action == "send"));
            Check(markedBar != plainBar && markedBar is not ("no-container" or "null" or "#00FFFFFF"),
                "M7 槽位标记真的画出来了（竖条颜色与普通行不同）: 槽=" + markedBar + " 普通行=" + plainBar);
            // 空槽**不列**（设计：分组头已经写了"N 个有兵"，再摆一排"（空槽）"自相矛盾还占屏）
            // ⇒ 面板上没有任何入口能走到空槽，旧断言"空槽会说明是空的"没有可走的路，换成钉住真实契约：
            // 槽区只列有兵的槽、条数对得上、行上写的都是真兵种名。
            var slotRows0 = navigator.Rows.Where(row => row.Action == "slot").ToArray();
            Check(slotRows0.Length == vm.PanelSlots.Count && slotRows0.Length > 0,
                "M7 收藏槽只列有兵的槽，条数对得上: 行 " + slotRows0.Length + " / 有兵 " + vm.PanelSlots.Count);
            Check(slotRows0.All(row => vm.PanelSlots.Any(unit => unit.Name == row.Label)),
                "M7 每条槽行写的都是真兵种名（没有「空槽」占位）: " + string.Join(" | ", slotRows0.Select(row => row.Label)));

            // 换成另一个兵，再换回槽位 —— 全程走真实入口，不借任何"暗门键"
            vm.SelectPanelPage("选单位");        // 用户路径：Num7/9 切到「选单位」页
            Check(vm.PanelLayer == MainViewModel.PanelLayerFaction, "M7 切到「选单位」页回到阵营层");
            WalkToAndExecute(r => r.Action == "findfaction" && (string)r.Payload! == biggestFactionName);
            WalkToAndExecute(r => r.Action == "finddomain" && r.Payload is UnitDomain d && d == biggestDomain);   // 分类层 → 单位层
            var otherRows = navigator.Rows.Where(row => row.Action == "findunit").ToArray();
            var other = (ReinforcementUnitEntry)otherRows.First(row => ((ReinforcementUnitEntry)row.Payload!).Code != target.Code).Payload!;
            WalkToAndExecute(row => row.Action == "findunit" && ((ReinforcementUnitEntry)row.Payload!).Code == other.Code);
            Check(vm.SelectedReinforcementUnit is not null && vm.SelectedReinforcementUnit.Code == other.Code, "M7 换个兵: " + other.Name);
            // 口径 B：收藏槽也是"走到那一行 + Num5"（编号既不印、也不可直按 —— 暗门已在产品里拆掉）
            var targetSlot = vm.PanelSlots.Select((unit, index) => (unit, index)).First(pair => pair.unit.Code == target.Code).index;
            WalkToAndExecute(row => row.Action == "slot" && (int)row.Payload! == targetSlot);
            Check(vm.SelectedReinforcementUnit is not null && vm.SelectedReinforcementUnit.Code == target.Code,
                "M7 走到槽位按 Num5 换回 " + target.Name + "（第 " + (targetSlot + 1) + " 个槽）");
            Check(vm.PanelResultText.Contains("当前兵种"), "M7 结果行回报当前兵种: " + vm.PanelResultText);

            // 发兵失败的原因必须进面板结果行（用户以前只看到"没反应"）
            panel.DispatchPanelKey(PanelKeyMap.Numpad0);
            vm.StatusMessage = "失败：游戏未解析单位类型、生成函数返回空对象或新单位等级组件为空；可能已部分生成，无法回滚，请检查战场。";
            Check(vm.PanelResultText.Contains("游戏未解析单位类型"),
                "M7 发兵失败原因会进面板结果行: " + vm.PanelResultText);

            // 位置调整：键盘没有入口，只有鼠标点那一格 —— 误触从根上没了
            Check(!vm.PanelPositionMode, "M7 面板默认不在位置调整");
            var positionRow = navigator.Rows.First(row => row.Kind == PanelRowKind.Position);
            Check(positionRow.KeyLabel.Length == 0, "M7 位置格不占任何键");
            var px = vm.OverlayPanelX;
            var py = vm.OverlayPanelY;
            panel.DispatchPanelKey(PanelKeyMap.Numpad6);
            Check(vm.OverlayPanelX == px && !vm.PanelPositionMode, "M7 平时 Num6 不动面板位置");
            vm.ActivatePanelRow(positionRow);
            Check(vm.PanelPositionMode, "M7 鼠标点位置格进入位置调整");
            panel.DispatchPanelKey(PanelKeyMap.Numpad6);
            Check(vm.OverlayPanelX == px + 1, "M7 位置调整里 Num6 右移 1px");
            panel.DispatchPanelKey(PanelKeyMap.Numpad2);
            Check(vm.OverlayPanelY == py + 1, "M7 位置调整里 Num2 下移 1px");
            var countBefore = vm.ReinforcementCountText;
            panel.DispatchPanelKey(PanelKeyMap.Numpad5);
            Check(vm.ReinforcementCountText == countBefore && vm.PanelResultText.Contains("屏蔽"),
                "M7 位置调整里 Num5 不会误发兵: " + vm.PanelResultText);

            // 面板不是"只有增援"：四个页签在游戏内都到得了，Num4/Num6 换页，
            // 而且面板页和主窗页签是同一份状态（不许两份）。

            panel.DispatchPanelKey(PanelKeyMap.Numpad6);
            panel.DispatchPanelKey(PanelKeyMap.Numpad2);
            panel.DispatchPanelKey(PanelKeyMap.Decimal);
            Check(!vm.PanelPositionMode && vm.OverlayPanelX == px + 2 && vm.OverlayPanelY == py + 2,
                "M7 Num. 退出位置调整，挪好的位置留着: " + vm.OverlayPanelX + "," + vm.OverlayPanelY);
            vm.OverlayPanelX = px;
            vm.OverlayPanelY = py;
            Check(navigator.Rows.First(row => row.Kind == PanelRowKind.Position).Detail.Contains(","), "M7 位置格显示 X , Y");

            // 面板页签契约（2026-10-06 用户改口径）：面板只在游戏里能看见，所以只放"进了游戏还做的事"。
            // 装载 / 连接游戏 / 诊断都是桌面动作，放在面板里自相矛盾
            //（需求原话："我都已经是进游戏的状态了，为什么还有启动这一栏"）。
            // 断言以 OverlayPanelSections 为准（单一事实来源），并显式禁止桌面页混进面板。
            var pageAtStart = vm.PanelPage;
            Check(vm.OverlayPanelSections.Contains(pageAtStart), "M7 面板页必须在面板页签里: " + pageAtStart);
            Check(!vm.OverlayPanelSections.Contains("启动"), "M7 面板不许有「启动」页（进了游戏装载早完成）: " + string.Join("/", vm.OverlayPanelSections));
            Check(!vm.OverlayPanelSections.Contains("诊断"), "M7 面板不许有「诊断」页（桌面低频活）: " + string.Join("/", vm.OverlayPanelSections));
            Check(vm.OverlayPanelSections.Contains("选单位"), "M7 面板必须有「选单位」页（500+ 兵种独占一页；单位操作留在「功能」页，不再重复列两遍）: " + string.Join("/", vm.OverlayPanelSections));
            // 口径 B（用户 2026-10-06）：换页是 Num9（下一屏/下页）和 Num7（上一屏/上页），
            // 4/6 归"左右调数值"，不再换页 —— 老夹具这里按的是 Num6/Num4，属于旧口径。
            var pageCount = vm.OverlayPanelSections.Count;
            var seenPages = new System.Collections.Generic.List<string>();
            for (var i = 0; i < pageCount; i++)
            {
                seenPages.Add(vm.PanelPage);
                panel.DispatchPanelKey(PanelKeyMap.Numpad9);
            }

            Check(seenPages.Distinct().Count() == pageCount, $"M7 面板 {pageCount} 页在游戏内都能到: " + string.Join("/", seenPages));
            Check(vm.PanelPage == pageAtStart, "M7 转一圈回到同一页: " + vm.PanelPage);
            panel.DispatchPanelKey(PanelKeyMap.Numpad7);
            Check(vm.PanelPage != pageAtStart, "M7 Num7 往回一页: " + vm.PanelPage);
            Check(vm.OverlayPanelSection == vm.PanelPage, "M7 面板页 = 主窗页签（一处状态，不两份）: " + vm.OverlayPanelSection);
            panel.DispatchPanelKey(PanelKeyMap.Numpad9);
            Check(vm.PanelPage == pageAtStart, "M7 Num9 换回来: " + vm.PanelPage);
            while (vm.PanelPage != "增援") panel.DispatchPanelKey(PanelKeyMap.Numpad9);
            Check(vm.PanelPage == "增援", "M7 能转回增援页: " + vm.PanelPage);
            // 4/6 不许偷偷换页（旧口径的残留）：换页只能靠 7/9。
            panel.DispatchPanelKey(PanelKeyMap.Numpad4);
            Check(vm.PanelPage == "增援", "M7 Num4 不换页（4/6 归调数值，换页是 7/9）: " + vm.PanelPage);

            // 反向：主窗点页签 → 面板页跟着走（一处状态，不两份）
            // 真实用户路径：点主窗页签（Navigation.SelectCommand）→ 面板页必须跟着走。
            // 用离屏构造的主窗（构造不创建 HWND、不激活），因为同步逻辑挂在窗口上。
            var navWindow = new MainWindow(vm);
            navWindow.Navigation.SelectCommand.Execute("功能");
            Check(vm.PanelPage == "功能", "M7 点主窗页签（真实路径）面板页跟着走: " + vm.PanelPage);
            Check(vm.OverlayPanelSection == "功能", "M7 主窗页签状态一份: " + vm.OverlayPanelSection);
            // 反向：面板里换页 → 主窗页签跟着走
            vm.SelectPanelPage("增援");
            Check(navWindow.Navigation.Selected == "增援", "M7 面板换页主窗页签跟着走: " + navWindow.Navigation.Selected);
            vm.SelectPanelPage("增援");
            Check(vm.PanelPage == "增援", "M7 切回增援页");

            // 主层必须一屏放得下：不需要滚动 → 不会再有"往下翻画面不跟随"
            vm.PanelMode = true;
            vm.RefreshPanelRows();
            panel.Render(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Check(navigator.RowCount <= 18, "M7 主层行数控制在一屏内: " + navigator.RowCount);
            var board = (System.Windows.Controls.ScrollViewer)window.FindName("PanelBoardScroll")!;
            Check(board is not null, "M7 面板行板还在");
            Check(vm.PanelResultText.Length > 0, "M7 结果行永远有内容: " + vm.PanelResultText);
            // 面板只有 360×440（行板视口 ~339px），主层 18 行比它高 —— 用面板内的滚轮翻。
            // 原生侧本来就吞掉了面板矩形里的滚轮、delta 也一直在发布，以前只是没人用它。
            board.ScrollToTop();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var scrollable = board.ScrollableHeight;
            // 设计目标：主层最好一屏放得下；放不下就用面板内的滚轮翻（原生侧本来就吞了面板内的滚轮）。
            if (scrollable > 0)
            {
                var offsetBefore = board.VerticalOffset;
                panel.DispatchPanelWheel(-120);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Check(board.VerticalOffset > offsetBefore, "M7 面板里的滚轮往下翻: " + board.VerticalOffset.ToString("F1"));
                panel.DispatchPanelWheel(120);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Check(board.VerticalOffset < scrollable, "M7 滚轮也能翻回来: " + board.VerticalOffset.ToString("F1"));
                Check(true, "M7 主层比视口高 " + scrollable.ToString("F1") + "px：用滚轮翻能看全（设计上更想一屏放下）");
            }
            else
            {
                Check(true, "M7 主层一屏放得下，不用滚: viewport=" + board.ViewportHeight.ToString("F1"));
            }

            board.ScrollToTop();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Check(vm.PanelResultText.Length > 0, "M7 结果行永远有内容: " + vm.PanelResultText);

            vm.PanelMode = false;
            vm.RefreshPanelRows();
            vm.OverlayPanelSection = "启动";
            panel.Render(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }

        // ---- M7c 找兵面板「分类维度」文案全表加钉（2026-10-07 用户一次性统一为短名）----
        // 同一分类维度的**三处文案**同源：选分类提示语 / 第 2 层列表项 / 选完兵的结果行 —— 全部只留
        // 细分类型，去掉陆军/空军/海军那段军种前缀；建筑/特殊 本身就是细分类型 ⇒ 原样保留（建筑 · 防御/生产 → 建筑）。
        // 第 3 层抬头（本块下方）同样加钉。四阵营 × 六分类全表跑一遍，任一处文案回退当场炸。
        {
            var navigator = vm.Navigator;
            vm.PanelMode = true;
            // 细分类型全表：分组枚举 → 抬头里该出现的短名（这就是用户的口径，逐条比对）。
            var shortByDomain = new System.Collections.Generic.Dictionary<UnitDomain, string>
            {
                [UnitDomain.Infantry] = "步兵",
                [UnitDomain.Vehicle] = "车辆",
                [UnitDomain.Aircraft] = "飞行器",
                [UnitDomain.Ship] = "舰船",
                [UnitDomain.Structure] = "建筑",
                [UnitDomain.Special] = "特殊",
            };
            // 产品和这张表必须是同一份口径（以后改一处漏一处会在这条炸）。
            foreach (var domain in UnitTaxonomy.DomainOrder)
                Check(UnitTaxonomy.ShortDomainLabel(domain) == shortByDomain[domain],
                    $"M7c ShortDomainLabel({domain}) = {UnitTaxonomy.ShortDomainLabel(domain)}");
            var pinned = 0;
            foreach (var faction in UnitTaxonomy.FactionOrder)
            {
                vm.SelectPanelPage("选单位");
                vm.RefreshPanelRows();   // PanelRows 是 Navigator.Rows 的缓存；页没变就得手动刷
                Check(vm.PanelLayer == MainViewModel.PanelLayerFaction, "M7c 进「选单位」页 = 阵营层 " + faction);
                var factionPick = navigator.Rows.FirstOrDefault(row => row.Action == "findfaction" && (string?)row.Payload == faction);
                Check(factionPick is not null, "M7c 阵营必须在列表里: " + faction);
                if (factionPick is null) continue;
                vm.ActivatePanelRow(factionPick);
                var domainPicks = navigator.Rows.Where(row => row.Action == "finddomain").ToArray();
                Check(domainPicks.Length == 6, $"M7c {faction} 六个分类都列出来了: {domainPicks.Length}");
                // 第 2 层列表项文字 2026-10-07 统一为短名（与选分类提示语/结果行/第 3 层抬头同一份口径）。
                foreach (var dp in domainPicks)
                {
                    Check(dp.Label == UnitTaxonomy.ShortDomainLabel((UnitDomain)dp.Payload!),
                        $"M7c 第 2 层列表项是 ShortDomainLabel 短名: {dp.Label}");
                    // 加严：第 2 层列表项不得再出现「陆军 ·」/「空军 ·」/「海军 ·」军种前缀。
                    Check(!dp.Label.Contains("陆军 ·", StringComparison.Ordinal)
                            && !dp.Label.Contains("空军 ·", StringComparison.Ordinal)
                            && !dp.Label.Contains("海军 ·", StringComparison.Ordinal),
                        $"M7c 第 2 层列表项去掉了军种前缀: {dp.Label}");
                }
                // 长标签 DomainLabel 本身仍在、确实是带前缀的长文案（别被这一轮删掉或与短名混同）。
                foreach (var dp in domainPicks)
                {
                    var domain = (UnitDomain)dp.Payload!;
                    if (domain is UnitDomain.Infantry or UnitDomain.Vehicle or UnitDomain.Aircraft or UnitDomain.Ship)
                        Check(UnitTaxonomy.DomainLabel(domain) != UnitTaxonomy.ShortDomainLabel(domain)
                                && UnitTaxonomy.DomainLabel(domain).Contains(" · ", StringComparison.Ordinal),
                            $"M7c DomainLabel 仍是带前缀的长标签、与短名不同: {domain} => {UnitTaxonomy.DomainLabel(domain)}");
                }
                foreach (var domainPick in domainPicks)
                {
                    var domain = (UnitDomain)domainPick.Payload!;
                    vm.ActivatePanelRow(domainPick);
                    Check(vm.PanelLayer == MainViewModel.PanelLayerUnit && vm.PanelDomain == domain,
                        $"M7c 进单位层 {faction}×{domain}");
                    // 选分类提示语也必须是短名（三处同一份口径之一）+ 不得带军种前缀。
                    Check(vm.PanelResultText.Contains(UnitTaxonomy.ShortDomainLabel(domain)),
                        $"M7c 选分类提示语含短分类名: {vm.PanelResultText}");
                    Check(!vm.PanelResultText.Contains("陆军 ·", StringComparison.Ordinal)
                            && !vm.PanelResultText.Contains("空军 ·", StringComparison.Ordinal)
                            && !vm.PanelResultText.Contains("海军 ·", StringComparison.Ordinal),
                        $"M7c 选分类提示语去掉了军种前缀: {vm.PanelResultText}");
                    var head = navigator.Rows.First(row => row.Action == "find-head");
                    var expected = faction + " · " + shortByDomain[domain];
                    Check(head.Label == expected, $"M7c 抬头 {faction}×{domain} = {head.Label}（应 {expected}）");
                    Check(head.Label.Count(ch => ch == '·') == 1
                            && head.Label == faction + " · " + UnitTaxonomy.ShortDomainLabel(domain),
                        $"M7c 抬头恰好一个「·」且与 ShortDomainLabel 同源: {head.Label}");
                    Check(!head.Label.Contains("陆军", StringComparison.Ordinal)
                            && !head.Label.Contains("空军", StringComparison.Ordinal)
                            && !head.Label.Contains("海军", StringComparison.Ordinal)
                            && !head.Label.Contains("防御/生产", StringComparison.Ordinal),
                        $"M7c 抬头去掉了军种前缀与用途尾巴: {head.Label}");
                    pinned++;
                    vm.ActivatePanelRow(navigator.Rows.First(row => row.Action == "find-back"));   // 回分类层
                }
            }
            Check(pinned == 24, $"M7c 四阵营 × 六分类抬头全钉到（{pinned}/24）");
            // 翻屏/键位口径不受影响：单位层仍是 1..9/0 的翻屏键 + 返回。
            vm.SelectPanelPage("选单位"); vm.RefreshPanelRows();
            vm.ActivatePanelRow(navigator.Rows.First(row => row.Action == "findfaction"));
            vm.ActivatePanelRow(navigator.Rows.First(row => row.Action == "finddomain"));
            Check(navigator.Rows.Any(row => row.Action == "find-next") && navigator.Rows.Any(row => row.Action == "find-prev")
                    && navigator.Rows.Any(row => row.Action == "find-first") && navigator.Rows.Any(row => row.Action == "find-last"),
                "M7c 单位层翻屏键（+ - * /）仍在");
            vm.PanelMode = false;
            vm.RefreshPanelRows();
        }

        // ---- 2026-10-07 抓到的真 bug 的回归钉：找兵层里 8/2/5/0 必须和主层一模一样 ----
        // 修之前 ApplyPanelKey 的按键表是 `_panelLayer == PanelLayerMain ? command : None`：
        // 在阵营层按 Num2 不是"往下走"，而是按 KeyLabel 偷偷直达第 2 行并一路把单位选掉；
        // Num0 也不是返回，而是选中第 10 个单位；屏幕上一个编号都没印，用户根本看不出发生了什么。
        {
            var navigator = vm.Navigator;
            vm.PanelMode = true;
            vm.SelectPanelPage("选单位");
            vm.RefreshPanelRows();
            Check(vm.PanelLayer == MainViewModel.PanelLayerFaction, "M7b 进「选单位」页 = 阵营层");
            Check(navigator.Rows.Count(row => row.Action == "findfaction") >= 3,
                "M7b 阵营层列出多个阵营: " + navigator.Rows.Count(row => row.Action == "findfaction"));
            panel.DispatchPanelKey(PanelKeyMap.Numpad2);   // 没高亮时先落到第一行
            var at = navigator.SelectedIndex;
            Check(at >= 0 && navigator.Rows[at].Action == "findfaction", "M7b 阵营层有高亮行: " + at);
            panel.DispatchPanelKey(PanelKeyMap.Numpad2);
            Check(vm.PanelLayer == MainViewModel.PanelLayerFaction && navigator.SelectedIndex > at,
                "M7b 阵营层 Num2 = 高亮往下走一行（不是按编号直达）: " + at + " -> " + navigator.SelectedIndex);
            panel.DispatchPanelKey(PanelKeyMap.Numpad8);
            Check(vm.PanelLayer == MainViewModel.PanelLayerFaction && navigator.SelectedIndex == at,
                "M7b 阵营层 Num8 = 回到上一行: " + navigator.SelectedIndex);
            // 数字键不再有任何暗门：编号直达已拆，行上一个号都不印。
            foreach (var digit in new[] { PanelKeyMap.Numpad1, PanelKeyMap.Numpad3, PanelKeyMap.Numpad6 })
            {
                panel.DispatchPanelKey(digit);
                Check(vm.PanelLayer == MainViewModel.PanelLayerFaction && navigator.SelectedIndex == at,
                    "M7b Num" + (digit - PanelKeyMap.Numpad0) + " 在阵营层不直达任何行（编号直达已拆）");
            }

            // Num0 = 返回主层（不是"选中第 10 个"）
            panel.DispatchPanelKey(PanelKeyMap.Numpad0);
            Check(vm.PanelLayer == MainViewModel.PanelLayerMain, "M7b 阵营层 Num0 = 返回主层（不是编号直达）");

            // 选完兵必须回「增援」页：结果行/数量/星级/收藏槽都在那一页，留在「选单位」页什么都看不见。
            vm.SelectPanelPage("选单位");
            vm.RefreshPanelRows();
            var factionRow = navigator.Rows.First(row => row.Action == "findfaction");
            vm.ActivatePanelRow(factionRow);
            Check(vm.PanelLayer == MainViewModel.PanelLayerDomain, "M7b 选阵营进游戏分类层");
            var domainRow = navigator.Rows.First(row => row.Action == "finddomain");
            vm.ActivatePanelRow(domainRow);
            Check(vm.PanelLayer == MainViewModel.PanelLayerUnit, "M7b 选分类进单位层");
            var unitRow = navigator.Rows.First(row => row.Action == "findunit");
            vm.ActivatePanelRow(unitRow);
            Check(vm.PanelPage == "增援" && vm.PanelLayer == MainViewModel.PanelLayerMain,
                "M7b 选中一个兵后回「增援」页 + 主层（结果行看得见）: " + vm.PanelPage + "/" + vm.PanelLayer);
            Check(navigator.Rows.Any(row => row.Action == "send"), "M7b 回增援页后「呼叫增援」就在手边");
            Check(vm.PanelResultText.Contains("已选"), "M7b 结果行回报选了什么: " + vm.PanelResultText);

            vm.PanelMode = false;
            vm.RefreshPanelRows();
        }


        // ---- M8 单位按 mod 分层（用户 2026-10-07 第二问）：面板【选单位/找兵】只显「当前 mod + 原版通用」；
        //      换 mod 时另一 mod 的单位/阵营随之收敛；开「显示全部 mod 单位」放出并带来源标注；原版环境没有 mod 新阵营。----
        // 假数据造两个 mod：MODA 往盟军里加兵；MODB 自带新阵营「暗影」。加两条原版通用作对照。
        {
            var navigator = vm.Navigator;
            vm.PanelMode = true;
            var originalLauncher = vm.LauncherPath;

            static ReinforcementUnitEntry Mk(uint code, string name, string faction, string source) =>
                new(code, name, faction + " · 单位", source);

            var catalogue = new[]
            {
                Mk(0xA0000001, "原版警犬", "盟军", "通用"),      // 原版通用（步兵）
                Mk(0xA0000002, "原版铁锤", "苏联", "通用"),      // 原版通用
                Mk(0xB0000001, "模A坦克", "盟军", "MODA"),       // MODA 往盟军里加（车辆）
                Mk(0xC0000001, "暗影武士", "暗影", "MODB"),      // MODB 自带新阵营（步兵）
                Mk(0xC0000002, "暗影忍者", "暗影", "MODB"),      // MODB 自带新阵营（步兵）
            };

            void LoadCatalogue()
            {
                vm.ReinforcementUnits.Clear();
                foreach (var unit in catalogue) vm.ReinforcementUnits.Add(unit);
            }

            // 从「选单位」页把当前作用域里所有可选的单位代号收齐（走真实的阵营→分类→单位三层）。
            HashSet<uint> VisibleCodes()
            {
                var codes = new HashSet<uint>();
                vm.SelectPanelPage("选单位");
                vm.RefreshPanelRows();
                var factionRows = navigator.Rows.Where(r => r.Action == "findfaction").ToArray();
                foreach (var factionRow in factionRows)
                {
                    vm.ActivatePanelRow(factionRow);
                    var domainRows = navigator.Rows.Where(r => r.Action == "finddomain").ToArray();
                    foreach (var domainRow in domainRows)
                    {
                        vm.ActivatePanelRow(domainRow);
                        foreach (var unitRow in navigator.Rows.Where(r => r.Action == "findunit"))
                            codes.Add(((ReinforcementUnitEntry)unitRow.Payload!).Code);
                        vm.ActivatePanelRow(navigator.Rows.First(r => r.Action == "find-back"));
                    }
                    vm.ActivatePanelRow(navigator.Rows.First(r => r.Action == "find-back"));
                }
                return codes;
            }

            // 当前阵营层列出的阵营（原始名，逗号分隔）。
            string FactionsNow()
            {
                vm.SelectPanelPage("选单位");
                vm.RefreshPanelRows();
                return string.Join("/", navigator.Rows.Where(r => r.Action == "findfaction").Select(r => (string)r.Payload!));
            }

            // 走到「阵营×分类」下，取某条单位行的来源标注（State）。
            string RowBadgeFor(string faction, UnitDomain domain, uint code)
            {
                vm.SelectPanelPage("选单位");
                vm.RefreshPanelRows();
                vm.ActivatePanelRow(navigator.Rows.First(r => r.Action == "findfaction" && (string)r.Payload! == faction));
                vm.ActivatePanelRow(navigator.Rows.First(r => r.Action == "finddomain" && r.Payload is UnitDomain d && d == domain));
                var row = navigator.Rows.FirstOrDefault(r => r.Action == "findunit" && ((ReinforcementUnitEntry)r.Payload!).Code == code);
                return row?.State ?? "<no-row>";
            }

            // ① 当前 mod = MODA
            vm.LauncherPath = @"D:\mods\MODALauncher.exe";
            LoadCatalogue();
            var codesA = VisibleCodes();
            Check(codesA.Contains(0xB0000001u), "M8 当前 mod=MODA：MODA 往盟军里加的单位在可选列表里");
            Check(codesA.Contains(0xA0000001u) && codesA.Contains(0xA0000002u), "M8 当前 mod=MODA：原版通用单位也在");
            Check(!codesA.Contains(0xC0000001u) && !codesA.Contains(0xC0000002u),
                "M8 当前 mod=MODA：另一个 mod(MODB) 的单位**不出现在可选列表**");
            var factionsA = FactionsNow();
            Check(!factionsA.Contains("暗影"), "M8 当前 mod=MODA：另一个 mod 的新阵营(暗影)不出现: " + factionsA);
            Check(factionsA.Contains("盟军") && factionsA.Contains("苏联"), "M8 当前 mod=MODA：原版阵营仍在: " + factionsA);
            Check(RowBadgeFor("盟军", UnitDomain.Vehicle, 0xB0000001u) == "仅MODA",
                "M8 当前 mod=MODA：MODA 加进盟军的单位带来源标注「仅MODA」");

            // ② 切到 MODB：可选列表与阵营列表随之收敛到另一边
            vm.LauncherPath = @"D:\mods\MODBLauncher.exe";
            LoadCatalogue();
            var codesB = VisibleCodes();
            Check(codesB.Contains(0xC0000001u) && codesB.Contains(0xC0000002u), "M8 切到 MODB：MODB 的（新阵营）单位出现");
            Check(!codesB.Contains(0xB0000001u), "M8 切到 MODB：MODA 的单位不出现（mod 之间互不串）");
            var factionsB = FactionsNow();
            Check(factionsB.Contains("暗影"), "M8 切到 MODB：新阵营(暗影)自动进阵营表: " + factionsB);
            Check(factionsB.IndexOf("盟军", StringComparison.Ordinal) < factionsB.IndexOf("暗影", StringComparison.Ordinal),
                "M8 新阵营排在已知阵营之后: " + factionsB);

            // ③ 开「显示全部 mod 单位」：另一个 mod 的单位放出来，且一律带来源标注
            vm.SelectPanelPage("选单位");
            vm.RefreshPanelRows();
            vm.ActivatePanelRow(navigator.Rows.First(r => r.Action == "showallmods"));
            Check(vm.ShowAllModUnits, "M8 阵营层有「显示全部 mod 单位」开关，切换生效");
            var codesAll = VisibleCodes();
            Check(codesAll.Contains(0xB0000001u), "M8 开「显示全部」：另一个 mod(MODA) 的单位也出现了");
            Check(RowBadgeFor("盟军", UnitDomain.Vehicle, 0xB0000001u) == "仅MODA",
                "M8 开「显示全部」：其它 mod 的单位带来源标注「仅MODA」");
            Check(RowBadgeFor("暗影", UnitDomain.Infantry, 0xC0000001u) == "仅MODB",
                "M8 开「显示全部」：MODB 的单位带来源标注「仅MODB」");
            Check(RowBadgeFor("盟军", UnitDomain.Infantry, 0xA0000001u).Length == 0,
                "M8 原版通用单位没有来源标注（原版不打标）");

            // ④ 原版环境（直接开 RA3.exe）：mod 新阵营一个都不出现
            vm.ShowAllModUnits = false;
            vm.LauncherPath = @"D:\Games\Red Alert 3\RA3.exe";
            LoadCatalogue();
            var codesBase = VisibleCodes();
            Check(codesBase.Contains(0xA0000001u) && codesBase.Contains(0xA0000002u), "M8 原版环境：原版通用单位仍在");
            Check(!codesBase.Contains(0xB0000001u) && !codesBase.Contains(0xC0000001u) && !codesBase.Contains(0xC0000002u),
                "M8 原版环境(RA3.exe)：两个 mod 的单位都不出现");
            var factionsBase = FactionsNow();
            Check(!factionsBase.Contains("暗影"), "M8 原版环境：没有「暗影」这类 mod 阵营: " + factionsBase);

            // 还原现场，别影响后面的段落
            vm.ShowAllModUnits = false;
            vm.LauncherPath = originalLauncher;
            vm.PanelMode = false;
            vm.RefreshPanelRows();
        }


        // ---- M8b 「当前玩的是哪个 mod」的硬信号识别（用户 2026-10-07「可以搞」）----
        // 夹具注入假信号（假模块列表 / 假命令行），走真实 MainViewModel + 面板路径，钉：
        //   ① 硬信号压过启动器配置 ② 识别结果在主窗与游戏内面板看得见 ③ 三情形文案 ④ 认不出 = 未过滤，不丢单位。
        // 旧口径（只看配置里的启动器 exe 名）在本段的"启动器配置与硬信号相反"时必红。
        {
            var navigator = vm.Navigator;
            vm.PanelMode = true;
            var originalLauncher = vm.LauncherPath;
            var originalSignals = liveSignals;

            static ReinforcementUnitEntry MkMod(uint code, string name, string faction, string source) =>
                new(code, name, faction + " · 单位", source);

            var catalogue = new[]
            {
                MkMod(0xA0000001, "原版警犬", "盟军", "通用"),
                MkMod(0xB0000001, "日冕机甲", "盟军", "日冕"),
                MkMod(0xC0000001, "装甲突击兵", "盟军", "ARKMOD"),
            };

            void LoadModCatalogue()
            {
                vm.ReinforcementUnits.Clear();
                foreach (var unit in catalogue) vm.ReinforcementUnits.Add(unit);
            }

            HashSet<uint> VisibleModCodes()
            {
                var codes = new HashSet<uint>();
                vm.SelectPanelPage("选单位");
                vm.RefreshPanelRows();
                foreach (var factionRow in navigator.Rows.Where(r => r.Action == "findfaction").ToArray())
                {
                    vm.ActivatePanelRow(factionRow);
                    foreach (var domainRow in navigator.Rows.Where(r => r.Action == "finddomain").ToArray())
                    {
                        vm.ActivatePanelRow(domainRow);
                        foreach (var unitRow in navigator.Rows.Where(r => r.Action == "findunit"))
                            codes.Add(((ReinforcementUnitEntry)unitRow.Payload!).Code);
                        vm.ActivatePanelRow(navigator.Rows.First(r => r.Action == "find-back"));
                    }
                    vm.ActivatePanelRow(navigator.Rows.First(r => r.Action == "find-back"));
                }
                return codes;
            }

            // ① 启动器配置写着 ARMod，但游戏进程里挂的是**日冕**的模块 ⇒ 硬信号说了算：认出日冕。
            liveSignals = new ModSignalSnapshot(null, null, [@"D:\Games\CoronaLauncher\tools\EnhancerCorona.dll"]);
            vm.LauncherPath = @"D:\Games\RA3_WIC_3.01A\ARModLauncher.exe";
            LoadModCatalogue();
            Check(vm.CurrentMod.ModKey == "corona",
                "M8b 已加载模块压过启动器配置：认出日冕（不是配置里的 ARMod）: " + vm.CurrentMod.ModKey);
            Check(vm.CurrentMod.Signal == ModSignalKind.ProcessModules, "M8b 识别依据 = 游戏进程已加载模块");
            Check(vm.CurrentModText == "当前 mod：日冕", "M8b 日冕文案: " + vm.CurrentModText);

            vm.SelectPanelPage("选单位");
            vm.RefreshPanelRows();
            var modRow = navigator.Rows.First(r => r.Action == "find-mod");
            Check(modRow.Label == "当前 mod：日冕", "M8b 游戏内面板写清当前 mod: " + modRow.Label);
            Check(modRow.Detail == "识别依据：游戏进程已加载模块", "M8b 面板附识别依据: " + modRow.Detail);

            var codesCorona = VisibleModCodes();
            Check(codesCorona.Contains(0xB0000001u), "M8b 认出日冕：日冕的单位在可选列表里");
            Check(codesCorona.Contains(0xA0000001u), "M8b 认出日冕：原版通用单位仍在");
            Check(!codesCorona.Contains(0xC0000001u), "M8b 认出日冕：ARMod 的单位不出现（识别为 A 不显示 B 的单位）");

            // ② 反向：模块来自 ARMod 目录（同名 DLL EnhancerCorona.dll，只有按路径段才分得开）⇒ 认出 ARMod。
            liveSignals = new ModSignalSnapshot(null, null, [@"D:\Games\RA3_WIC_3.01A\tools\EnhancerCorona.dll"]);
            vm.LauncherPath = @"D:\Games\CoronaLauncher\CoronaLauncher.exe";
            // 换 mod 会重载分档清单（换 mod 不拿错代号），所以清单要在换完之后再摆进去。
            LoadModCatalogue();
            Check(vm.CurrentMod.ModKey == "armod",
                "M8b 反向：认出 ARMod（同名 DLL 按路径段区分，不认成日冕）: " + vm.CurrentMod.ModKey);
            var codesArmod = VisibleModCodes();
            Check(codesArmod.Contains(0xC0000001u) && !codesArmod.Contains(0xB0000001u),
                "M8b 认出 ARMod：ARMod 的兵可选、日冕的兵不出现");

            // ③ 命令行 -modConfig 最强：压过已加载模块，也压过启动器配置。
            liveSignals = new ModSignalSnapshot(null,
                "\"D:\\Games\\Red Alert 3\\Data\\RA3_1.12.game\" -modConfig D:\\Games\\CoronaLauncher\\CoronaData\\Corona\\corona_3.258.skudef",
                [@"D:\Games\RA3_WIC_3.01A\tools\EnhancerCorona.dll"]);
            vm.LauncherPath = @"D:\Games\RA3_WIC_3.01A\ARModLauncher.exe";
            LoadModCatalogue();
            Check(vm.CurrentMod.ModKey == "corona" && vm.CurrentMod.Signal == ModSignalKind.ProcessCommandLine,
                "M8b 命令行 -modConfig 压过已加载模块与启动器配置");

            // ④ 三情形文案之一：原版（游戏进程就是本体、命令行没有 -modConfig，配置里却写着日冕启动器）。
            liveSignals = new ModSignalSnapshot(null,
                "\"D:\\Games\\Red Alert 3\\Data\\RA3_1.12.game\" -xres 1920 -yres 1080 -config \"D:\\Games\\Red Alert 3\\RA3_chinese_t_1.12.SkuDef\"",
                []);
            vm.LauncherPath = @"D:\Games\CoronaLauncher\CoronaLauncher.exe";
            LoadModCatalogue();
            Check(vm.CurrentModText == "当前 mod：原版", "M8b 原版文案: " + vm.CurrentModText);
            var codesBase = VisibleModCodes();
            Check(codesBase.Contains(0xA0000001u) && !codesBase.Contains(0xB0000001u) && !codesBase.Contains(0xC0000001u),
                "M8b 原版：两个 mod 的单位都不出现，原版通用仍在");

            // ⑤ 三情形文案之三：全缺 ⇒ 未知（未过滤，避免藏掉你的单位），一个单位都不丢。
            // 注意：LauncherPath 置空时 VM 会自动预选候选里的第一个（免得下拉框空白）——
            // 要真的模拟"什么都没配"，得先把候选也清空，测完再放回去。
            var savedCandidates = vm.LauncherCandidates.ToArray();
            vm.LauncherCandidates.Clear();
            liveSignals = ModSignalSnapshot.Empty;
            vm.LauncherPath = string.Empty;
            Check(vm.LauncherPath.Length == 0 && vm.CurrentMod.ModKey == "default" && !vm.CurrentMod.IsKnown,
                "M8b 什么都没配/没信号 ⇒ default（不是原版、不是某个 mod）: " + vm.LauncherPath + " / " + vm.CurrentMod.ModKey);
            LoadModCatalogue();
            Check(vm.CurrentModText.Contains("未过滤", StringComparison.Ordinal) &&
                  vm.CurrentModText.Contains("避免藏掉你的单位", StringComparison.Ordinal),
                "M8b 未知文案写明未过滤: " + vm.CurrentModText);
            var codesUnknown = VisibleModCodes();
            Check(codesUnknown.IsSupersetOf(new uint[] { 0xA0000001u, 0xB0000001u, 0xC0000001u }),
                "M8b 认不出（default）：不过滤，三个单位全在");

            // 还原现场，别影响后面的段落
            foreach (var candidate in savedCandidates) vm.LauncherCandidates.Add(candidate);
            liveSignals = originalSignals;
            vm.LauncherPath = originalLauncher;
            vm.ShowAllModUnits = false;
            vm.PanelMode = false;
            vm.RefreshPanelRows();
        }


        // ---- 主窗 840x530：默认「启动」页 + 四页都不留空白 ----
        {
            var main = new MainWindow(vm);
            Check(!main.IsVisible && new WindowInteropHelper(main).Handle == IntPtr.Zero, "main window fixture never shown or allocated HWND");
            Check(main.Navigation.Selected == "启动", "main window defaults to the 启动 page");
            var content = (System.Windows.FrameworkElement)main.Content;
            void Layout()
            {
                content.Measure(new Size(840, 530));
                content.Arrange(new Rect(0, 0, 840, 530));
                content.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }
            var pages = new[] { ("启动", "StartPage"), ("增援", "ReinforcementPage"), ("功能", "FeaturesPage"), ("诊断", "DiagnosticsPage") };
            var shortPages = new System.Collections.Generic.List<string>();
            foreach (var (section, pageName) in pages)
            {
                main.Navigation.Selected = section;
                Layout();
                Check(main.FindName("Nav" + section) is System.Windows.Controls.Button, "main window has a nav button for " + section);
                foreach (var (otherSection, otherName) in pages)
                    Check(((System.Windows.FrameworkElement)main.FindName(otherName)!).Visibility == (otherName == pageName ? Visibility.Visible : Visibility.Collapsed), section + " shows only its own page (" + otherSection + ")");
                var scroll = (System.Windows.Controls.ScrollViewer)main.FindName(pageName)!;
                Console.WriteLine($"LAYOUT {section}: extent={scroll.ExtentHeight:F1} viewport={scroll.ViewportHeight:F1} 视口差={scroll.ViewportHeight - scroll.ExtentHeight:F1}");
                if (scroll.ExtentHeight < scroll.ViewportHeight - 2)
                    shortPages.Add(section + ": extent=" + scroll.ExtentHeight + " viewport=" + scroll.ViewportHeight);
                { var b = new RenderTargetBitmap(840, 530, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                  b.Render(content); var e = new PngBitmapEncoder(); e.Frames.Add(BitmapFrame.Create(b));
                  using var f = File.Create(Path.Combine(output, "main-" + pageName + ".png")); e.Save(f); }
            }
            Check(shortPages.Count == 0, "every main page fills the window, no blank lower half: " + string.Join("; ", shortPages));
            Check(main.FindName("MaintenancePage") is null, "the old 维护 page is gone from the main window");

            // ---- 心灵控制（写入）：高风险闸门 + 写入/还原入口都在「诊断」页，且默认不可点 ----
            {
                main.Navigation.Selected = "诊断";
                Layout();
                var diag = (System.Windows.Controls.ScrollViewer)main.FindName("DiagnosticsPage")!;
                var diagButtons = Descendants(diag).OfType<System.Windows.Controls.Button>().ToArray();
                var applyButton = diagButtons.FirstOrDefault(b => (b.Content as string) == "心灵控制（当前选中单位）");
                var restoreButton = diagButtons.FirstOrDefault(b => (b.Content as string) == "解除心灵控制");
                Check(applyButton is not null, "诊断页有「心灵控制（当前选中单位）」按钮");
                Check(restoreButton is not null, "诊断页有「解除心灵控制」按钮");
                // 多选心灵控制（路线 C）已按用户 2026-10-07 定论撤掉（游戏里框选只装自己的单位 ⇒ 拿不到多选输入）：
                // 入口、命令、清单文本都不该再存在；只留「心灵控制（一次一个）+ 解除心灵控制」。
                Check(diagButtons.All(b => (b.Content as string) != "列出全部选中（只读）"), "诊断页不再有「列出全部选中（只读）」按钮");
                Check(diagButtons.All(b => (b.Content as string) != "心灵控制全部选中"), "诊断页不再有「心灵控制全部选中」按钮");
                Check(vm.GetType().GetProperty("ReadAllSelectedUnitsCommand") is null, "多选命令 ReadAllSelectedUnitsCommand 已删除");
                Check(vm.GetType().GetProperty("ApplyOwnershipToAllSelectedCommand") is null, "多选命令 ApplyOwnershipToAllSelectedCommand 已删除");
                Check(vm.GetType().GetProperty("OwnershipRosterText") is null, "多选清单文本 OwnershipRosterText 已删除");
                // 心灵控制那一段在诊断页的下半截（视口外）⇒ 滚到底再渲一张，供人眼复核（我看，不交给他）。
                // 仪器：渲染前后各取一次前台/焦点 —— 红线是不许抢他游戏的前台，谁动的一眼看得出来。
                var fgBefore = GetForegroundWindow(); var focusBefore = GetFocus();
                diag.ScrollToEnd();
                Layout();
                { var b = new RenderTargetBitmap(840, 530, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                  b.Render(content); var e = new PngBitmapEncoder(); e.Frames.Add(BitmapFrame.Create(b));
                  using var f = File.Create(Path.Combine(output, "main-DiagnosticsPage-duoshe.png")); e.Save(f); }
                diag.ScrollToTop();
                Layout();
                Console.WriteLine($"CHROME 心灵控制段渲染前后：foreground {fgBefore}->{GetForegroundWindow()} focus {focusBefore}->{GetFocus()}");
                // 夹具从未附加游戏 ⇒ 两条命令都不可点（心灵控制默认关闭、且没有可还原快照）。
                Check(!vm.ApplyOwnershipTransferCommand.CanExecute(null), "心灵控制按钮默认不可点（未安装 patch/未连接）");
                Check(!vm.RestoreOwnershipCommand.CanExecute(null), "解除心灵控制按钮默认不可点（没有快照）");
                Check(!restoreButton!.IsEnabled, "解除心灵控制按钮默认灰置");
                Check(applyButton!.IsEnabled == vm.ApplyOwnershipTransferCommand.CanExecute(null),
                    "心灵控制按钮可用性由命令决定: " + applyButton.IsEnabled);
                // 高风险闸门就在同一页，且绑到 vm.AllowUnverifiedWorldPositionFeatures。
                var gate = Descendants(diag).OfType<System.Windows.Controls.CheckBox>()
                    .FirstOrDefault(cb => System.Windows.Data.BindingOperations.GetBinding(
                        cb, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.Path.Path == "AllowUnverifiedWorldPositionFeatures");
                Check(gate is not null, "诊断页有「高风险功能」勾选框，绑到 AllowUnverifiedWorldPositionFeatures");
                Check(!vm.AllowUnverifiedWorldPositionFeatures, "高风险功能默认关闭（心灵控制因此默认不可用）");
                Check(vm.OwnershipTransferText.Length > 0 && vm.OwnershipTransferText.Contains("默认关闭", StringComparison.Ordinal),
                    "心灵控制状态文本有初始说明: " + vm.OwnershipTransferText);
                // 闸门确实控制这条功能（心灵控制按钮走的就是 FeatureSafetyPolicy.Validate）。
                bool Throws(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
                var changeId = vm.Groups.SelectMany(g => g.Features).Single(i => i.Feature.RawName == "Select Unit Change ID").Feature;
                Check(!string.IsNullOrEmpty(changeId.ValueHint) && changeId.ValueHint == "0x09" && changeId.DispatchTarget == "MustCode2+800",
                    "心灵控制走的是引擎 0x09 动作 / MustCode2+800 块: " + changeId.ValueHint + " -> " + changeId.DispatchTarget);
                Check(Throws(() => FeatureSafetyPolicy.Validate(changeId, true, false)),
                    "闸门关闭时「心灵控制」被策略拒绝（暂受限制）");
                var gateOnAllowed = true;
                try { FeatureSafetyPolicy.Validate(changeId, true, true); } catch (InvalidOperationException) { gateOnAllowed = false; }
                Check(gateOnAllowed, "闸门开启后放行（仍会做对象链校验）");
            }
            main.Navigation.Selected = "启动";
            Layout();

            // 主窗「增援」页的选择单位入口必须仍然可用（不是只剩下"用游戏里选中的"）
            main.Navigation.Selected = "增援";
            Layout();
            // 主窗也要看得见「当前 mod」（用户 2026-10-07：识别要看得见）——文案跟 vm 的识别结果一致。
            var modLine = main.FindName("CurrentModLine") as System.Windows.Controls.TextBlock;
            Check(modLine is not null && modLine.Text == vm.CurrentModText,
                "主窗「增援」页显示当前 mod: " + (modLine?.Text ?? "<找不到 CurrentModLine>"));
            var reinforcementPage = (System.Windows.Controls.ScrollViewer)main.FindName("ReinforcementPage")!;
            var chooseButton = Descendants(reinforcementPage).OfType<System.Windows.Controls.Button>()
                .FirstOrDefault(button => (button.Content as string) == "选择单位");
            Check(chooseButton is not null, "main window 增援 page still offers 选择单位");
            var editor = main.ReinforcementEditor;
            InvokeButton(chooseButton!); Layout();
            // IsChoosing 那个"展开/收起"死状态已删（选兵区常驻）⇒ 改成断言按钮的真实作用：
            // 把左列跳到当前兵种所在的分类。
            Check(editor.SelectedCategory == vm.SelectedReinforcementUnit?.Category,
                "点「选择单位」把左列跳到当前兵种的分类: " + editor.SelectedCategory);
            { var b = new RenderTargetBitmap(840, 530, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
              b.Render(content); var e = new PngBitmapEncoder(); e.Frames.Add(BitmapFrame.Create(b));
              using var f = File.Create(Path.Combine(output, "main-ReinforcementPage-choosing.png")); e.Save(f); }
            var unitList = Descendants(reinforcementPage).OfType<System.Windows.Controls.ListBox>()
                .FirstOrDefault(list => list.Name == "UnitSelector");
            Check(unitList is not null, "the 增援 page still has its unit list");

            // ★ 用户 2026-10-07：「分类左边固定住，没必要点一下单位就消失」★
            // 分类列是常驻列：进页就在、点了分类还在、点了兵种还在。以前是"二选一"开关，
            // 点一下分类它就整块消失（选了分类反而看不到分类），这三条断言把"常驻"钉死。
            // 这个窗口是离屏的（永不 Show）⇒ IsVisible 永远是 false，沿父链自己算"真实可见"。
            bool Rendered(System.Windows.DependencyObject node)
            {
                for (var n = node; n is not null && !ReferenceEquals(n, reinforcementPage); n = System.Windows.Media.VisualTreeHelper.GetParent(n))
                {
                    if (n is System.Windows.UIElement ui && ui.Visibility != System.Windows.Visibility.Visible) return false;
                }

                return true;
            }

            var categories = Descendants(reinforcementPage).OfType<System.Windows.Controls.ListBox>()
                .FirstOrDefault(list => list.Name == "CategorySelector");
            Check(categories is not null, "增援页有分类列（常驻，不是按需出现）");
            Check(Rendered(categories!) && categories!.ActualHeight > 40,
                "一进页分类列就画出来且可点: rendered=" + Rendered(categories) + " h=" + categories.ActualHeight.ToString("F1"));
            var categoryButton = Descendants(categories).OfType<System.Windows.Controls.Button>().First();
            InvokeButton(categoryButton); Layout();
            Check(Rendered(categories) && categories.ActualHeight > 40,
                "点了分类之后分类列仍然在（不许消失）: rendered=" + Rendered(categories) + " h=" + categories.ActualHeight.ToString("F1"));
            Check(editor.SelectedCategory is not null && editor.HasCategory,
                "点了分类右列就列出该分类的兵种: " + editor.SelectedCategory);
            { var b = new RenderTargetBitmap(840, 530, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
              b.Render(content); var e = new PngBitmapEncoder(); e.Frames.Add(BitmapFrame.Create(b));
              using var f = File.Create(Path.Combine(output, "main-ReinforcementPage-category-chosen.png")); e.Save(f); }

            Check(unitList!.Items.Count > 0, "the unit list lists entries: " + unitList.Items.Count);
            var beforeUnit = vm.SelectedReinforcementUnit;
            var otherUnitButton = Descendants(unitList).OfType<System.Windows.Controls.Button>()
                .FirstOrDefault(button => (button.DataContext as ReinforcementUnitEntry)?.Code != beforeUnit?.Code);
            Check(otherUnitButton is not null, "the list offers a unit other than the current one");
            InvokeButton(otherUnitButton!); Layout();
            Check(vm.SelectedReinforcementUnit is not null && vm.SelectedReinforcementUnit.Code != beforeUnit?.Code,
                "clicking a unit in the main window really changes the selected 兵种");
            var currentUnitName = Descendants(reinforcementPage).OfType<System.Windows.Controls.TextBlock>()
                .First(block => block.Name == "CurrentUnitName");
            Check(currentUnitName.Text == vm.SelectedReinforcementUnit?.Name,
                "选中的兵种名字当场反映在「兵种」行（选兵区不收，反馈靠这一行）: " + currentUnitName.Text);
            Check(Rendered(categories) && categories.ActualHeight > 40 && Rendered(unitList),
                "选中一个兵种后选兵区与分类列都还在（点一下不该让分类消失）: rendered=" + Rendered(categories)
                + " h=" + categories.ActualHeight.ToString("F1") + " 单位列表也在=" + Rendered(unitList));
            main.Navigation.Selected = "启动";
            Layout();
            var bmp = new RenderTargetBitmap(840, 530, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(content);
            var frame = new byte[840 * 530 * 4];
            bmp.CopyPixels(frame, 840 * 4, 0);
            Check(frame.Distinct().Count() > 40, "main window start page renders real text/control pixels");
            { var e = new PngBitmapEncoder(); e.Frames.Add(BitmapFrame.Create(bmp)); using var f = File.Create(Path.Combine(output, "main-start.png")); e.Save(f); }
        }
        using var process=Process.GetCurrentProcess();long created=process.StartTime.ToFileTimeUtc();
        var slot = new OverlayPanelSlot(process.Id, created);
        try
        {
            Check(slot.Publish(reinforcement!,360,440,24,24),"first frame marks dirty");
            uint generation=slot.Generation;
            Check(!slot.Publish(reinforcement!,360,440,24,24) && slot.Generation==generation,"stationary pixels/position heartbeat leaves dirty generation unchanged");
            Check(slot.Publish(reinforcement!,360,440,42,24) && slot.Generation==generation+1,"position change marks dirty");
            Check(slot.Publish(changed,360,440,42,24),"numeric content change marks dirty");
            slot.Publish(reinforcement!,360,440,24,24);
            using var mapping=MemoryMappedFile.OpenExisting(OverlayPanelSlot.Name(process.Id,created),MemoryMappedFileRights.Read);
            using var view=mapping.CreateViewAccessor(0,OverlayPanelSlot.Size,MemoryMappedFileAccess.Read);
            var frame=new byte[OverlayPanelSlot.Size];view.ReadArray(0,frame,0,frame.Length);
            Check(BitConverter.ToUInt32(frame,8)==1 && BitConverter.ToUInt32(frame,12)%2==0,"stable versioned ABI snapshot");
            Check(BitConverter.ToUInt32(frame,76)==OverlayPanelSlot.Checksum(reinforcement!),"pixel checksum self-check");
            // reserved[0] is the M2a panel mode (0/1); the rest of the M2/M3 reserve must stay zero.

            Check(BitConverter.ToUInt32(frame,88)<=1 && frame.AsSpan(92,36).ToArray().All(b=>b==0) && frame.AsSpan(128+OverlayPanelSlot.PixelCapacity).ToArray().All(b=>b==0),"M2/M3 ACK/ring reserved zero except the M2a mode word");

            slot.PanelMode=true;slot.Publish(reinforcement!,360,440,24,24);

            view.ReadArray(0,frame,0,frame.Length);

            Check(BitConverter.ToUInt32(frame,88)==1,"panel mode round-trips to the native header");

            slot.PanelMode=false;slot.Publish(reinforcement!,360,440,24,24);

            view.ReadArray(0,frame,0,frame.Length);

            Check(BitConverter.ToUInt32(frame,88)==0,"panel mode off is written back as zero");
            File.WriteAllBytes(Path.Combine(output,"panel-frame.bin"),frame);
            slot.Disable();Check(view.ReadUInt32(28)==0,"stop visible through read-only native view");
            bool rejected=false;try{slot.Publish([],513,440,0,0);}catch(ArgumentOutOfRangeException){rejected=true;}
            Check(rejected,"host rejects out of bounds pixel frame");
        }
        finally {slot.Dispose();}
        bool absent=false;try{using var gone=MemoryMappedFile.OpenExisting(OverlayPanelSlot.Name(process.Id,created));}catch(FileNotFoundException){absent=true;}
        Check(absent,"mapping disappears after all handles close, no mapping file");
        // Exercise actual optional orchestration without deployment or game attachment.
        using var failurePanel = new OffscreenPanel(vm);
        using var failureSlot = new OverlayPanelSlot(process.Id, created);
        var overlay = new ProductOverlay((kind, _) => { if(kind=="osd-panel-published") throw new IOException("synthetic sink unavailable"); });
        void Set(string name, object? value) => typeof(ProductOverlay).GetField(name,System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.SetValue(overlay,value);
        Set("_panel",failurePanel);Set("_panelSlot",failureSlot);
        // 面板只有在面板模式下才会真的绘制/发布；不打开它，这段就测不到发布路径。
        vm.PanelMode = true;
        overlay.Publish(true,true,0,"fixture");
        Check(failureSlot.Generation==1 && !(bool)typeof(ProductOverlay).GetField("_panelFailed",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(overlay)!,"published-log sink exception cannot disable panel");
        string nativeLog=Path.Combine(Path.GetTempPath(),$"Ra3Overlay-{process.Id}.log");
        File.WriteAllText(nativeLog,$"osd-session {created:X16}\nosd-installed\nosd-panel-drawn\n");
        Set("_feedbackPid",process.Id);Set("_feedbackCreation",created);
        failurePanel.Dispose();overlay.Publish(true,true,0,"fixture");
        Check(overlay.Status.Contains("不可用"),"host render fault remains visible despite historical successful native feedback");
        using(var read=MemoryMappedFile.OpenExisting(OverlayPanelSlot.Name(process.Id,created),MemoryMappedFileRights.Read))
        using(var view=read.CreateViewAccessor(0,OverlayPanelSlot.Size,MemoryMappedFileAccess.Read))
            Check(view.ReadUInt32(28)==0,"host render failure publishes disabled before reporting");
        typeof(ProductOverlay).GetMethod("ReleaseLease",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(overlay,null);
        File.Delete(nativeLog);
        // ---- M3: a click on the panel really runs that control's command ----
        // Uses the still-alive panel (the fixture disposes it further down).
        {
            vm.OverlayPanelSection = "增援";
            // 面板模式下页签栏是隐藏的（游戏内只画正文），所以"点面板上的控件真的会跑它的命令"
            // 这一条要在窗口模式下点页签栏按钮来验；面板模式下标题条的桌面按钮另有断言。
            vm.PanelMode = false;
            panel.Render(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var content = panel.Root;
            // 只挑**面板行**里的按钮：标题条上那几个是桌面动作（面板模式下已被隐藏），
            // 拿它们当"面板控件"来测就不是在测面板了。
            // 夹具的窗口是离屏的（Opacity=0、Left=-30000），所以 IsVisible 永远是 false；
            // 这里沿父链自己算"真实可见"（父级 Collapsed 也算不可见）。
            bool VisiblyRendered(DependencyObject node)
            {
                for (var n = node; n is not null && n != content; n = System.Windows.Media.VisualTreeHelper.GetParent(n))
                {
                    if (n is System.Windows.UIElement ui && ui.Visibility != System.Windows.Visibility.Visible) return false;
                }

                return true;
            }

            var chromeLabels = new[] { "收起", "展开", "✕", "收成细条" };
            var candidates = Descendants(content).OfType<System.Windows.Controls.Button>()
                .Where(b => !(b.Content as string is string label && chromeLabels.Contains(label)))
                .ToArray();
            var button = candidates
                .FirstOrDefault(b => b.IsEnabled && b.ActualWidth > 4 && b.ActualHeight > 4 && VisiblyRendered(b) && b.IsHitTestVisible && !InsideToggle(b, content));
            Check(button is not null, "M3 fixture located a real enabled button in the panel; 候选=" +
                string.Join(" | ", candidates.Select(b => $"{b.Content}:vis={b.IsVisible}:en={b.IsEnabled}:{b.ActualWidth}x{b.ActualHeight}")));
            var origin = button!.TransformToAncestor(content).Transform(new System.Windows.Point(0, 0));
            int cx = (int)(origin.X + button.ActualWidth / 2), cy = (int)(origin.Y + button.ActualHeight / 2);
            // The press arrives in game pixels; the panel sits at 24,24. Map first, exactly
            // like the product does, then hit test the mapped point.
            Check(PanelClickMap.TryMap(cx + 24, cy + 24, 24, 24, OffscreenPanel.Width, OffscreenPanel.Height, out var lx, out var ly)
                  && lx == cx && ly == cy, "M3 a game-pixel press maps onto the panel one to one");
            int clicked = 0;
            button.Click += (_, _) => clicked++;
            Check(panel.TryInvokeAt(lx, ly, out var hit) && hit.Length > 0, "M3 the press lands on the button and names it");
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Check(clicked == 1, "M3 the button's own click handler ran, exactly once");
            // 桌面浮窗已取消（2026-10-06 用户：「桌面我浮窗有什么用」）：
            // 标题条上的桌面按钮（收成细条/展开/✕）现在**根本不存在** —— 比"隐藏"更强，
            // 游戏里没有任何东西可以点到。本条从"必须不可见"加严为"必须不存在"。
            vm.PanelMode = true;
            panel.Render(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var titleButtons = Descendants(content).OfType<System.Windows.Controls.Button>()
                .Where(b => (b.Content as string) is "收起" or "展开" or "✕" or "收成细条").ToArray();
            Check(titleButtons.Length == 0,
                "M3 标题条上没有任何桌面浮窗按钮（收成细条/展开/✕ 已随桌面浮窗一起删除）");

            // 桌面形态概念（发丝状态线 / 边缘热区 / 迷你条）已整体删除：
            // 面板是该窗口唯一形态 ⇒ 无论怎么切面板模式，正文都必须可见，
            // 不可能再退化成 88px 空壳（那正是当初用户按 Ins 只看到一行文字的原因）。
            vm.PanelMode = false;
            vm.PanelMode = true;
            panel.Render(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var board = Descendants(content).OfType<System.Windows.FrameworkElement>()
                .FirstOrDefault(e => e.Name == "PanelBoardScroll");
            Check(board is not null && VisiblyRendered(board),
                "M3 面板模式下正文可见（桌面形态已删，不可能再变 88px 空壳）");
            // A press goes to the nearest ButtonBase, which is the documented contract: a
            // Button living inside a ToggleButton's template correctly resolves to the
            // ToggleButton that covers it (observed directly through the hit-test readout).
            // So no assertion is made here about which control a second press picks.
            Check(panel.TryInvokeAt(-5, 5, out _) == false && panel.TryInvokeAt(5, -5, out _) == false
                  && panel.TryInvokeAt(OffscreenPanel.Width, 5, out _) == false,
                  "M3 presses outside the panel bounds are ignored");
            Check(clicked == 1, "M3 ignored presses never reached a control");
        }

        // ---- 2026-10-07：心灵控制进面板（不 alt-tab 的那个入口）----
        // 放在**最后**：它会写面板结果行，而上面的位图基线断言要求"面板模式切来切去像素回到原样" ——
        // 之前把它放在功能页那段里，当场把 "panel mode off restores the same bitmap" 撞红，所以测试
        // 自己动的状态必须排在这些位图断言之后。
        {
            vm.PanelMode = false;
            vm.SelectPanelPage("功能");
            vm.RefreshPanelRows();
            var indexed = vm.PanelRows.Select((row, i) => (row, i)).ToArray();
            var lastFeatIndex = indexed.Where(t => t.row.Action.StartsWith("feat:", StringComparison.Ordinal)).Max(t => t.i);
            // 契约一：心灵控制那一族**按固定顺序**追加在功能行之后，既有行索引一个不动。
            // 页首插行会让"往下走到钱那一行"整体位移，这个夹具当场抓到过一次 ⇒ 位置本身是要钉的。
            // 2026-10-07：多选两行已撤（用户实测框选装不进别人的单位），只剩这两行 —— 顺序/位置仍是要钉的契约。
            string[] duosheOrder = ["duoshe:apply", "duoshe:restore"];
            var duosheIndexes = duosheOrder.Select(action => indexed.First(t => t.row.Action == action).i).ToArray();
            Check(duosheIndexes.SequenceEqual(Enumerable.Range(lastFeatIndex + 1, duosheOrder.Length)),
                $"心灵控制两行按固定顺序追加在功能行之后，既有行索引一个不动 (indexes={string.Join(",", duosheIndexes)} lastFeat={lastFeatIndex})");
            Check(vm.PanelRows.All(row => row.Action != "duoshe:roster" && row.Action != "duoshe:applyall"),
                "面板里不再有「列出全部选中」「心灵控制全部选中」两行（多选已撤）");
            // StatusRow 的第三个参数是 State（不是 Detail）—— 页头那格写在 State 上。
            Check(vm.PanelRows.First(row => row.Action == "page-head").State.Contains("Ctrl+D", StringComparison.Ordinal),
                "功能页页头写出心灵控制的键盘快捷键: " + vm.PanelRows.First(row => row.Action == "page-head").State);
            Check(vm.PanelRows.Count(row => row.Kind == PanelRowKind.Position) == 1, "加了心灵控制行后面板位置行仍只有一行");
            var applyRow = vm.PanelRows.First(row => row.Action == "duoshe:apply");
            Check(applyRow.Kind == PanelRowKind.Action, "心灵控制行是执行行（Num5 与点击同一入口）");
            Check(applyRow.State.Length > 0, "心灵控制行把「为什么现在按不了」写在状态列上: " + applyRow.State);
            // 契约二：按了必须**有反应**。这里是没连游戏的环境 ⇒ 必须明说不可用，不允许静默。
            vm.ActivatePanelRow(applyRow);
            Check(vm.PanelResultText.Contains("心灵控制") && vm.PanelResultText.Contains("不可用"),
                "没连游戏时按心灵控制：面板结果行明说不可用，不静默: " + vm.PanelResultText);
            var restoreRow = vm.PanelRows.First(row => row.Action == "duoshe:restore");
            Check(restoreRow.Kind == PanelRowKind.Action, "解除心灵控制行是执行行");
            vm.ActivatePanelRow(restoreRow);
            Check(vm.PanelResultText.Contains("还原") && vm.PanelResultText.Contains("不可用"),
                "没连游戏时按解除心灵控制：面板结果行明说不可用，不静默: " + vm.PanelResultText);
            // ★ Num1 = 心灵控制直通（用户 2026-10-07：面板按一个键就完成心灵控制）★
            //    要在**任何页任何层**都认、不先走到那一行、也不动层与光标；按了必须有反应。
            // 按键派发要求面板模式开着（这一块之前把它关掉了），验完恢复。
            vm.PanelMode = true;
            vm.SelectPanelPage("功能");
            vm.RefreshPanelRows();
            var layerBefore = vm.PanelLayer;
            var indexBefore = vm.Navigator.SelectedIndex;
            panel.DispatchPanelKey(PanelKeyMap.Numpad1);
            Check(vm.PanelResultText.Contains("心灵控制") && vm.PanelResultText.Contains("Num1"),
                "Num1 直通：没连游戏时明说不可用（带 Num1 字样），不静默: " + vm.PanelResultText);
            Check(vm.PanelLayer == layerBefore && vm.Navigator.SelectedIndex == indexBefore,
                "Num1 直通不动层与光标（不用先走到那一行）");
            vm.SelectPanelPage("选单位");
            vm.RefreshPanelRows();
            panel.DispatchPanelKey(PanelKeyMap.Numpad1);
            Check(vm.PanelResultText.Contains("心灵控制不可用") && vm.PanelResultText.Contains("Num1"),
                "Num1 在「选单位」页同样触发心灵控制（跨页直通）: " + vm.PanelResultText);
            vm.SelectPanelPage("功能");
            vm.RefreshPanelRows();
            vm.PanelMode = false;
        }
        panel.Dispose();
        Check(foreground==GetForegroundWindow() && focus==GetFocus() && activated==0,$"foreground/focus unchanged, Activated=0 (fg {foreground}->{GetForegroundWindow()}, focus {focus}->{GetFocus()}, activated={activated})");
        Check(memory.Calls==0,"fixture never reads/writes game memory or dispatches input");
File.WriteAllText(Path.Combine(output,"README.txt"),"这是离屏夹具，不是真机像素。复用产品 FloatingWindow / ViewModel，未创建 HWND、未 Show、未注入输入。360×440 Pbgra32。panel-frame.bin 是完整 C# ABI 导出，供 native PanelTests 跨语言验纹理与四边形。\n");
        Console.WriteLine($"RESULT WPF-panel checks={_checks} failures=0 skipped=0");return 0;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
}
