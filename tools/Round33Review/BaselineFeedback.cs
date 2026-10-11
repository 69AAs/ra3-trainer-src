namespace Round33Baseline;

/// <summary>Diagnostic display only; never authorizes installation or memory writes.</summary>
public static class OverlayFeedback
{
    public const string Guidance = "关闭后必须重启游戏，再点一键启动。以下为排查候选，不代表已确认占槽：\n"
        + "• NVIDIA App → 设置 → 功能 → 关闭「高光 / Highlights」及「NVIDIA 覆盖」。若版本中高光位于覆盖设置，请在那里关闭。仅关闭即时重放或桌面捕捉不够。\n"
        + "这些是全局开关，与 RA3 是否在游戏列表无关。若关闭后仍注入，退出 NVIDIA App 并重启电脑，再核对占槽模块。\n"
        + "• Discord → 用户设置 → 游戏覆盖 → 关闭游戏内覆盖。\n"
        + "• Steam → 设置 → 游戏中 → 关闭游戏内 Steam 界面。\n"
        + "• Epic / EOS：完全退出 Epic 启动器；若游戏必须由它启动且没有覆盖开关，请向该游戏支持方确认禁用方式。不要删改 EOS DLL。\n"
        + "• 若模块属于日冕 / mod，请在对应启动器关闭覆盖或捕获功能；没有开关时提交模块名和日志给作者。不要删除 mod DLL。";

    public static string? Parse(string log, long creationTime)
    {
        var marker = $"osd-session {creationTime:X16}";
        var lines = log.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var start = Array.FindLastIndex(lines, l => l == marker);
        if (start < 0) return null; // PID reuse or an old DLL cannot claim current evidence.
        var entries = new Dictionary<string, string>();
        string? status = null;
        bool refused = false;
        // The producer appends the line ending separately; ignore an incomplete final record.
        for (var i = start + 1; i < lines.Length - 1; i++)
        {
            var line = lines[i];
            if (line.StartsWith("osd-session ", StringComparison.Ordinal)) break;
            if (line.StartsWith("osd-entry slot=", StringComparison.Ordinal))
            {
                var slot = line.Contains("slot=Present ", StringComparison.Ordinal) ? "Present" :
                    line.Contains("slot=Reset ", StringComparison.Ordinal) ? "Reset" : null;
                var split = line.IndexOf(" module=", StringComparison.Ordinal);
                if (slot is null || split < 0) continue;
                var module = line[(split + 8)..];
                if (module.Length is 0 or > 260 || module.Any(char.IsControl)) module = "unidentified";
                var reason = line.Contains("reason=foreign-module ", StringComparison.Ordinal) ? "第三方捕获/覆盖层或 mod 占槽" :
                    line.Contains("reason=verified-system ", StringComparison.Ordinal) ? "系统入口校验通过" :
                    line.Contains("reason=system-entry-unverified ", StringComparison.Ordinal) ? "系统入口未通过磁盘校验，修改者未识别" : "占槽者未识别";
                string Field(string key)
                {
                    var prefix = key + "=";
                    var value = line.Split(' ').FirstOrDefault(p => p.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
                    return value is { Length: > 0 and <= 260 } && !value.Any(char.IsControl) ? value : "unidentified";
                }
                if (line.Contains("reason=entry-transfer-unverified ", StringComparison.Ordinal))
                    reason = $"入口存在跳转（{Field("transfer")}）；目标 {Field("target")} / {Field("targetModule")}；目标模块不等于修改者，仍拒装";
                else if (line.Contains("reason=relocation-or-image-mismatch ", StringComparison.Ordinal))
                    reason = "未见已支持的入口跳转形态；可能为重定位或映像差异，尚未证实系统差异，修改者未识别，仍拒装";
                else if (line.Contains("reason=disk-evidence-unavailable ", StringComparison.Ordinal))
                    reason = "磁盘对照证据不可用，仍拒装";
                else if (line.Contains("reason=entry-read-failed ", StringComparison.Ordinal))
                    reason = "入口字节读取失败，仍拒装";
                entries[slot] = $"{slot}：{(module == "unidentified" ? "模块未识别" : module)}（{reason}）";
            }
            if (line.StartsWith("osd-install-refused ", StringComparison.Ordinal))
            {
                var reason = line[20..];
                if (reason == "retain-disabled" && refused) continue;
                refused = true;
                status = "游戏内状态行：本局未安装（原因：" + (reason switch
                {
                    "foreign-entry" => entries.Values.Any(v => v.Contains("第三方捕获/覆盖层或 mod 占槽", StringComparison.Ordinal))
                        ? "第三方捕获/覆盖层或 mod 占用设备槽" : "入口校验未通过；占用或修改者未识别",
                    "entry-changed-during-prepare" => "安装准备期间入口发生变化，已失败关闭",
                    "thread-window" => "无法证明安装线程窗口安全",
                    "Ex-presentation" => "尚不支持 D3D9Ex 呈现",
                    "allocation" => "覆盖层内存分配失败",
                    "retain-disabled" => "安装失败，具体原因未识别",
                    _ => "安装校验失败，诊断码：" + reason
                }) + "）。\n" + (entries.Count == 0 ? "占槽模块未识别；下列仅为排查候选，不是归因。" : string.Join("\n", entries.Values));
            }
            if (refused) continue;
            if (line == "osd-installed") status = "游戏内状态行：本局已安装，等待成功呈现；尚未确认可见。";
            if (line == "osd-frame-presented") status = "游戏内状态行：已收到成功呈现记录；实际文字以游戏画面为准。";
            if (line.StartsWith("osd-draw-failed ", StringComparison.Ordinal) || line.StartsWith("osd-present-failed ", StringComparison.Ordinal))
                status = "游戏内状态行：绘制／呈现失败，已停止绘制；入口与模块留驻至游戏退出。";
        }
        return status;
    }
}
