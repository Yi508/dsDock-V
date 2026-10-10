using System.Reflection;

namespace DsDock.Diagnostics;

/// <summary>版本号唯一读取点：来自 csproj 的 &lt;Version&gt; / &lt;InformationalVersion&gt;，避免多处硬编码漂移。</summary>
internal static class AppVersion
{
    /// <summary>显示用版本，例如 "1.08"。</summary>
    public static string Display { get; } =
        (typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.00")
        .Split('+')[0].Trim();

    /// <summary>语义版本，例如 "1.0.5.0"（程序集版本，供卸载项/调试用）。</summary>
    public static string Semantic { get; } =
        typeof(AppVersion).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>完整摘要，例如 "dsDock 1.05 (1.0.5.0)"。</summary>
    public static string Summary => $"dsDock {Display} ({Semantic})";
}