using System.Drawing;
using System.Windows.Forms;
using WeChatBoot.Core;

namespace WeChatBoot.App;

/// <summary>
/// 设置窗口。主界面刻意只留「做什么」，所有「怎么做的」都收到这里：
/// 启动方式、间隔、超时、额外互斥量名、手动指定过的路径。
/// 只有点保存才写回配置，点取消一律不改。
/// 布局全部用显式坐标（不用 Dock/Anchor）—— 纯代码界面下 Dock 的排列顺序不可靠。
/// </summary>
internal sealed class SettingsForm : Form
{
    private const int Pad = 28;
    private const int ContentWidth = 504;
    private const int FieldWidth = 140;
    private const int FieldHeight = 36;
    private const int Col2X = Pad + 252;

    private readonly AppConfig _config;

    private readonly PickerField _strategy = new();
    private readonly Stepper _stagger = new();
    private readonly Stepper _timeout = new();
    private readonly TextBox _extraMutexes = new();
    private readonly ListBox _recentPaths = new();
    private readonly Label _configPath = new();

    private static readonly (LaunchStrategy Value, string Label)[] Strategies =
    {
        (LaunchStrategy.Auto, "自动（推荐）"),
        (LaunchStrategy.MutexAcl, "互斥量 ACL（免管理员）"),
        (LaunchStrategy.CloseHandle, "关闭句柄（兜底）"),
        (LaunchStrategy.Direct, "直接启动（只用于对比测试）"),
    };

    public SettingsForm(AppConfig config)
    {
        _config = config;

        Text = "设置";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Color.White;
        Font = Theme.BodyFont;
        AutoScaleMode = AutoScaleMode.None;

        BuildUi();
        LoadValues();
    }

    private void BuildUi()
    {
        var y = 20;

        AddLabel("设置", Pad, y, ContentWidth, Theme.TitleFont, Theme.TextPrimary);
        y += 38;
        AddLabel("一般不用改。改完点保存，下次启动就按这个来。", Pad, y, ContentWidth, Theme.SmallFont, Theme.TextSecondary);
        y += 34;

        // 启动方式
        AddCaption("启动方式", Pad, y);
        y += 24;
        _strategy.SetItems(StrategyItems(), 0);
        _strategy.SetBounds(Pad, y, 340, 36);
        y += 50;

        // 两个数值项并排
        AddCaption("启动间隔（毫秒）", Pad, y);
        AddCaption("等待窗口超时（毫秒）", Col2X, y);
        y += 24;

        _stagger.Min = 200;
        _stagger.Max = 5000;
        _stagger.Step = 100;
        _stagger.Font = Theme.BodyFont;
        _stagger.SetBounds(Pad, y, FieldWidth + 16, FieldHeight);

        _timeout.Min = 2000;
        _timeout.Max = 30000;
        _timeout.Step = 500;
        _timeout.Font = Theme.BodyFont;
        _timeout.SetBounds(Col2X, y, FieldWidth + 16, FieldHeight);
        y += 44;

        AddLabel("两次启动之间等多久，太短容易互相抢", Pad, y, 236, Theme.SmallFont, Theme.TextSecondary);
        AddLabel("慢机器调大一点，等窗口出现的最长时间", Col2X, y, 252, Theme.SmallFont, Theme.TextSecondary);
        y += 38;

        // 额外互斥量名
        AddCaption("额外互斥量名（每行一个）", Pad, y);
        y += 22;
        AddLabel("一般留空。微信换版本改了互斥量名时，在这里手动补上。", Pad, y, ContentWidth, Theme.SmallFont, Theme.TextSecondary);
        y += 22;

        _extraMutexes.Multiline = true;
        _extraMutexes.BorderStyle = BorderStyle.FixedSingle;
        _extraMutexes.ScrollBars = ScrollBars.Vertical;
        _extraMutexes.Font = Theme.LogFont;
        _extraMutexes.SetBounds(Pad, y, ContentWidth, 72);
        y += 88;

        // 手动指定过的路径
        AddCaption("手动指定过的微信路径", Pad, y);
        y += 24;

        _recentPaths.Font = Theme.SmallFont;
        _recentPaths.BorderStyle = BorderStyle.FixedSingle;
        _recentPaths.IntegralHeight = false;
        _recentPaths.SetBounds(Pad, y, ContentWidth, 76);
        y += 84;

        AddLabel("点「手动指定…」选过的路径会记在这里，符号链接也行。", Pad, y + 4, ContentWidth - 100, Theme.SmallFont, Theme.TextSecondary);

        var removePath = new PillButton
        {
            Text = "删除选中",
            Kind = ButtonKind.Ghost,
            Font = Theme.SmallFont,
            Size = new Size(84, 26),
            Location = new Point(Pad + ContentWidth - 84, y),
        };
        removePath.Click += (_, _) =>
        {
            if (_recentPaths.SelectedIndex < 0) return;
            _recentPaths.Items.RemoveAt(_recentPaths.SelectedIndex);
        };
        y += 36;

        // 配置文件位置
        _configPath.Font = Theme.SmallFont;
        _configPath.ForeColor = Theme.TextSecondary;
        _configPath.AutoSize = false;
        _configPath.AutoEllipsis = true;
        _configPath.SetBounds(Pad, y, ContentWidth, 18);
        _configPath.Text = $"配置文件：{AppConfig.ConfigFilePath}";
        y += 30;

        // 底部按钮
        var openFolder = new PillButton
        {
            Text = "打开配置文件夹",
            Kind = ButtonKind.Ghost,
            Font = Theme.SmallFont,
            Size = new Size(124, 32),
            Location = new Point(Pad, y),
        };
        openFolder.Click += (_, _) => OpenConfigFolder();

        var reset = new PillButton
        {
            Text = "恢复默认",
            Kind = ButtonKind.Ghost,
            Size = new Size(92, 32),
            Location = new Point(Pad + ContentWidth - 92 - 88 - 96 - 20, y),
        };
        reset.Click += (_, _) => ResetDefaults();

        var cancel = new PillButton
        {
            Text = "取消",
            Kind = ButtonKind.Ghost,
            Size = new Size(88, 32),
            Location = new Point(Pad + ContentWidth - 88 - 96 - 10, y),
            DialogResult = DialogResult.Cancel,
        };

        var save = new PillButton
        {
            Text = "保存",
            Kind = ButtonKind.Primary,
            Size = new Size(96, 32),
            Location = new Point(Pad + ContentWidth - 96, y),
        };
        save.Click += (_, _) => SaveAndClose();

        Controls.AddRange(new Control[]
        {
            _strategy, _stagger, _timeout, _extraMutexes, _recentPaths, removePath,
            _configPath, openFolder, reset, cancel, save,
        });

        y += 32 + 26;
        ClientSize = new Size(Pad * 2 + ContentWidth, y);

        CancelButton = cancel;
        AcceptButton = save;
        ActiveControl = _strategy;
    }

    private void AddCaption(string text, int x, int y)
    {
        AddLabel(text, x, y, ContentWidth, Theme.BodyBold, Theme.TextPrimary);
    }

    private void AddLabel(string text, int x, int y, int width, Font font, Color color)
    {
        Controls.Add(new Label
        {
            Text = text,
            Font = font,
            ForeColor = color,
            AutoSize = false,
            UseMnemonic = false,
            Size = new Size(width, 20),
            Location = new Point(x, y),
        });
    }

    private static (string Text, object? Tag)[] StrategyItems()
        => Array.ConvertAll(Strategies, s => (s.Label, (object?)s.Value));

    private void LoadValues()
    {
        var index = Array.FindIndex(Strategies, s => s.Value == _config.Strategy);
        _strategy.SetItems(StrategyItems(), index < 0 ? 0 : index);
        _stagger.Value = Math.Clamp(_config.StaggerMs, 200, 5000);
        _timeout.Value = Math.Clamp(_config.VerifyTimeoutMs, 2000, 30000);
        _extraMutexes.Text = string.Join(Environment.NewLine, _config.ExtraMutexNames);

        _recentPaths.Items.Clear();
        foreach (var path in _config.RecentPaths) _recentPaths.Items.Add(path);
        _recentPaths.SelectedIndex = _recentPaths.Items.Count > 0 ? 0 : -1;
    }

    private void ResetDefaults()
    {
        _strategy.SetItems(StrategyItems(), 0);
        _stagger.Value = 1200;
        _timeout.Value = 6000;
        _extraMutexes.Text = string.Empty;
        _recentPaths.Items.Clear();
    }

    private void SaveAndClose()
    {
        _config.Strategy = Strategies[Math.Max(0, _strategy.SelectedIndex)].Value;
        _config.StaggerMs = _stagger.Value;
        _config.VerifyTimeoutMs = _timeout.Value;
        _config.ExtraMutexNames = _extraMutexes.Lines
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _config.RecentPaths = _recentPaths.Items.Cast<string>().ToList();

        if (!_config.Save(out var error))
        {
            MessageBox.Show(this, $"保存失败：{error}", "设置", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private void OpenConfigFolder()
    {
        try
        {
            var folder = Path.GetDirectoryName(AppConfig.ConfigFilePath);
            if (string.IsNullOrEmpty(folder)) return;
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"打不开文件夹：{ex.Message}", "设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
