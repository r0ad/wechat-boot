using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WeChatBoot.App;

/// <summary>全局配色与字体。改这里就能整体换肤。</summary>
internal static class Theme
{
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

    public static readonly Font TitleFont = MakeFont(15f, FontStyle.Bold);
    public static readonly Font HeaderFont = MakeFont(12.5f, FontStyle.Bold);
    public static readonly Font BodyFont = MakeFont(9.5f, FontStyle.Regular);
    public static readonly Font BodyBold = MakeFont(9.5f, FontStyle.Bold);
    public static readonly Font SmallFont = MakeFont(8.5f, FontStyle.Regular);
    public static readonly Font LogFont = MakeFont(8.5f, FontStyle.Regular, "Consolas");

    private static Font MakeFont(float size, FontStyle style, string family = "Microsoft YaHei UI")
    {
        try
        {
            var f = new Font(family, size, style);
            // Font 构造在缺字体时会静默回落到 Microsoft Sans Serif，这里检查一下名字
            return f.Name == family ? f : new Font("Segoe UI", size, style);
        }
        catch
        {
            return new Font(FontFamily.GenericSansSerif, size, style);
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
