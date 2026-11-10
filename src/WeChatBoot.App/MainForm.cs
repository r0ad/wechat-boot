using System.Diagnostics;
using System.Drawing;
using WeChatBoot.Core;

namespace WeChatBoot.App;

/// <summary>
/// 主窗体。刻意不用设计器：整个界面就是一个可读的 C# 文件，
/// 想改字号、加按钮直接改代码即可（需求里要的“轻量编辑”）。
/// </summary>
public sealed class MainForm : Form
{
    private readonly AppConfig _config = AppConfig.Load();
    private readonly List<WeChatProduct> _products = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new();

    private ComboBox _productBox = null!;
    private NumericUpDown _countBox = null!;
    private ComboBox _strategyBox = null!;
    private Button _launchButton = null!;
    private Button _closeAllButton = null!;
    private Button _rescanButton = null!;
    private ListView _instanceList = null!;
    private TextBox _logBox = null!;
    private Label _statusLabel = null!;
    private bool _busy;

    public MainForm()
    {
        BuildUi();
        LoadProducts();
        RefreshInstances();

        _refreshTimer.Interval = 2000;
        _refreshTimer.Tick += (_, _) => RefreshInstances();
        _refreshTimer.Start();

        Log($"配置：{AppConfig.ConfigFilePath}");
    }

    // ───────────────────────── 界面 ─────────────────────────

    private void BuildUi()
    {
        Text = "微信多开启动器";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(920, 640);
        MinimumSize = new Size(780, 560);
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;

        var header = new Panel { Dock = DockStyle.Top, Height = 100, Padding = new Padding(12, 10, 12, 10) };

        var productLabel = new Label { Text = "微信程序", AutoSize = true, Location = new Point(12, 16) };
        _productBox = new ComboBox
        {
            Location = new Point(78, 12),
            Width = 470,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        var pickButton = new Button { Text = "手动指定…", Location = new Point(556, 11), Size = new Size(96, 26), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        pickButton.Click += (_, _) => PickExecutable();

        _rescanButton = new Button { Text = "重新扫描", Location = new Point(658, 11), Size = new Size(92, 26), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _rescanButton.Click += (_, _) => LoadProducts();

        var countLabel = new Label { Text = "实例数", AutoSize = true, Location = new Point(12, 58) };
        _countBox = new NumericUpDown
        {
            Location = new Point(66, 54),
            Width = 60,
            Minimum = 1,
            Maximum = 20,
            Value = Math.Clamp(_config.InstanceCount, 1, 20),
        };

        var strategyLabel = new Label { Text = "方式", AutoSize = true, Location = new Point(142, 58) };
        _strategyBox = new ComboBox
        {
            Location = new Point(182, 54),
            Width = 190,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _strategyBox.Items.AddRange(new object[]
        {
            new StrategyItem(LaunchStrategy.Auto, "自动（推荐）"),
            new StrategyItem(LaunchStrategy.MutexAcl, "互斥量 ACL"),
            new StrategyItem(LaunchStrategy.CloseHandle, "关闭句柄"),
            new StrategyItem(LaunchStrategy.Direct, "直接启动（诊断）"),
        });
        _strategyBox.SelectedIndex = _strategyBox.Items.Cast<StrategyItem>()
            .Select((item, index) => (item, index))
            .FirstOrDefault(pair => pair.item.Value == _config.Strategy).index;

        _launchButton = new Button
        {
            Text = "启动",
            Location = new Point(390, 52),
            Size = new Size(110, 30),
            BackColor = Color.FromArgb(7, 193, 96),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        _launchButton.FlatAppearance.BorderSize = 0;
        _launchButton.Click += OnLaunchClick;

        _closeAllButton = new Button { Text = "全部关闭", Location = new Point(510, 52), Size = new Size(100, 30) };
        _closeAllButton.Click += OnCloseAllClick;

        _statusLabel = new Label
        {
            AutoSize = true,
            Location = new Point(622, 58),
            ForeColor = Color.DimGray,
            Text = "就绪",
        };

        header.Controls.AddRange(new Control[]
        {
            productLabel, _productBox, pickButton, _rescanButton,
            countLabel, _countBox, strategyLabel, _strategyBox,
            _launchButton, _closeAllButton, _statusLabel,
        });

        var logPanel = new Panel { Dock = DockStyle.Bottom, Height = 170, Padding = new Padding(12, 0, 12, 10) };
        var logTitle = new Label { Text = "运行日志", Dock = DockStyle.Top, Height = 22, ForeColor = Color.DimGray };
        _logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            BackColor = Color.FromArgb(250, 250, 250),
            Font = new Font("Consolas", 9F),
        };
        logPanel.Controls.Add(_logBox);
        logPanel.Controls.Add(logTitle);

        var listPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0) };
        _instanceList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            HideSelection = false,
            MultiSelect = false,
        };
        _instanceList.Columns.Add("PID", 70);
        _instanceList.Columns.Add("窗口标题", 150);
        _instanceList.Columns.Add("窗口类", 210);
        _instanceList.Columns.Add("程序路径", 420);
        listPanel.Controls.Add(_instanceList);

        Controls.Add(listPanel);
        Controls.Add(logPanel);
        Controls.Add(header);

        FormClosing += (_, _) => SaveConfig();
    }

    // ───────────────────────── 产品列表 ─────────────────────────

    private void LoadProducts()
    {
        try
        {
            _products.Clear();
            _products.AddRange(WeChatLocator.DetectAll(_config, Log));

            // 手动维护过的路径即便当前没在扫描结果里，也要保留（用户可能在改完目录后先手动指定）
            foreach (var path in _config.RecentPaths)
            {
                if (!File.Exists(path)) continue;
                if (_products.Any(p => string.Equals(p.ExecutablePath, path, StringComparison.OrdinalIgnoreCase))) continue;
                _products.Add(Describe(path, "手动指定"));
            }

            var previous = (_productBox.SelectedItem as WeChatProduct)?.ExecutablePath ?? _config.ExecutablePath;

            _productBox.Items.Clear();
            foreach (var product in _products) _productBox.Items.Add(product);

            if (_products.Count == 0)
            {
                _statusLabel.Text = "未找到微信，请用“手动指定…”";
                return;
            }

            var index = previous is null
                ? 0
                : Math.Max(0, _products.FindIndex(p => string.Equals(p.ExecutablePath, previous, StringComparison.OrdinalIgnoreCase)));
            _productBox.SelectedIndex = index;
            _statusLabel.Text = $"已识别 {_products.Count} 个微信";
        }
        catch (Exception ex)
        {
            Log($"扫描微信失败：{ex.Message}");
        }
    }

    private void PickExecutable()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择微信主程序（Weixin.exe / WeChat.exe）",
            Filter = "微信主程序|Weixin.exe;WeChat.exe|可执行文件|*.exe|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _config.RememberPath(dialog.FileName);
        _config.ExecutablePath = dialog.FileName;

        var product = Describe(dialog.FileName, "手动指定");
        _products.RemoveAll(p => string.Equals(p.ExecutablePath, product.ExecutablePath, StringComparison.OrdinalIgnoreCase));
        _products.Insert(0, product);

        _productBox.Items.Clear();
        foreach (var item in _products) _productBox.Items.Add(item);
        _productBox.SelectedIndex = 0;

        Log($"已手动指定：{dialog.FileName}");
        SaveConfig();
    }

    private static WeChatProduct Describe(string path, string source)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        string? version = null;
        try { version = FileVersionInfo.GetVersionInfo(path).FileVersion; } catch { /* 版本不重要 */ }

        return new WeChatProduct
        {
            Key = name,
            DisplayName = name.Equals("Weixin", StringComparison.OrdinalIgnoreCase) ? "微信 4.x" : "微信 3.x",
            ExecutablePath = path,
            Version = version,
            Source = source,
        };
    }

    // ───────────────────────── 启动 ─────────────────────────

    private async void OnLaunchClick(object? sender, EventArgs e)
    {
        if (_busy) return;

        var product = _productBox.SelectedItem as WeChatProduct;
        if (product is null || !File.Exists(product.ExecutablePath))
        {
            MessageBox.Show(this, "请先选择一个有效的微信程序。", "微信多开启动器", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var strategy = ((StrategyItem)_strategyBox.SelectedItem!).Value;
        var count = (int)_countBox.Value;

        _config.InstanceCount = count;
        _config.Strategy = strategy;
        _config.ExecutablePath = product.ExecutablePath;
        _config.LastProductKey = product.Key;
        SaveConfig();

        _busy = true;
        _launchButton.Enabled = false;
        _rescanButton.Enabled = false;
        _statusLabel.Text = "正在启动…";
        Log($"====== 开始启动：{Path.GetFileName(product.ExecutablePath)} × {count}（方式：{_strategyBox.SelectedItem}）======");

        var exe = product.ExecutablePath;
        var stagger = _config.StaggerMs;
        var timeout = _config.VerifyTimeoutMs;
        var extra = _config.ExtraMutexNames.ToList();

        try
        {
            var summary = await Task.Run(() =>
            {
                // 守卫在整轮启动期间保持存活：ACL 只有在有进程持句柄时才持续生效
                using var guard = new MutexGuard();
                var ok = 0;
                for (var i = 0; i < count; i++)
                {
                    if (i > 0 && stagger > 0) Thread.Sleep(stagger);

                    var outcome = Launcher.Launch(exe, strategy, guard, timeout, message => Log("  " + message), extra);
                    if (outcome.Success) ok++;
                    Log($"[{i + 1}/{count}] {(outcome.Success ? "成功" : "失败")}：{outcome.Message}");
                }

                return (ok, total: count, detail: guard.Detail);
            });

            Log($"====== 结束：成功 {summary.ok}/{summary.total}，布防={summary.detail} ======");
            _statusLabel.Text = summary.ok > 0 ? $"成功 {summary.ok}/{summary.total}" : "未能多开，请查看日志";
        }
        catch (Exception ex)
        {
            Log($"启动异常：{ex.Message}");
            _statusLabel.Text = "启动异常";
        }
        finally
        {
            _busy = false;
            _launchButton.Enabled = true;
            _rescanButton.Enabled = true;
            RefreshInstances();
        }
    }

    private void OnCloseAllClick(object? sender, EventArgs e)
    {
        if (_busy) return;
        if (MessageBox.Show(this, "结束所有微信相关进程？未发送的消息会留在服务器上。", "确认",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

        var killed = Launcher.CloseAll(Log);
        Log($"已结束 {killed} 个微信相关进程");
        RefreshInstances();
    }

    // ───────────────────────── 刷新与日志 ─────────────────────────

    private void RefreshInstances()
    {
        if (IsDisposed) return;

        try
        {
            var instances = InstanceManager.GetInstances();
            var selected = _instanceList.SelectedItems.Count > 0 ? _instanceList.SelectedItems[0].Text : null;

            _instanceList.BeginUpdate();
            _instanceList.Items.Clear();
            foreach (var instance in instances)
            {
                var item = new ListViewItem(instance.Pid.ToString());
                item.SubItems.Add(instance.Title);
                item.SubItems.Add(instance.ClassName);
                item.SubItems.Add(instance.ImagePath ?? instance.ProcessName);
                _instanceList.Items.Add(item);
                if (item.Text == selected) item.Selected = true;
            }
            _instanceList.EndUpdate();

            if (!_busy) _statusLabel.Text = $"当前实例 {instances.Count} 个";
        }
        catch (Exception ex)
        {
            Log($"刷新实例失败：{ex.Message}");
        }
    }

    private void Log(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action<string>(Log), message); } catch (InvalidOperationException) { /* 窗体正在关闭 */ }
            return;
        }

        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    private void SaveConfig()
    {
        if (!_config.Save(out var error)) Log($"配置保存失败：{error}");
    }

    private sealed record StrategyItem(LaunchStrategy Value, string Text)
    {
        public override string ToString() => Text;
    }
}
