using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WeChatBoot.App;

/// <summary>圆角白色卡片容器，子控件默认白底。</summary>
internal sealed class Card : Panel
{
    private int _radius = 14;

    public Card()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw,
            true);
        BackColor = Theme.Card;
    }

    public int Radius
    {
        get => _radius;
        set { _radius = value; Invalidate(); }
    }

    /// <summary>去掉边框（用于贴边元素）。</summary>
    public bool ShowBorder { get; set; } = true;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);

        using var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
        using (var fill = new SolidBrush(BackColor))
        {
            g.FillPath(fill, path);
        }

        if (ShowBorder)
        {
            using var pen = new Pen(Theme.Border);
            g.DrawPath(pen, path);
        }
    }
}

internal enum ButtonKind
{
    Primary,
    Ghost,
    Danger,
}

/// <summary>胶囊按钮：主按钮渐变绿，次按钮白底描边。</summary>
internal sealed class PillButton : Button
{
    private bool _hover;
    private bool _down;

    public PillButton()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw,
            true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        Font = Theme.BodyBold;
        Size = new Size(120, 38);
    }

    public ButtonKind Kind { get; set; } = ButtonKind.Primary;

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _down = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _down = true;
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _down = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Card);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.Round(rect, Height / 2);
        var enabled = Enabled;

        if (Kind == ButtonKind.Primary)
        {
            var c1 = enabled ? Theme.AccentLight : Color.FromArgb(0xC9, 0xE8, 0xD8);
            var c2 = enabled ? Theme.AccentDark : Color.FromArgb(0xB4, 0xDD, 0xC8);
            if (_down) c1 = ControlPaint.Dark(c1, 0.06f);
            if (_hover && !_down) c1 = ControlPaint.Light(c1, 0.12f);

            using var brush = new LinearGradientBrush(rect, c1, c2, LinearGradientMode.Vertical);
            g.FillPath(brush, path);
        }
        else
        {
            using (var fill = new SolidBrush(_hover ? Theme.RowHover : Color.White))
            {
                g.FillPath(fill, path);
            }

            using var pen = new Pen(Kind == ButtonKind.Danger && _hover ? Theme.Warn : Theme.Border);
            g.DrawPath(pen, path);
        }

        var fore = Kind switch
        {
            ButtonKind.Primary => Color.White,
            ButtonKind.Danger => _hover ? Theme.Warn : Theme.TextSecondary,
            _ => _hover ? Theme.TextPrimary : Theme.TextSecondary,
        };
        if (!enabled) fore = Color.FromArgb(0xB6, 0xBD, 0xC6);

        TextRenderer.DrawText(
            g,
            Text,
            Font,
            ClientRectangle,
            fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>数量加减控件（自绘，− / 数值 / +）。</summary>
internal sealed class Stepper : Control
{
    private int _value = 2;
    private bool _hoverLeft;
    private bool _hoverRight;

    public Stepper()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw,
            true);
        Cursor = Cursors.Hand;
        Size = new Size(132, 38);
        Font = Theme.BodyBold;
        BackColor = Color.White;
    }

    public int Min { get; set; } = 1;

    public int Max { get; set; } = 20;

    /// <summary>每次点一下的步长。</summary>
    public int Step { get; set; } = 1;

    public int Value
    {
        get => _value;
        set
        {
            var v = Math.Clamp(value, Min, Max);
            if (v == _value) return;
            _value = v;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ValueChanged;

    private Rectangle LeftZone => new(1, 1, 36, Height - 2);

    private Rectangle RightZone => new(Width - 37, 1, 36, Height - 2);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Card);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.Round(rect, 10);
        using (var fill = new SolidBrush(Color.White))
        {
            g.FillPath(fill, path);
        }

        using (var pen = new Pen(Theme.Border))
        {
            g.DrawPath(pen, path);
        }

        if (_hoverLeft) Fill(g, LeftZone, 10, false);
        if (_hoverRight) Fill(g, RightZone, 10, true);

        var zoneColor = Theme.TextSecondary;
        TextRenderer.DrawText(g, "−", Font, LeftZone, zoneColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "+", Font, RightZone, zoneColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        var mid = new Rectangle(36, 1, Width - 73, Height - 2);
        TextRenderer.DrawText(g, Value.ToString(), Font, mid, Theme.TextPrimary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    /// <summary>把左侧/右侧圆角高亮画出来（只在内侧切角处圆）。</summary>
    private void Fill(Graphics g, Rectangle zone, int radius, bool right)
    {
        var r = new Rectangle(zone.X, zone.Y, zone.Width, zone.Height);
        using var path = new GraphicsPath();
        if (right)
        {
            path.AddArc(r.X, r.Y, radius * 2, radius * 2, 180, 90);
            path.AddLine(r.X + radius, r.Bottom, r.Right, r.Bottom);
            path.AddLine(r.Right, r.Bottom, r.Right, r.Y);
            path.AddLine(r.Right, r.Y, r.X + radius, r.Y);
        }
        else
        {
            path.AddLine(r.X, r.Y, r.Right, r.Y);
            path.AddLine(r.Right, r.Y, r.Right, r.Bottom);
            path.AddLine(r.Right, r.Bottom, r.X + radius, r.Bottom);
            path.AddArc(r.X, r.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
            path.AddLine(r.X, r.Y + radius, r.X, r.Y);
        }

        path.CloseFigure();
        using var b = new SolidBrush(Theme.RowHover);
        g.FillPath(b, path);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var l = LeftZone.Contains(e.Location);
        var r = RightZone.Contains(e.Location);
        if (l != _hoverLeft || r != _hoverRight)
        {
            _hoverLeft = l;
            _hoverRight = r;
            Invalidate();
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hoverLeft = _hoverRight = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (LeftZone.Contains(e.Location)) Value -= Step;
        else if (RightZone.Contains(e.Location)) Value += Step;
        base.OnMouseDown(e);
    }
}

/// <summary>下拉选择框（自绘，点击弹出菜单）。</summary>
internal sealed class PickerField : Control
{
    private string _text = "未找到微信";
    private bool _hover;
    private ContextMenuStrip? _menu;

    public PickerField()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw,
            true);
        Cursor = Cursors.Hand;
        Font = Theme.BodyFont;
        Size = new Size(280, 38);
        BackColor = Color.White;
    }

    public IReadOnlyList<(string Text, object? Tag)> Items { get; private set; } = Array.Empty<(string, object?)>();

    public int SelectedIndex { get; private set; } = -1;

    public (string Text, object? Tag)? Selected =>
        SelectedIndex >= 0 && SelectedIndex < Items.Count ? Items[SelectedIndex] : null;

    public event EventHandler? SelectionChanged;

    public bool DropDownEnabled
    {
        get => Cursor != Cursors.Default;
        set
        {
            Cursor = value ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    public void SetItems(IReadOnlyList<(string Text, object? Tag)> items, int select)
    {
        Items = items;
        SelectedIndex = select >= 0 && select < items.Count ? select : -1;
        _text = Selected?.Text ?? "未找到微信";
        _menu?.Dispose();
        _menu = null;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Card);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.Round(rect, 10);
        using (var fill = new SolidBrush(_hover && Items.Count > 0 ? Theme.RowHover : Color.White))
        {
            g.FillPath(fill, path);
        }

        using (var pen = new Pen(Theme.Border))
        {
            g.DrawPath(pen, path);
        }

        var textRect = new Rectangle(14, 1, Width - 42, Height - 2);
        TextRenderer.DrawText(
            g,
            _text,
            Font,
            textRect,
            Selected is null ? Theme.TextSecondary : Theme.TextPrimary,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        var chev = new Rectangle(Width - 28, 1, 18, Height - 2);
        TextRenderer.DrawText(g, "▾", Font, chev, Theme.TextSecondary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (Items.Count == 0) return;

        _menu ??= BuildMenu();
        _menu.Show(this, new Point(0, Height + 4));
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip
        {
            Font = Theme.BodyFont,
            ShowImageMargin = false,
            BackColor = Color.White,
        };

        for (var i = 0; i < Items.Count; i++)
        {
            var index = i;
            var item = new ToolStripMenuItem(Items[i].Text) { Padding = new Padding(6, 3, 6, 3) };
            if (i == SelectedIndex) item.Font = Theme.BodyBold;
            item.Click += (_, _) =>
            {
                SelectedIndex = index;
                _text = Items[index].Text;
                Invalidate();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            };
            menu.Items.Add(item);
        }

        return menu;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _menu?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>实例列表里的一行：绿点 + 标题 + 明细 + 路径。</summary>
internal sealed class InstanceRow : Panel
{
    private readonly Label _title;
    private readonly Label _meta;
    private readonly Label _path;
    private bool _hover;

    public InstanceRow(int index, int pid, string title, string meta, string path)
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw,
            true);
        Height = 46;
        Margin = new Padding(0, 0, 0, 4);
        BackColor = Color.White;
        Cursor = Cursors.Default;

        _title = new Label
        {
            AutoSize = false,
            Font = Theme.BodyBold,
            ForeColor = Theme.TextPrimary,
            BackColor = Color.Transparent,
            Text = $"微信 #{index}",
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(28, 4),
            Size = new Size(240, 18),
        };

        _meta = new Label
        {
            AutoSize = false,
            Font = Theme.SmallFont,
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
            Text = meta,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(28, 22),
            Size = new Size(340, 16),
        };

        _path = new Label
        {
            AutoSize = false,
            Font = Theme.SmallFont,
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
            Text = path,
            TextAlign = ContentAlignment.MiddleRight,
            Location = new Point(180, 13),
            Size = new Size(300, 18),
            AutoEllipsis = true,
        };

        _path.Text = string.Empty;
        var tip = new ToolTip();
        tip.SetToolTip(this, path);
        tip.SetToolTip(_meta, path);

        Controls.AddRange(new Control[] { _title, _meta, _path });

        _title.MouseEnter += (_, _) => SetHover(true);
        _meta.MouseEnter += (_, _) => SetHover(true);
        _path.MouseEnter += (_, _) => SetHover(true);
        _title.MouseLeave += (_, _) => SetHover(false);
        _meta.MouseLeave += (_, _) => SetHover(false);
        _path.MouseLeave += (_, _) => SetHover(false);
    }

    /// <summary>窗口标题（「微信」/「Weixin」）放在标题后面做淡色说明。</summary>
    public void SetWindowTitle(string windowTitle)
    {
        _title.Text = string.IsNullOrWhiteSpace(windowTitle)
            ? _title.Text
            : $"{_title.Text}";
    }

    private void SetHover(bool hover)
    {
        if (_hover == hover) return;
        _hover = hover;
        BackColor = hover ? Theme.RowHover : Color.White;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        // 构造时先设 Height 会触发一次 OnResize，此时子控件还没建好
        if (_path is null || _meta is null) return;

        _path.Location = new Point(Width - 312, 13);
        _meta.Width = Math.Max(120, Width - 350);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        using (var fill = new SolidBrush(BackColor))
        {
            using var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), 10);
            g.FillPath(fill, path);
        }

        using (var dot = new SolidBrush(Theme.Accent))
        {
            // 和标题第一行居中对齐（标题 Top=4、高 18）
            g.FillEllipse(dot, 12, 9, 8, 8);
        }
    }
}
