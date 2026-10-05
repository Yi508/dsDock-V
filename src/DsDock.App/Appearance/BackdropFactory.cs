using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using DsDock.Platform;

namespace DsDock.Appearance;

/// <summary>
/// Fallback "frosted glass": the blur is baked from the current wallpaper instead of asking
/// DWM, because a WS_CHILD window of explorer.exe has no DWM backdrop to blur.
/// </summary>
internal static class BackdropFactory
{
    public static UIElement? Create(IntPtr hwnd, NativeMethods.RECT windowScreenRect, double dipWidth, double dipHeight,
        double dpiScale, int frostPercent, Color tint, double tintOpacity, out string detail)
    {
        detail = "";
        string path = GetWallpaperPath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            detail = "未取到壁纸路径（SPI_GETDESKWALLPAPER 为空或文件不存在），本模式只用纯色背景";
            return null;
        }

        BitmapImage bmp;
        try
        {
            bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
        }
        catch (Exception ex)
        {
            detail = $"壁纸加载失败: {ex.Message}";
            return null;
        }

        int vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int vw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int vh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        if (vw <= 0 || vh <= 0) { vx = 0; vy = 0; vw = windowScreenRect.Right; vh = windowScreenRect.Bottom; }

        (string style, bool tile) = GetWallpaperStyle();
        double iw = bmp.PixelWidth, ih = bmp.PixelHeight;

        // Where the wallpaper is drawn on the virtual screen.
        double px = vx, py = vy, pw = vw, ph = vh;
        switch (style)
        {
            case "10": // fill
            case "22": // span
            {
                double s = Math.Max(vw / iw, vh / ih);
                pw = iw * s; ph = ih * s;
                px = vx + (vw - pw) / 2.0; py = vy + (vh - ph) / 2.0;
                break;
            }
            case "6": // fit
            {
                double s = Math.Min(vw / iw, vh / ih);
                pw = iw * s; ph = ih * s;
                px = vx + (vw - pw) / 2.0; py = vy + (vh - ph) / 2.0;
                break;
            }
            case "2": // stretch
                break;
            default: // 0 = center, 1 = tile, anything unknown -> natural size centered
                pw = iw; ph = ih;
                px = vx + (vw - pw) / 2.0; py = vy + (vh - ph) / 2.0;
                break;
        }

        double scaleX = pw / iw;
        double scaleY = ph / ih;

        // Crop the wallpaper region that sits behind the window, expanded by the blur pad.
        double blurDip = frostPercent <= 0 ? 0 : 6.0 + frostPercent / 100.0 * 26.0;
        double padDip = blurDip * 2.0 + 4.0;
        double padPx = padDip * dpiScale;

        double cropX = (windowScreenRect.Left - padPx - px) / scaleX;
        double cropY = (windowScreenRect.Top - padPx - py) / scaleY;
        double cropW = (windowScreenRect.Width + 2 * padPx) / scaleX;
        double cropH = (windowScreenRect.Height + 2 * padPx) / scaleY;

        int cx = (int)Math.Floor(cropX);
        int cy = (int)Math.Floor(cropY);
        int cw = (int)Math.Ceiling(cropW);
        int ch = (int)Math.Ceiling(cropH);
        bool clamped = false;

        if (cx < 0) { cw += cx; cx = 0; clamped = true; }
        if (cy < 0) { ch += cy; cy = 0; clamped = true; }
        if (cx + cw > iw) { cw = (int)iw - cx; clamped = true; }
        if (cy + ch > ih) { ch = (int)ih - cy; clamped = true; }
        if (cw <= 0 || ch <= 0)
        {
            detail = "裁剪区域落在壁纸之外（可能壁纸小于屏幕），退回纯色背景";
            return null;
        }

        ImageSource source = bmp;
        try
        {
            source = new CroppedBitmap(bmp, new Int32Rect(cx, cy, cw, ch));
            source.Freeze();
        }
        catch (Exception ex)
        {
            detail = $"CroppedBitmap 失败({ex.Message})，退回整图";
        }

        var grid = new Grid { ClipToBounds = true, IsHitTestVisible = false };

        var image = new Image
        {
            Source = source,
            Stretch = Stretch.Fill,
            Width = dipWidth + padDip * 2,
            Height = dipHeight + padDip * 2,
            Margin = new Thickness(-padDip),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        if (blurDip > 0.01)
        {
            image.Effect = new BlurEffect { Radius = blurDip, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
        }
        grid.Children.Add(image);

        var tintRect = new System.Windows.Shapes.Rectangle
        {
            Fill = new SolidColorBrush(tint),
            Opacity = tintOpacity,
        };
        grid.Children.Add(tintRect);

        detail = $"壁纸模糊兜底: {Path.GetFileName(path)} style={style}{(tile ? "+tile" : "")} " +
                 $"crop=({cx},{cy},{cw},{ch}) blur={blurDip:F1}dip tintOpacity={tintOpacity:F2}" +
                 (clamped ? "（裁剪被壁纸边界截断，边缘可能有淡出）" : "");
        return grid;
    }

    public static string GetWallpaperPath()
    {
        var sb = new StringBuilder(512);
        if (NativeMethods.SystemParametersInfoW(NativeMethods.SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0))
            return sb.ToString();
        return "";
    }

    public static (string Style, bool Tile) GetWallpaperStyle()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            string style = key?.GetValue("WallpaperStyle")?.ToString() ?? "10";
            bool tile = (key?.GetValue("TileWallpaper")?.ToString() ?? "0") == "1";
            return (style, tile);
        }
        catch
        {
            return ("10", false);
        }
    }
}
