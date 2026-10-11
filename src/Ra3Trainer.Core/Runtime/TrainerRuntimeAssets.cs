using System.Reflection;
using Ra3Trainer.Core.Manifest;

namespace Ra3Trainer.Core.Runtime;

public static class TrainerRuntimeAssets
{
    private const string ManifestResourceName = "Ra3Trainer.Core.Assets.trainer_report.json";
    private const string BootstrapResourceName = "Ra3Trainer.Core.Assets.00_bootstrap.aa";

    public static TrainerManifest LoadManifest()
    {
        using var stream = OpenResource(ManifestResourceName);
        return TrainerManifestRepository.Load(stream, ManifestResourceName);
    }

    public static IReadOnlyList<string> ReadBootstrapLines()
    {
        using var stream = OpenResource(BootstrapResourceName);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        // 只读包装：IReadOnlyList 只是编译期视图，返回 List 的话调用方可以 cast 回去改内容
        // （这个列表会一路喂给 BootstrapCodeBuilder/AaScriptReader）。
        return lines.AsReadOnly();
    }

    private static Stream OpenResource(string name)
    {
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded runtime asset: {name}.");
    }
}
