using System.Text.Json;

namespace Ra3Trainer.Core.Manifest;

public static class TrainerManifestRepository
{
    private static readonly IReadOnlyDictionary<string, string[]> KnownActionFlags =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Select Unit Ammo MAX"] = ["iEnable+12"],
            ["Select Unit Ammo MAX 2"] = ["iEnable+12"],
            ["Danger Level MAX"] = ["iEnable+13"],
            ["Danger Level MIN"] = ["iEnable+13"],
            ["Restore Danger Level Normal"] = ["iEnable+13"],
            ["Restore Select Ore Mine"] = ["iEnable+14"]
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static TrainerManifest Load(string analysisDirectory)
    {
        var reportPath = Path.Combine(analysisDirectory, "trainer_report.json");
        using var stream = File.OpenRead(reportPath);
        return Load(stream, reportPath);
    }

    public static TrainerManifest Load(Stream stream, string sourceName)
    {
        var report = JsonSerializer.Deserialize<TrainerReportDto>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"Unable to read trainer manifest from {sourceName}.");

        // System.Text.Json 对"缺字段/显式 null"一律给 null，即使 C# 侧声明成非空：
        // 原来直接解引用，少一个键就是一句 NullReferenceException，看不出少了什么。
        var missing = new List<string>();
        if (report.TrainerMetadata is null) missing.Add("trainer_metadata");
        if (report.Features is null) missing.Add("features");
        if (report.PatchManifest is null) missing.Add("patch_manifest");
        else if (report.PatchManifest.Hooks is null) missing.Add("patch_manifest.hooks");
        if (report.ActionDispatch is null) missing.Add("action_dispatch");
        if (string.IsNullOrWhiteSpace(report.TrainerMetadata?.TargetProcess)) missing.Add("trainer_metadata.target_process");
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"{sourceName} 缺少必需字段：{string.Join("、", missing)}。");
        }

        // 这些字符串字段声明成非空，但 JSON 少写一个键就是 null，而 null 会一路走到
        // 进程名比较 / 补丁地址解析 / 派发键里：在那里报的错看不出是清单缺了哪个字段。
        return new TrainerManifest(
            report.TrainerMetadata!.TargetProcess!.Trim(),
            report.Features!.Select(ToFeature).ToArray(),
            new PatchManifest(report.PatchManifest!.Hooks!.Select((hook, index) => ToHook(hook, index)).ToArray()),
            report.ActionDispatch!
                .Select((item, index) => new ActionDispatchEntry(
                    Require(item.Value, $"action_dispatch[{index}].value"),
                    Require(item.Target, $"action_dispatch[{index}].target"),
                    Require(item.Description, $"action_dispatch[{index}].description")))
                .ToArray());
    }

    private static string Require(string? value, string fieldName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"清单缺少必需字段 {fieldName}。")
            : value;
    }

    private static TrainerFeature ToFeature(FeatureDto feature)
    {
        return new TrainerFeature(
            Require(feature.Name, "features[].name"),
            DisplayName(feature.Name),
            NormalizeHotkey(feature.Hotkey),
            ResolveEnableFlags(feature),
            feature.DispatchTarget,
            feature.ValueHint);
    }

    private static IReadOnlyList<string> ResolveEnableFlags(FeatureDto feature)
    {
        if (feature.EnableFlags is { Count: > 0 })
        {
            return feature.EnableFlags.ToArray();
        }

        return KnownActionFlags.TryGetValue(feature.Name, out var flags)
            ? flags
            : Array.Empty<string>();
    }

    private static PatchHook ToHook(PatchHookDto hook, int index)
    {
        var address = Require(hook.Address, $"patch_manifest.hooks[{index}].address");
        var sectionTitle = Require(hook.SectionTitle, $"patch_manifest.hooks[{index}].section_title");

        // 这两个字段 DTO 里声明成非空数组，但 JSON 里少写一个键反序列化出来就是 null，
        // 原来到这里直接 NRE（一句 NullReferenceException，看不出是哪个 hook 少了哪个字段）。
        if (hook.PatchAssembly is null || hook.OriginalAssembly is null)
        {
            var missing = hook.PatchAssembly is null ? "patch_assembly" : "original_assembly";
            throw new InvalidOperationException($"Patch hook '{address}' 缺少 {missing} 字段。");
        }

        return new PatchHook(
            address,
            sectionTitle,
            hook.PatchAssembly.ToArray(),
            hook.TrampolineTarget,
            hook.ReturnLabel,
            hook.EnableFlags?.ToArray() ?? Array.Empty<string>(),
            hook.OriginalAssembly.ToArray());
    }

    private static string DisplayName(string rawName)
    {
        return rawName
            .Replace("Moeny", "Money", StringComparison.Ordinal)
            .Replace("Destory", "Destroy", StringComparison.Ordinal);
    }

    private static string? NormalizeHotkey(string? hotkey)
    {
        return hotkey switch
        {
            "elete." => "Delete",
            "Page Up!" => "Page Up",
            _ => hotkey
        };
    }
}
