using System;
using System.Reflection;

namespace Ra3Trainer.App;

/// <summary>
/// 产品身份与版本（界面「关于」用）。版本号来自项目文件里的 &lt;Version&gt;，不要在代码里再写一份。
/// </summary>
public static class AppInfo
{
    /// <summary>界面上显示的产品名。</summary>
    public const string DisplayName = "红警3修改器";

    /// <summary>主版本号，例：1.0.0。</summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>窗口标题用的一行：红警3修改器 v1.0.0。</summary>
    public static string TitleLine => $"{DisplayName} v{Version}";

    /// <summary>游戏内面板窗口的标题。</summary>
    public static string PanelTitleLine => $"{DisplayName} 游戏内面板";

    private static string ResolveVersion()
    {
        try
        {
            var assembly = typeof(AppInfo).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                // 源链接版本可能带 +commit 尾巴，界面只显示纯版本号
                var plus = informational.IndexOf('+');
                return plus > 0 ? informational[..plus] : informational;
            }

            var file = assembly.GetName().Version;
            return file is null ? "1.0.0" : $"{file.Major}.{file.Minor}.{file.Build}";
        }
        catch
        {
            return "1.0.0";
        }
    }
}
