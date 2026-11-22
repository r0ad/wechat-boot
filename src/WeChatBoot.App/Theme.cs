using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WeChatBoot.App;

/// <summary>
/// 全局配色、字体与 DPI 缩放。
///
/// 为什么要自己做缩放：界面是纯代码 + 显式像素坐标搭的，WinForms 的 AutoScale 只管
/// 「创建那一刻就已经在父容器里的控件」，运行时建的实例行、自绘控件内部的偏移一律不管；
/// 而字体用 pt 时又会被 GDI 按系统 DPI 放大。两者一叠加就是「字放大了、格子没放大」——
/// 字挤在一起、标题被裁掉。所以这里统一口径：
///   · 所有像素常量过一遍 S()（96 DPI 的设计像素 → 当前 DPI 的物理像素）；
///   · 字体用「像素」单位创建，大小同样乘缩放比，不再受系统 DPI 隐式影响。
/// 这样布局和文字永远同倍率，且可以在一台 96 DPI 的机器上按 2 倍渲染做截图回归。
/// </summary>
internal static class Theme
{
    private static float _scale = 1f;

    /// <summary>当前缩放比例（96 DPI = 1.0，200% 缩放 = 2.0）。</summary>
    public static float Scale => _scale;

    /// <summary>把 96 DPI 下的设计像素换算成当前 DPI 下的物理像素。</summary>
    public static int S(int value) => (int)MathF.Round(value * _scale, MidpointRounding.AwayFromZero);

    public static Size Sz(int width, int height) => new(S(width), S(height));

    public static Point Pt(int x, int y) => new(S(x), S(y));

    public static Padding Pad(int all) => new(S(all));

    public static Padding Pad(int left, int top, int right, int bottom)
        => new(S(left), S(top), S(right), S(bottom));

    public static readonly Color Bg = Color.FromArgb(0xF4, 0xF6, 0xF9);
    public static readonly Color Card = Color.White;
    public static readonly Color Border = Color.FromArgb(0xE8, 0xEC, 0xF2);
    public static readonly Color RowHover = Color.FromArgb(0xF7, 0xFA, 0xF9);

    public static readonly Color TextPrimary = Color.FromArgb(0x1B, 0x1F, 0x24);
    public static readonly Color TextSecondary = Color.FromArgb(0x8B, 0x92, 0x9C);

    public static readonly Color Accent = Color.FromArgb(0x07, 0xC1, 0x60);
    public static readonly Color AccentLight = Color.FromArgb(0x2B, 0xD9, 0x8A);
    public static readonly Color AccentDark = Color.FromArgb(0x05, 0xA6, 0x5F);
    public static readonly Color AccentSoft = Color.FromArgb(0xEA, 0xF9, 0xF1);
    public static readonly Color Warn = Color.FromArgb(0xE0, 0x6A, 0x2B);

    /// <summary>15pt ≈ 20px（96 DPI 下的设计尺寸，下面同理）。</summary>
    public static Font TitleFont { get; private set; } = MakeFont(20f, FontStyle.Bold);

    /// <summary>12.5pt ≈ 17px。</summary>
    public static Font HeaderFont { get; private set; } = MakeFont(17f, FontStyle.Bold);

    /// <summary>9.5pt ≈ 13px。</summary>
    public static Font BodyFont { get; private set; } = MakeFont(13f, FontStyle.Regular);

    public static Font BodyBold { get; private set; } = MakeFont(13f, FontStyle.Bold);

    /// <summary>8.5pt ≈ 11px。</summary>
    public static Font SmallFont { get; private set; } = MakeFont(11f, FontStyle.Regular);

    public static Font LogFont { get; private set; } = MakeFont(11f, FontStyle.Regular, "Consolas");

    /// <summary>Windows 的缩放百分比是 100/125/150/175/200/250/300，这里给足余量。</summary>
    public static void ApplyDpi(int deviceDpi) => ApplyScale(deviceDpi <= 0 ? 1f : deviceDpi / 96f);

    /// <summary>切换缩放比并重建字体。界面控件必须在下次创建时才会拿到新字体，见 <see cref="MainForm"/>。</summary>
    public static void ApplyScale(float scale)
    {
        _scale = Math.Clamp(scale, 0.75f, 4f);

        TitleFont = MakeFont(20f, FontStyle.Bold);
        HeaderFont = MakeFont(17f, FontStyle.Bold);
        BodyFont = MakeFont(13f, FontStyle.Regular);
        BodyBold = MakeFont(13f, FontStyle.Bold);
        SmallFont = MakeFont(11f, FontStyle.Regular);
        LogFont = MakeFont(11f, FontStyle.Regular, "Consolas");
    }

    /// <summary>
    /// 一律用 GraphicsUnit.Pixel：字号已经乘过缩放比，不要再让 GDI 按系统 DPI 二次放大。
    /// </summary>
    private static Font MakeFont(float pixelSize, FontStyle style, string family = "Microsoft YaHei UI")
    {
        var size = Math.Max(1f, pixelSize * _scale);
        try
        {
            var f = new Font(family, size, style, GraphicsUnit.Pixel);

            // Font 构造在缺字体时会静默回落到 Microsoft Sans Serif，这里检查一下名字
            return f.Name == family ? f : new Font("Segoe UI", size, style, GraphicsUnit.Pixel);
        }
        catch
        {
            return new Font(FontFamily.GenericSansSerif, size, style, GraphicsUnit.Pixel);
        }
    }

    public static GraphicsPath Round(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0 || r.Width <= 1 || r.Height <= 1)
        {
            path.AddRectangle(r);
            return path;
        }

        var d = radius * 2;
        d = Math.Min(d, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>
/// 启动时读系统 DPI。必须在 ApplicationConfiguration.Initialize()（设进程 DPI 感知）之后调用。
/// 读不到就按 96 处理：退化成「不缩放」，至少字和图不会错位。
/// </summary>
internal static class DpiProbe
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    public static int System()
    {
        try
        {
            var dpi = (int)GetDpiForSystem();
            if (dpi > 0) return dpi;
        }
        catch
        {
            // Win10 1607 之前的系统没有这个入口，走下面的 GDI 兜底
        }

        try
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            return (int)Math.Round(g.DpiX);
        }
        catch
        {
            return 96;
        }
    }
}
