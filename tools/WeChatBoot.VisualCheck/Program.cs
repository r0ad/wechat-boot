using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WeChatBoot.App;
using WeChatBoot.Core;

namespace WeChatBoot.VisualCheck;

/// <summary>
/// 高 DPI 视觉回归工具（无交互，跑完退出）。
///
///   dotnet run --project tools/WeChatBoot.VisualCheck -- [输出目录]
///
/// 做两件事：
///   1. 把主界面 / 设置页 / 实例行按 1x、1.5x、2x 渲染成 PNG，人眼看；
///   2. 量一遍几何：文字有没有比自己的框高（挤在一起就是这个）、同一张卡里的控件有没有重叠。
/// 判定标准就一条 —— 文字和格子必须是同一个倍率。1x 通过而 2x 不通过，就是缩放没跟上。
/// </summary>
internal static class Program
{
    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var outDir = args.Length > 0 ? args[0] : Path.Combine("artifacts", "visual");
        Directory.CreateDirectory(outDir);

        var failures = 0;

        foreach (var scale in new[] { 1f, 1.5f, 2f })
        {
            Theme.ApplyScale(scale);
            var suffix = scale.ToString("0.##", CultureInfo.InvariantCulture) + "x";

            var problems = new List<string>();

            using (var main = new MainForm())
            {
                Capture(main, Path.Combine(outDir, $"main-{suffix}.png"));
                problems.AddRange(Verify(main, "主界面"));
            }

            using (var settings = new SettingsForm(AppConfig.Load()))
            {
                Capture(settings, Path.Combine(outDir, $"settings-{suffix}.png"));
                problems.AddRange(Verify(settings, "设置页"));
            }

            problems.AddRange(VerifyRows(Path.Combine(outDir, $"rows-{suffix}.png")));

            if (problems.Count == 0)
            {
                Console.WriteLine($"{suffix}: OK · 卡片 {Theme.S(112)}px · 标题字 {Theme.HeaderFont.Height}px");
            }
            else
            {
                failures += problems.Count;
                Console.WriteLine($"{suffix}: {problems.Count} 处问题");
                foreach (var p in problems) Console.WriteLine($"  - {p}");
            }
        }

        Console.WriteLine($"图片目录：{Path.GetFullPath(outDir)}");

        // 配置口径迁移（纯 Core 逻辑，不依赖界面，但和缩放同源，放一起检查）
        var migration = VerifyConfigMigration();
        if (migration.Count == 0)
        {
            Console.WriteLine("配置迁移: OK · 200% 下的旧值 1320 换算成 660 逻辑像素");
        }
        else
        {
            failures += migration.Count;
            foreach (var p in migration) Console.WriteLine($"  - {p}");
        }

        return failures == 0 ? 0 : 1;
    }

    /// <summary>旧配置（物理像素）→ 新口径（逻辑像素）只换算一次，且新款配置不能再被换算。</summary>
    private static List<string> VerifyConfigMigration()
    {
        var problems = new List<string>();

        var legacy = new AppConfig { WindowWidth = 1320, WindowHeight = 1200 }; // LayoutVersion = 0
        if (!legacy.MigrateToLogicalWindowSize(2f))
            problems.Add("旧配置没有触发迁移");
        if (legacy.WindowWidth != 660 || legacy.WindowHeight != 600)
            problems.Add($"旧配置换算结果不对：{legacy.WindowWidth}x{legacy.WindowHeight}，期望 660x600");
        if (legacy.MigrateToLogicalWindowSize(2f))
            problems.Add("同一份配置被迁移了两次（第二次应该直接跳过）");
        if (legacy.WindowWidth != 660)
            problems.Add($"二次迁移改动了宽度：{legacy.WindowWidth}");

        var fresh = AppConfig.Load();
        if (fresh.WindowWidth is < 560 or > 1400)
            problems.Add($"配置加载后的宽度不合理：{fresh.WindowWidth}");

        return problems;
    }

    // ───────────────────────── 截图 ─────────────────────────

    /// <summary>显示出来（会闪一下），WM_PRINT 抓图，再关掉。</summary>
    private static void Capture(Form form, string path)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = Point.Empty;
        form.ShowInTaskbar = false;
        form.Show();
        Application.DoEvents();

        // 屏幕装不下 2x 的窗口，Form.Size 会被 MaxWindowTrackSize 夹住。
        // 直接 SetWindowPos 能放过一部分，剩下的交给几何检查兜底。
        var outer = form.Size;
        SetWindowPos(form.Handle, IntPtr.Zero, 0, 0, outer.Width, outer.Height, SwpNoZOrder | SwpNoActivate);
        Application.DoEvents();

        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
        bitmap.Save(path, ImageFormat.Png);
        form.Hide();
    }

    private static List<string> VerifyRows(string path)
    {
        var problems = new List<string>();
        var width = Theme.S(620);

        using var panel = new Panel
        {
            BackColor = Color.White,
            Size = new Size(width, Theme.S(106)),
            Padding = Theme.Pad(12, 12, 12, 12),
        };

        for (var i = 0; i < 2; i++)
        {
            panel.Controls.Add(new InstanceRow(i + 1, 12345, "微信", "4.2.0.53 · 已经开了 0 秒", @"C:\Tencent\Weixin\Weixin.exe")
            {
                Width = width - Theme.S(24),
                Location = new Point(0, Theme.S(50) * i),
            });
        }

        panel.Show();
        Application.DoEvents();

        using var bitmap = new Bitmap(panel.Width, panel.Height);
        panel.DrawToBitmap(bitmap, new Rectangle(0, 0, panel.Width, panel.Height));
        bitmap.Save(path, ImageFormat.Png);
        panel.Hide();

        problems.AddRange(Verify(panel, "实例行"));
        return problems;
    }

    // ───────────────────────── 几何检查 ─────────────────────────

    private static List<string> Verify(Control root, string scope)
    {
        var problems = new List<string>();

        foreach (var control in Walk(root))
        {
            var text = control.Text;
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (control.Width <= 0 || control.Height <= 0) continue;
            if (control is TextBox or ListBox or Panel or FlowLayoutPanel) continue;

            // 单行测量：字比框高 = 被裁掉 / 压到下一行，这正是 200% 时看到的样子
            var measured = TextRenderer.MeasureText(text, control.Font);
            if (measured.Height > control.Height + 1)
            {
                problems.Add($"{scope} · {Describe(control)} 文字高 {measured.Height}px > 控件高 {control.Height}px（「{Shorten(text)}」）");
            }
        }

        foreach (var parent in Walk(root).OfType<Control>().Prepend(root))
        {
            problems.AddRange(CheckOverlap(parent, scope));
        }

        return problems;
    }

    /// <summary>同一容器里非 Dock 的兄弟控件不该重叠（Dock=Fill 的层叠是故意的，跳过）。</summary>
    private static List<string> CheckOverlap(Control parent, string scope)
    {
        var problems = new List<string>();
        if (parent.Controls.Count < 2) return problems;
        if (parent.Controls.Cast<Control>().Any(c => c.Dock == DockStyle.Fill)) return problems;

        var kids = parent.Controls.Cast<Control>()
            .Where(c => c.Visible && c.Width > 0 && c.Height > 0 && c.Dock == DockStyle.None)
            .ToList();

        for (var i = 0; i < kids.Count; i++)
        {
            for (var j = i + 1; j < kids.Count; j++)
            {
                var a = kids[i];
                var b = kids[j];
                if (!a.Bounds.IntersectsWith(b.Bounds)) continue;

                problems.Add($"{scope} · 「{Shorten(a.Text)}」{a.Bounds} 和「{Shorten(b.Text)}」{b.Bounds} 重叠");
            }
        }

        return problems;
    }

    private static IEnumerable<Control> Walk(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var grand in Walk(child)) yield return grand;
        }
    }

    private static string Describe(Control c)
        => $"{c.GetType().Name}({c.Left},{c.Top} {c.Width}x{c.Height})";

    private static string Shorten(string text)
        => text.Replace("\r", " ").Replace("\n", " ").Length > 16
            ? text.Replace("\r", " ").Replace("\n", " ")[..16] + "…"
            : text.Replace("\r", " ").Replace("\n", " ");

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, int flags);
}
