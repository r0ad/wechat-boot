using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeChatBoot.Core;

/// <summary>
/// 本地配置。存放位置优先用 exe 同目录（便携），目录不可写时退回 %APPDATA%。
/// 手动维护的路径、实例数、策略都会持久化，下次打开即用。
/// </summary>
public sealed class AppConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>手动指定的微信主程序路径（为空则自动寻址）。</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>上次选择的产品：Weixin / WeChat。</summary>
    public string? LastProductKey { get; set; }

    /// <summary>一次点击启动几个实例。</summary>
    public int InstanceCount { get; set; } = 2;

    /// <summary>多开策略。</summary>
    public LaunchStrategy Strategy { get; set; } = LaunchStrategy.Auto;

    /// <summary>实例之间的间隔（毫秒），太快容易互相干扰。</summary>
    public int StaggerMs { get; set; } = 1200;

    /// <summary>等待新实例主窗口出现的超时（毫秒）。</summary>
    public int VerifyTimeoutMs { get; set; } = 6000;

    /// <summary>窗口宽度（记住上次大小）。</summary>
    public int WindowWidth { get; set; } = 660;

    /// <summary>窗口高度（记住上次大小）。</summary>
    public int WindowHeight { get; set; } = 600;

    /// <summary>高级：额外需要布防的互斥量名（一般留空）。</summary>
    public List<string> ExtraMutexNames { get; set; } = new();

    /// <summary>最近使用过的路径（手动维护的候选列表）。</summary>
    public List<string> RecentPaths { get; set; } = new();

    /// <summary>配置文件实际路径。</summary>
    [JsonIgnore]
    public static string ConfigFilePath { get; private set; } = DefaultPortablePath();

    private static string DefaultPortablePath() =>
        Path.Combine(AppContext.BaseDirectory, "wechatboot.config.json");

    private static string RoamingPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WeChatBoot", "config.json");

    public static AppConfig Load()
    {
        ConfigFilePath = DefaultPortablePath();

        foreach (var path in new[] { ConfigFilePath, RoamingPath() })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
                ConfigFilePath = path;
                cfg.Normalize();
                return cfg;
            }
            catch
            {
                // 配置损坏不应阻断启动，继续尝试下一个位置
            }
        }

        var fresh = new AppConfig();
        fresh.Normalize();
        return fresh;
    }

    public bool Save(out string? error)
    {
        error = null;

        foreach (var path in new[] { DefaultPortablePath(), RoamingPath() })
        {
            try
            {
                Normalize();
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
                ConfigFilePath = path;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }

        return false;
    }

    private void Normalize()
    {
        InstanceCount = Math.Clamp(InstanceCount, 1, 20);
        StaggerMs = Math.Clamp(StaggerMs, 0, 30000);
        VerifyTimeoutMs = Math.Clamp(VerifyTimeoutMs, 1000, 60000);

        RecentPaths ??= new List<string>();
        ExtraMutexNames ??= new List<string>();

        RecentPaths.RemoveAll(string.IsNullOrWhiteSpace);
        ExtraMutexNames.RemoveAll(string.IsNullOrWhiteSpace);

        // 去重 + 只保留仍然存在的路径，避免列表越用越长
        var dedup = new List<string>();
        foreach (var p in RecentPaths)
        {
            if (!File.Exists(p)) continue;
            if (dedup.Contains(p, StringComparer.OrdinalIgnoreCase)) continue;
            dedup.Add(p);
        }

        RecentPaths = dedup.Take(8).ToList();
    }

    /// <summary>记住一个路径（手动维护列表）。</summary>
    public void RememberPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        RecentPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentPaths.Insert(0, path);
        RecentPaths = RecentPaths.Take(8).ToList();
    }
}
