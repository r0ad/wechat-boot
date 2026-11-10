using System.Drawing;
using System.Windows.Forms;
using WeChatBoot.Core;

namespace WeChatBoot.App;

/// <summary>
/// 主界面。
///
/// 约定大于配置：界面上只有两件事要用户决定 ——「用哪个微信」和「开几个」。
/// 策略、间隔、超时、互斥量名全部走默认值（Auto + 自动发现），要改就改
/// wechatboot.config.json，界面不摆这些开关。
/// 有意做小：这是个常驻托盘边上的小工具，默认 660×600，可自由缩放。
/// </summary>
public sealed class MainForm : Form
{
    private readonly AppConfig _config;

    private Panel _body = new();
    private readonly Panel _header = new();
    private readonly Label _statusLabel = new();
    private readonly PillButton _settingsButton = new();
    private readonly PictureBox _logo = new();

    private readonly Card _actionCard = new();
    private readonly PickerField _productPicker = new();
    private readonly PillButton _rescanButton = new();
    private readonly PillButton _manualButton = new();
    private readonly Stepper _stepper = new();
    private readonly PillButton _launchButton = new();

    private readonly Card _instancesCard = new();
    private readonly Label _instancesTitle = new();
    private readonly PillButton _closeAllButton = new();
    private readonly FlowLayoutPanel _rows = new();
    private readonly Label _emptyHint = new();

    private readonly Card _logCard = new();
    private readonly TextBox _log = new();
    private readonly PillButton _logToggle = new();

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };

    private IReadOnlyList<WeChatProduct> _products = Array.Empty<WeChatProduct>();
    private bool _busy;
    private int _currentCount;
    private volatile bool _cancelRequested;
    private bool _logCollapsed = true;
    private string _rowSignature = string.Empty;

    private const int LogExpandedHeight = 126;
    private const int LogCollapsedHeight = 40;

    private static readonly object FileLogLock = new();

    /// <summary>文件日志跟配置文件放在一起（便携版就是 exe 同目录），用户容易找。</summary>
    private static string FileLogPath => Path.Combine(
        Path.GetDirectoryName(AppConfig.ConfigFilePath) ?? AppContext.BaseDirectory,
        "wechatboot.log");

    public MainForm()
    {
        _config = AppConfig.Load();

        Text = "微信多开启动器";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(
            Math.Clamp(_config.WindowWidth, 560, 1400),
            Math.Clamp(_config.WindowHeight, 480, 1200));
        MinimumSize = new Size(560, 470);
        BackColor = Theme.Bg;
        Font = Theme.BodyFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        DoubleBuffered = true;
        Icon = LoadAppIcon();

        BuildHeader();
        BuildBody();

        AcceptButton = _launchButton;
        LayoutShell();

        _timer.Tick += (_, _) => RefreshInstances();
        _timer.Start();

        Load += (_, _) =>
        {
            Scan(verbose: false);
            _stepper.Value = Math.Clamp(_config.InstanceCount, 1, 20);
            UpdateLaunchButton();
            RefreshInstances();
            SetStatus(IdleStatusText(), Theme.TextSecondary);
            Log($"{WeChatBootInfo.Name} {WeChatBootInfo.Version} 已就绪");

        };

        FormClosing += (_, _) => SaveConfig();
    }

    // ───────────────────────── 布局 ─────────────────────────

    private void BuildHeader()
    {
        _header.SetBounds(0, 0, ClientSize.Width, 60);
        _header.BackColor = Color.White;
        _header.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, _header.Height - 1, _header.Width, _header.Height - 1);
        };

        _logo.Location = new Point(18, 15);
        _logo.Size = new Size(30, 30);
        _logo.SizeMode = PictureBoxSizeMode.Zoom;
        _logo.BackColor = Color.Transparent;
        _logo.Image = Icon?.ToBitmap();

        var title = new Label
        {
            Text = "微信多开启动器",
            Font = Theme.HeaderFont,
            ForeColor = Theme.TextPrimary,
            BackColor = Color.White,
            AutoSize = true,
            Location = new Point(58, 19),
        };

        _statusLabel.AutoSize = false;
        _statusLabel.TextAlign = ContentAlignment.MiddleRight;
        _statusLabel.Font = Theme.SmallFont;
        _statusLabel.ForeColor = Theme.TextSecondary;
        _statusLabel.BackColor = Color.White;
        _statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _statusLabel.Location = new Point(ClientSize.Width - 428, 21);
        _statusLabel.Size = new Size(320, 18);

        _settingsButton.Text = "设置";
        _settingsButton.Kind = ButtonKind.Ghost;
        _settingsButton.Font = Theme.SmallFont;
        _settingsButton.Size = new Size(64, 28);
        _settingsButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _settingsButton.Location = new Point(ClientSize.Width - 88, 16);
        _settingsButton.Click += (_, _) => OpenSettings();

        _header.Controls.AddRange(new Control[] { _logo, title, _statusLabel, _settingsButton });
        Controls.Add(_header);
    }

    private void BuildBody()
    {
        var body = new Panel { BackColor = Theme.Bg };
        _body = body;

        BuildActionCard();
        BuildInstancesCard();
        BuildLogCard();

        body.Controls.AddRange(new Control[] { _actionCard, _instancesCard, _logCard });
        body.Resize += (_, _) => LayoutCards();

        Controls.Add(body);
        Resize += (_, _) => LayoutShell();
    }

    /// <summary>外壳：顶栏固定 60，其余全给 body。</summary>
    private void LayoutShell()
    {
        var width = ClientSize.Width;
        var height = ClientSize.Height;

        _header.Bounds = new Rectangle(0, 0, width, 60);
        _body.Bounds = new Rectangle(0, 60, width, Math.Max(0, height - 60));
        LayoutCards();
    }

    /// <summary>
    /// 三张卡片显式摆放：上（操作）、中（列表，吃掉剩余高度）、下（日志）。
    /// 不用 Dock 是因为 Fill + Top 的叠放顺序依赖 z 序，实测会互相盖住。
    /// </summary>
    private void LayoutCards()
    {
        const int pad = 16;
        const int gap = 10;
        const int topPad = 14;
        const int bottomPad = 14;

        var width = Math.Max(260, _body.ClientSize.Width - pad * 2);
        var height = _body.ClientSize.Height;
        var logHeight = _logCollapsed ? LogCollapsedHeight : LogExpandedHeight;

        _actionCard.Bounds = new Rectangle(pad, topPad, width, 112);

        var logTop = Math.Max(topPad + 112 + gap + 120, height - bottomPad - logHeight);
        _logCard.Bounds = new Rectangle(pad, logTop, width, logHeight);

        var instancesTop = topPad + 112 + gap;
        _instancesCard.Bounds = new Rectangle(
            pad,
            instancesTop,
            width,
            Math.Max(120, logTop - gap - instancesTop));
    }

    /// <summary>一张卡搞定「用哪个 + 开几个 + 启动」，两行排布，尽量占地方少。</summary>
    private void BuildActionCard()
    {
        _actionCard.SetBounds(0, 0, CardWidth(), 112);

        var productCaption = Caption("微信");
        productCaption.Location = new Point(16, 15);

        _productPicker.Location = new Point(52, 11);
        _productPicker.Size = new Size(_actionCard.Width - 52 - 16 - 148, 36);
        _productPicker.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _productPicker.SelectionChanged += (_, _) =>
        {
            _config.LastProductKey = ProductKeyOf(SelectedProduct());
            _config.RememberPath(SelectedProduct()?.ExecutablePath ?? string.Empty);
            Log($"已选择：{_productPicker.Selected?.Text}");
            RefreshInstances();
        };

        _manualButton.Text = "手动指定";
        _manualButton.Kind = ButtonKind.Ghost;
        _manualButton.Font = Theme.SmallFont;
        _manualButton.Size = new Size(76, 36);
        _manualButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _manualButton.Location = new Point(_actionCard.Width - 144, 11);
        _manualButton.Click += (_, _) => PickManually();

        _rescanButton.Text = "扫描";
        _rescanButton.Kind = ButtonKind.Ghost;
        _rescanButton.Font = Theme.SmallFont;
        _rescanButton.Size = new Size(60, 36);
        _rescanButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _rescanButton.Location = new Point(_actionCard.Width - 76, 11);
        _rescanButton.Click += (_, _) => Scan(verbose: true);

        var countCaption = Caption("数量");
        countCaption.Location = new Point(16, 71);

        _stepper.Location = new Point(52, 64);
        _stepper.Size = new Size(118, 36);
        _stepper.Min = 1;
        _stepper.Max = 20;
        _stepper.ValueChanged += (_, _) =>
        {
            _config.InstanceCount = _stepper.Value;
            UpdateLaunchButton();
        };

        var hint = new Label
        {
            Text = "含已打开的总数",
            Font = Theme.SmallFont,
            ForeColor = Theme.TextSecondary,
            BackColor = Color.White,
            AutoSize = true,
            Location = new Point(180, 74),
        };

        _launchButton.Kind = ButtonKind.Primary;
        _launchButton.Size = new Size(180, 40);
        _launchButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _launchButton.Location = new Point(_actionCard.Width - 196, 62);
        _launchButton.Click += async (_, _) => await LaunchAsync();

        _actionCard.Controls.AddRange(new Control[]
        {
            productCaption, _productPicker, _manualButton, _rescanButton,
            countCaption, _stepper, hint, _launchButton,
        });
    }

    private void BuildInstancesCard()
    {
        _instancesCard.SetBounds(0, 0, CardWidth(), 320);
        _instancesCard.Padding = new Padding(0);

        _instancesTitle.Text = "已经打开";
        _instancesTitle.Font = Theme.BodyBold;
        _instancesTitle.ForeColor = Theme.TextPrimary;
        _instancesTitle.BackColor = Color.White;
        _instancesTitle.AutoSize = true;
        _instancesTitle.Location = new Point(16, 14);

        _closeAllButton.Text = "全部关闭";
        _closeAllButton.Kind = ButtonKind.Danger;
        _closeAllButton.Font = Theme.SmallFont;
        _closeAllButton.Size = new Size(80, 26);
        _closeAllButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _closeAllButton.Location = new Point(_instancesCard.Width - 96, 10);
        _closeAllButton.Click += async (_, _) => await CloseAllAsync();

        _rows.Dock = DockStyle.Fill;
        _rows.FlowDirection = FlowDirection.TopDown;
        _rows.WrapContents = false;
        _rows.AutoScroll = true;
        _rows.BackColor = Color.White;
        _rows.Padding = new Padding(12, 42, 12, 10);

        _emptyHint.Text = "还没有打开微信\r\n点上面的绿色按钮就行，不用改设置";
        _emptyHint.Font = Theme.BodyFont;
        _emptyHint.ForeColor = Theme.TextSecondary;
        _emptyHint.TextAlign = ContentAlignment.MiddleCenter;
        _emptyHint.Dock = DockStyle.Fill;
        _emptyHint.BackColor = Color.White;

        var host = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        host.Controls.Add(_rows);
        host.Controls.Add(_emptyHint);
        _emptyHint.BringToFront();

        _instancesCard.Controls.AddRange(new Control[] { host, _instancesTitle, _closeAllButton });
        host.BringToFront();
    }

    private void BuildLogCard()
    {
        _logCard.SetBounds(0, 0, CardWidth(), LogCollapsedHeight);

        var caption = new Label
        {
            Text = "详细信息",
            Font = Theme.SmallFont,
            ForeColor = Theme.TextSecondary,
            BackColor = Color.White,
            AutoSize = true,
            Location = new Point(16, 11),
        };

        _logToggle.Text = "展开";
        _logToggle.Kind = ButtonKind.Ghost;
        _logToggle.Font = Theme.SmallFont;
        _logToggle.Size = new Size(56, 24);
        _logToggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _logToggle.Location = new Point(_logCard.Width - 72, 8);
        _logToggle.Click += (_, _) => ToggleLog();

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BorderStyle = BorderStyle.None;
        _log.BackColor = Color.FromArgb(0xFA, 0xFB, 0xFD);
        _log.ForeColor = Theme.TextSecondary;
        _log.Font = Theme.LogFont;
        _log.Dock = DockStyle.Fill;
        _log.TabStop = false;
        _log.Visible = !_logCollapsed;

        var logHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 38, 14, 12), BackColor = Color.White };
        logHost.Controls.Add(_log);
        logHost.Controls.Add(caption);
        _logCard.Controls.Add(logHost);
    }

    private int CardWidth() => Math.Max(320, ClientSize.Width - 32);

    private static Label Caption(string text) => new()
    {
        Text = text,
        Font = Theme.SmallFont,
        ForeColor = Theme.TextSecondary,
        BackColor = Color.White,
        AutoSize = true,
    };

    // ───────────────────────── 行为 ─────────────────────────

    private WeChatProduct? SelectedProduct() => _productPicker.Selected?.Tag as WeChatProduct;

    private static string? ProductKeyOf(WeChatProduct? p) => p is null ? null : $"{p.Key}:{p.ExecutablePath}";

    private string IdleStatusText()
    {
        var instances = InstanceManager.GetInstances().Count;
        if (_products.Count == 0) return "没找到微信";

        var installs = _products.Count == 1 ? "1 个微信安装" : $"{_products.Count} 个微信安装";
        return instances == 0 ? $"就绪 · {installs}" : $"就绪 · {installs} · 已开 {instances} 个";
    }

    private void SetStatus(string text, Color color)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetStatus(text, color)));
            return;
        }

        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

    private void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => Log(message)));
            return;
        }

        // 界面日志只留最近一段：开几十个实例后无限追加会让 TextBox 越来越卡
        if (_log.TextLength > 60000)
        {
            _log.Text = _log.Text[^30000..];
        }

        _log.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();

        AppendToFile(message);
    }

    /// <summary>
    /// 文件日志和界面日志同源：用户在群里喊「不好使」的时候，直接让他把 wechatboot.log 发过来，
    /// 不用再靠截图猜。超过 512KB 就从新文件开始，免得日志长成几十兆。
    /// </summary>
    private static void AppendToFile(string message)
    {
        try
        {
            lock (FileLogLock)
            {
                var info = new FileInfo(FileLogPath);
                if (info.Exists && info.Length > 512 * 1024) info.Delete();

                File.AppendAllText(
                    FileLogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 目录只读之类的写不进去就算了，日志不该影响开微信这件正事
        }
    }

    private void Scan(bool verbose)
    {
        if (verbose) Log("重新扫描本机微信…");

        _products = WeChatLocator.DetectAll(_config, verbose ? Log : null);

        var previous = SelectedProduct()?.ExecutablePath;
        var items = _products.Select(p => ((string Text, object? Tag))(p.ToDisplayText(), p)).ToList();

        var select = 0;
        var list = _products.ToList();
        if (!string.IsNullOrEmpty(previous))
        {
            var index = list.FindIndex(p => string.Equals(p.ExecutablePath, previous, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) select = index;
        }
        else if (!string.IsNullOrEmpty(_config.LastProductKey))
        {
            var index = list.FindIndex(p => ProductKeyOf(p) == _config.LastProductKey);
            if (index >= 0) select = index;
        }

        _productPicker.SetItems(items, select);
        _productPicker.DropDownEnabled = items.Count > 0;

        // SetItems 是直接改索引、不触发 SelectionChanged 的，这里显式同步一次。
        // 不同步就会留下「配置里记 A、界面选中 B」的暗坑：等下次启动按配置开，
        // 开的和用户以为的不是同一个版本。
        var chosen = SelectedProduct();
        if (chosen is not null)
        {
            _config.LastProductKey = ProductKeyOf(chosen);
            if (!string.IsNullOrEmpty(previous) &&
                !string.Equals(chosen.ExecutablePath, previous, StringComparison.OrdinalIgnoreCase))
            {
                Log($"原来选的安装找不到了，已切换到 {chosen.ToDisplayText()}");
            }
        }

        if (items.Count == 0)
        {
            SetStatus("没找到微信，点「手动指定」", Theme.Warn);
            Log("没有自动找到微信，请点「手动指定」选择 Weixin.exe / WeChat.exe");
        }
        else
        {
            Log($"识别到 {items.Count} 个微信安装");
            SetStatus(IdleStatusText(), Theme.TextSecondary);
        }

        UpdateLaunchButton();
        RefreshInstances();
    }

    private void UpdateLaunchButton()
    {
        _launchButton.Enabled = _busy || SelectedProduct() is not null;

        // 按钮说的是「还要开几个」，不是「总共几个」—— 已经开着的时候别让人以为点了没反应。
        // 正在开的时候变成「停止」：中途反悔不用等它把 20 次都试完。
        var delta = _stepper.Value - _currentCount;
        _launchButton.Text = _busy
            ? (_cancelRequested ? "正在停止…" : "停止")
            : delta > 0 ? $"打开 {delta} 个微信" : "已经开着，不用重复打开";
        _launchButton.Invalidate();

        _stepper.Enabled = !_busy;
        _productPicker.Enabled = !_busy;
        _rescanButton.Enabled = !_busy;
        _manualButton.Enabled = !_busy;
    }

    private void OpenSettings()
    {
        using var dialog = new SettingsForm(_config);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        Log($"设置已保存：{_config.Strategy} · 间隔 {_config.StaggerMs}ms · 超时 {_config.VerifyTimeoutMs}ms");
        Scan(verbose: false);
        SetStatus("设置已保存", Theme.AccentDark);
    }

    private void PickManually()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择微信主程序",
            Filter = "微信主程序|Weixin.exe;WeChat.exe|可执行文件|*.exe",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _config.RememberPath(dialog.FileName);
        _config.LastProductKey = null;
        Log($"手动指定：{dialog.FileName}");
        Scan(verbose: true);
    }

    private async Task LaunchAsync()
    {
        // 正在开的时候按钮变成「停止」，再点一次就是取消，不再继续开
        if (_busy)
        {
            _cancelRequested = true;
            Log("收到停止请求，这一轮开完就停…");
            SetStatus("正在停止…", Theme.TextSecondary);
            return;
        }

        var product = SelectedProduct();
        Log("收到启动请求");
        if (product is null)
        {
            SetStatus("先选一个微信", Theme.Warn);
            return;
        }

        var target = _stepper.Value;
        var before = Launcher.InstancesOf(product.ExecutablePath).Count;
        _cancelRequested = false;
        _busy = true;
        UpdateLaunchButton();
        SetStatus("正在启动…", Theme.TextSecondary);
        Log($"目标 {target} 个：{product.ToDisplayText()}");

        var ok = false;
        try
        {
            ok = await Task.Run(() => RunLaunch(product, target));
        }
        catch (Exception ex)
        {
            Log($"启动出错了：{ex.Message}");
        }
        finally
        {
            _busy = false;
            UpdateLaunchButton();
            RefreshInstances();
        }

        var now = Launcher.InstancesOf(product.ExecutablePath).Count;
        var opened = Math.Max(0, now - before);

        if (_cancelRequested)
        {
            SetStatus($"已停止，这次新开了 {opened} 个（一共 {now} 个）", Theme.TextSecondary);
            Log($"已停止：新开 {opened} 个，一共 {now} 个。");
        }
        else if (ok && opened > 0)
        {
            SetStatus($"✓ 新开了 {opened} 个，现在一共 {now} 个，扫码登录就行", Theme.AccentDark);
            Log($"完成：新开 {opened} 个，一共 {now} 个。");
        }
        else if (ok)
        {
            SetStatus($"已经开了 {now} 个，不用重复打开", Theme.TextSecondary);
            Log("完成：数量已达标，没有新开。");
        }
        else if (now > 0)
        {
            SetStatus($"只新开了 {opened} 个（一共 {now} 个），可能到上限了", Theme.Warn);
        }
        else
        {
            SetStatus("没能打开，展开「详细信息」看看原因", Theme.Warn);
        }
    }

    /// <summary>
    /// 按一次只负责把「点下去那一刻还差的个数」开出来。
    ///
    /// 关键：循环条件用「已经成功开了几个」，不是「现在总共有几个」。
    /// 用总数当条件会有个气人的后果——用户中途手动关掉一个窗口，程序看到数量不够，
    /// 就又把那个位置补上（实测就是这样）。用户关窗口是他自己的决定，工具不替他纠偏。
    ///
    /// 守卫必须活着覆盖整个过程：deny DACL 依附在内核对象上，守卫一退出、对象没人持有就会被销毁。
    /// </summary>
    private bool RunLaunch(WeChatProduct product, int target)
    {
        var exe = product.ExecutablePath;
        using var guard = new MutexGuard();

        var current = Launcher.InstancesOf(exe).Count;
        var need = Math.Max(0, target - current);
        if (need == 0)
        {
            Log($"已经有 {current} 个，不用再开");
            return true;
        }

        Log($"现在 {current} 个，这次要新开 {need} 个");
        var opened = 0;

        for (var i = 0; i < need; i++)
        {
            if (_cancelRequested) break;

            var done = false;

            // 一个实例最多试两次：被单实例机制折叠时重试一次就够，不无限重试
            for (var attempt = 1; attempt <= 2 && !done && !_cancelRequested; attempt++)
            {
                Log($"开第 {current + opened + 1} 个（第 {attempt} 次尝试）");
                var outcome = Launcher.Launch(
                    exe,
                    _config.Strategy,
                    guard,
                    _config.VerifyTimeoutMs,
                    Log,
                    _config.ExtraMutexNames);

                done = outcome.Success;
                if (!done)
                {
                    Log($"没成功：{outcome.Message}");
                    Log(FriendlyReason(outcome.Message));
                }
            }

            if (!done) break;

            opened++;
            if (i < need - 1)
            {
                Thread.Sleep(Math.Clamp(_config.StaggerMs, 200, 5000));
            }
        }

        Log($"这一轮结束：新开 {opened} 个（目标 {need} 个）");
        return opened >= need;
    }

    /// <summary>把内核层的失败原因翻译成小白能照做的一句话。</summary>
    private static readonly Dictionary<string, string> VersionCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>读 exe 的文件版本，读不到就当没有（不为了个版本号让行建不出来）。</summary>
    private static string ProductVersionOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        lock (VersionCache)
        {
            if (VersionCache.TryGetValue(path, out var cached)) return cached;
        }

        var version = string.Empty;
        try
        {
            version = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion ?? string.Empty;
        }
        catch
        {
            // 路径没了/权限不够都当没有版本
        }

        lock (VersionCache)
        {
            VersionCache[path] = version;
        }

        return version;
    }

    private static string FriendlyReason(string message)
    {
        if (message.Contains("折叠", StringComparison.Ordinal))
            return "提示：这个微信版本可能限制多开次数。可以先关掉全部微信，再点一次打开。";
        if (message.Contains("环境异常", StringComparison.Ordinal))
            return "提示：微信检测到了改动。请把微信升级到官方最新版后重试。";
        if (message.Contains("超时", StringComparison.Ordinal))
            return "提示：微信启动得比平时慢，等一两秒再看看列表。";
        return "提示：先关掉全部微信再试一次，通常就好了。";
    }

    private async Task CloseAllAsync()
    {
        if (_busy) return;

        var count = InstanceManager.GetInstances().Count;
        if (count == 0)
        {
            SetStatus("现在没有开着的微信", Theme.TextSecondary);
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"要关闭全部 {count} 个微信吗？\r\n没发出去的消息会丢，聊天记录不受影响。",
            "全部关闭",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.OK) return;

        _busy = true;
        UpdateLaunchButton();
        SetStatus("正在关闭…", Theme.TextSecondary);

        try
        {
            await Task.Run(() => Launcher.CloseAll(Log));
        }
        finally
        {
            _busy = false;
            UpdateLaunchButton();
            RefreshInstances();
        }

        SetStatus("已全部关闭", Theme.TextSecondary);
    }

    private void ToggleLog()
    {
        _logCollapsed = !_logCollapsed;
        _logCard.Height = _logCollapsed ? LogCollapsedHeight : LogExpandedHeight;
        _log.Visible = !_logCollapsed;
        _logToggle.Text = _logCollapsed ? "展开" : "收起";
        _logToggle.Invalidate();
        LayoutCards();
    }

    private void RefreshInstances()
    {
        var instances = InstanceManager.GetInstances();

        var selected = SelectedProduct();
        _currentCount = selected is null ? 0 : Launcher.InstancesOf(selected.ExecutablePath).Count;
        UpdateLaunchButton();

        var signature = string.Join(",", instances.Select(i => i.Pid)) + "#" + _instancesCard.Width;

        _instancesTitle.Text = instances.Count == 0 ? "已经打开" : $"已经打开 {instances.Count} 个";

        _emptyHint.Visible = instances.Count == 0;
        _rows.Visible = instances.Count > 0;
        _closeAllButton.Enabled = instances.Count > 0;

        if (signature == _rowSignature) return;
        _rowSignature = signature;

        _rows.SuspendLayout();
        foreach (Control control in _rows.Controls)
        {
            control.Dispose();
        }

        _rows.Controls.Clear();

        var width = Math.Max(260, _rows.ClientSize.Width - _rows.Padding.Horizontal - 20);
        var index = 1;

        foreach (var instance in instances)
        {
            var started = instance.StartTime is null
                ? "正在启动"
                : $"已经开了 {FormatDuration(DateTime.Now - instance.StartTime.Value)}";

            var version = ProductVersionOf(instance.ImagePath);
            var meta = version.Length > 0 ? $"{version} · {started}" : started;
            _rows.Controls.Add(new InstanceRow(index++, instance.Pid, instance.Title, meta, instance.ImagePath ?? "")
            {
                Width = width,
            });
        }

        _rows.ResumeLayout();

        if (!_busy)
        {
            SetStatus(IdleStatusText(), Theme.TextSecondary);
        }
    }

    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return $"{Math.Max(1, (int)span.TotalSeconds)} 秒";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟";
        return $"{(int)span.TotalHours} 小时 {(int)span.Minutes} 分";
    }

    private void SaveConfig()
    {
        _config.InstanceCount = _stepper.Value;
        _config.LastProductKey = ProductKeyOf(SelectedProduct());

        if (WindowState == FormWindowState.Normal)
        {
            _config.WindowWidth = Width;
            _config.WindowHeight = Height;
        }

        if (!_config.Save(out var error))
        {
            Log($"配置没能保存：{error}");
        }
    }

    private static Icon? LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            return string.IsNullOrEmpty(exe) ? null : Icon.ExtractAssociatedIcon(exe);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>程序名与版本（避免多处手写）。</summary>
internal static class WeChatBootInfo
{
    public static string Name => "微信多开启动器";

    public static string Version
    {
        get
        {
            var v = typeof(WeChatBootInfo).Assembly.GetName().Version;

            // 报 bug 的时候版本号要说全，1.1 和 1.1.1 是两次发布
            if (v is null) return "v1.0";
            return v.Build > 0 ? $"v{v.Major}.{v.Minor}.{v.Build}" : $"v{v.Major}.{v.Minor}";
        }
    }
}
