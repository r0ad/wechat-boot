using System.Text;
using WeChatBoot.Core;

namespace WeChatBoot.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 小工具没有崩溃上报，出问题时至少留下可读的一行，别让用户对着消失的窗口发呆。
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog("AppDomain", e.ExceptionObject as Exception);
        Application.ThreadException += (_, e) => CrashLog("UI线程", e.Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        try
        {
            ApplicationConfiguration.Initialize();

            // --settings 只用于开发和截图：直接把设置窗口当主窗口打开
            if (args.Any(a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase)))
            {
                Application.Run(new SettingsForm(AppConfig.Load()));
                return;
            }

            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            CrashLog("启动", ex);
            throw;
        }
    }

    private static void CrashLog(string where, Exception? ex)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "wechatboot-crash.log");
            var text = new StringBuilder()
                .AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}")
                .AppendLine(ex?.ToString() ?? "未知异常")
                .AppendLine()
                .ToString();
            File.AppendAllText(path, text, Encoding.UTF8);
        }
        catch
        {
            // 写日志失败就算了，不能因为日志把程序搞死
        }
    }
}
