using System;
using System.Windows;
using System.Windows.Media;
using DsDock.Diagnostics;

namespace DsDock.Appearance;

/// <summary>
/// Runtime appearance values (强调色/字号/圆角/透明度/磨砂). Values come from settings.json or the
/// command line; the settings window mutates them and raises Changed so every window re-applies.
/// </summary>
internal static class Theme
{
    /// <summary>五个预设强调色（spec: 五个预设 + 自定义调色板）。</summary>
    public static readonly (string Name, string Hex)[] Presets =
    {
        ("粉", "#DB6BBC"),
        ("蓝", "#3B82F6"),
        ("青", "#06B6D4"),
        ("绿", "#22C55E"),
        ("橙", "#F59E0B"),
        ("紫", "#A855F7"),
    };

    public static Color Accent { get; private set; } = Color.FromRgb(0xDB, 0x6B, 0xBC);
    public static double FontSize { get; private set; } = 15;
    public static int Corner { get; private set; } = 6;
    public static int Alpha { get; private set; } = 90;
    public static int Frost { get; private set; } = 50;

    public static Color PanelTint { get; } = Color.FromRgb(0x12, 0x18, 0x24);

    public static event Action? Changed;

    public static void Apply(Color accent, double fontSize, int corner, int alpha, int frost)
    {
        Accent = accent;
        FontSize = fontSize;
        Corner = corner;
        Alpha = alpha;
        Frost = frost;
        Log.Info($"外观更新: accent=#{accent.R:X2}{accent.G:X2}{accent.B:X2} font={fontSize} corner={corner} alpha={alpha} frost={frost}");
        Changed?.Invoke();
    }

    public static void ApplyAccent(string hex)
    {
        if (TryParseHex(hex, out Color color))
            Apply(color, FontSize, Corner, Alpha, Frost);
    }

    public static bool TryParseHex(string? hex, out Color color)
    {
        color = Accent;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try
        {
            string v = hex.TrimStart('#');
            if (v.Length != 6) return false;
            color = Color.FromRgb(
                Convert.ToByte(v.Substring(0, 2), 16),
                Convert.ToByte(v.Substring(2, 2), 16),
                Convert.ToByte(v.Substring(4, 2), 16));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>半透明强调色，用于高亮/悬停。</summary>
    public static SolidColorBrush AccentBrush(byte alpha = 0xFF)
        => new(Color.FromArgb(alpha, Accent.R, Accent.G, Accent.B));

    /// <summary>静态设计 token 从 Themes/Default.xaml 读，读不到则用这里的兜底值。</summary>
    public static double TokenDuration(string key, double fallback)
    {
        if (Application.Current?.Resources[key] is double d) return d;
        return fallback;
    }

    public static CornerRadius TokenCorner(string key, double fallback)
    {
        if (Application.Current?.Resources[key] is CornerRadius c) return c;
        return new CornerRadius(fallback);
    }
}
