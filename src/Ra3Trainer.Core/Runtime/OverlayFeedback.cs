namespace Ra3Trainer.Core.Runtime;

/// <summary>Diagnostic display only; never authorizes installation or memory writes.</summary>
public static class OverlayFeedback
{
    public const string Guidance = "面板／状态行不显示时，按这个顺序查：\n"
        + "1) 是否勾了「游戏内面板 M1」（或「游戏内状态行」）—— 没勾就不会部署。\n"
        + "2) 是否用【一键启动】进的游戏 —— 游戏已经开始跑之后再点连接，程序不会事后注入设备。勾选后必须走一键启动。\n"
        + "3) 面板位置 X/Y 是否越界 —— 越界时日志会写 TargetBounds，把位置改小重试。\n"
        + "4) 看「游戏内面板：…」那句状态：它会写明失败步骤与原因码（如 mapping-missing / heartbeat-expired / ABI 版本不符），把它发给我即可定位。\n"
        + "5) 只有当日志写 reason=entry-branch / patched-nonrelocation-byte / foreign-module 时，才说明入口真的被别人占了（会同时指名模块）。只有那种情况才需要按下面排查；否则不要关任何覆盖层。以下仅为排查候选，不代表已确认占槽。\n"
        + "• NVIDIA App → 设置 → 功能 → 关闭「高光 / Highlights」及「NVIDIA 覆盖」。仅关闭即时重放或桌面捕捉不够；这些是全局开关，与 RA3 是否在游戏列表无关。\n"
        + "• Discord → 用户设置 → 游戏覆盖 → 关闭游戏内覆盖。\n"
        + "• Steam → 设置 → 游戏中 → 关闭游戏内 Steam 界面。\n"
        + "• Epic / EOS：完全退出 Epic 启动器；不要删改 EOS DLL。\n"
        + "• 若模块属于日冕 / mod，请在对应启动器关闭覆盖或捕获功能；没有开关时把模块名和日志给作者。不要删除 mod DLL。\n"
        + "关闭覆盖后须重启游戏，再点一键启动。";

    public static string? Parse(string log, long creationTime)
    {
        var marker = $"osd-session {creationTime:X16}";
        var lines = log.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var start = Array.FindLastIndex(lines, l => l == marker);
        if (start < 0) return null; // PID reuse or an old DLL cannot claim current evidence.
        var entries = new Dictionary<string, string>();
        string? status = null;
        string? drawFailure = null;
        bool refused = false;
        bool panelSeen = false;
        // The producer appends the line ending separately; ignore an incomplete final record.
        // 注：OCR 建议"最后一段非空就算数"，但这里是**有意**的保守口径，两个测试钉住
        // （PartialLogRecord_IsNotEvidence / Relay_... 里 Parse(Session+"osd-panel-drawn")==null）：
        // 没有换行收尾就可能读到写了一半的记录，宁可这轮不算、等换行落地后下一轮再算。
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
                // Round33 native output uses only these five exclusive codes.
                reason = Field("reason") switch
                {
                    "verified-system" => "verified-system：按重定位表校正后逐字节一致，入口校验通过，允许进入安装",
                    "patched-nonrelocation-byte" => "patched-nonrelocation-byte：非重定位字节不一致，拒绝安装；修改者未识别",
                    "entry-branch" => $"entry-branch：入口跳转（{Field("transfer")}）；目标 {Field("target")} / {Field("targetModule")}，拒绝安装；目标模块不等于修改者",
                    "image-mismatch" => "image-mismatch：映像身份或重定位值不一致，拒绝安装",
                    "unidentified" => "unidentified：校验所需证据不足或格式不支持，拒绝安装",
                    _ => reason // Keep historical logs readable without authorizing them.
                };
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
                    "creation-thread-unverified" => "无法确认 CreateDevice 线程来源，已停止安装",
                    "retain-disabled" => "安装失败，具体原因未识别",
                    _ => "安装校验失败，诊断码：" + reason
                }) + "）。\n" + (entries.Count == 0 ? "占槽模块未识别；下列仅为排查候选，不是归因。" : string.Join("\n", entries.Values));
            }
            if (refused) continue;
            if (line.StartsWith("osd-panel-step ", StringComparison.Ordinal) && line.Length <= 512 && !line.Any(char.IsControl))
            {
                panelSeen = true;
                string PanelField(string key) => line.Split(' ').FirstOrDefault(p => p.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..] ?? "未知";
                var hr = PanelField("hr");
                if (hr != "00000000")
                {
                    var reason = PanelField("reason");
                    var explanation = reason switch
                    {
                        "mapping-missing" => "面板映射缺失，等待修改器发布",
                        "mapping-size" => "面板映射容量不符",
                        "abi-mismatch" => "面板 ABI 版本或布局不匹配",
                        "identity-mismatch" or "process-identity" => "面板进程身份校验失败",
                        "heartbeat-expired" => "面板心跳过期，修改器可能已断开",
                        "disabled" => "修改器已关闭面板",
                        "bounds-invalid" => "面板尺寸、步幅或位置越界",
                        "format-unsupported" => "面板像素格式不支持",
                        "reserved-nonzero" => "面板预留字段校验失败",
                        "snapshot-busy" or "snapshot-torn" => "面板快照尚未完整，本帧不画",
                        "checksum-mismatch" => "面板像素校验和不匹配",
                        "generation-mismatch" => "面板内容与脏标记不一致",
                        "TargetFormat" => "游戏后缓冲格式不支持",
                        "TargetBounds" => "游戏画面尺寸无法容纳完整面板",
                        "TextureCaps" => "设备纹理尺寸能力不足",
                        "CheckDeviceMultiSampleType" => "游戏 MSAA 配置校验失败",
                        "CreateTexture" => "面板纹理创建失败",
                        "LockTexture" or "UnlockTexture" or "TexturePitch" => "面板纹理像素上传失败",
                        "ApplyStateBlock" => "游戏绘制状态恢复失败",
                        _ => "面板步骤失败"
                    };
                    drawFailure = $"{explanation}；步骤={PanelField("name")}，hr={hr}，原因码={reason}。";
                    status = "游戏内面板：已停止绘制。" + drawFailure + "不影响游戏 hook 一键全撤。";
                }
            }
            if (line == "osd-panel-drawn")
            {
                panelSeen = true;
                drawFailure = null;
                status = "游戏内面板：收到绘制记录；真机像素待目视确认，M1 尚无游戏内点击功能。";
            }
            if (line.StartsWith("osd-draw-failure-detail name=", StringComparison.Ordinal) &&
                line.Length <= 256 && !line.Any(char.IsControl))
                drawFailure = line.Contains("name=OwnerThread ", StringComparison.Ordinal)
                    ? "失败步骤：OwnerThread。本地线程判定拒绝，绘制 API 尚未调用；不是 D3D 返回的线程限制。与游戏正常渲染的观测需进一步核对，请提供创建线程、Present 线程及 flags 读数。"
                    : "失败步骤：" + line["osd-draw-failure-detail ".Length..] + "。";
            if (line == "osd-installed") status = "游戏内状态行：本局已安装，等待成功呈现；尚未确认可见。\n" + string.Join("\n", entries.Values);
            if (line == "osd-frame-presented") status = (panelSeen
                ? "游戏内面板：收到成功呈现记录；实际面板以游戏画面为准，显示成功不等于能用。\n"
                : "游戏内状态行：已收到成功呈现记录；实际文字以游戏画面为准。\n") + string.Join("\n", entries.Values);
            if (line.StartsWith("osd-draw-failed ", StringComparison.Ordinal) || line.StartsWith("osd-present-failed ", StringComparison.Ordinal))
                status = (panelSeen ? "游戏内面板" : "游戏内状态行") + "：绘制／呈现失败，已停止绘制；入口与模块留驻至游戏退出。" + drawFailure;
        }
        return status;
    }
}
