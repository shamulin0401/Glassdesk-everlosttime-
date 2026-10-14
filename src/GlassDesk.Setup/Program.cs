using System.IO;
using System.Windows;

namespace GlassDesk.Setup;

/// <summary>
/// 入口点：默认显示图形安装向导；带静默参数时走无界面路径（供自动化验证与无人值守安装）。
/// </summary>
/// <remarks>
/// 静默模式不往控制台输出——本程序是 <c>WinExe</c>，没有控制台可写；它把过程写进
/// <c>%TEMP%\GlassDesk-Setup.log</c>，验证脚本读这个文件与文件系统/注册表来判断结果。
///
/// <para>
/// 用法：<c>GlassDesk-1.0-Setup.exe [--dir=&lt;路径&gt;] [--desktop] [--run] [--silent]</c>；
/// 卸载：<c>--uninstall [--silent]</c>。
/// </para>
/// </remarks>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var uninstall = Has(args, "--uninstall");
        var silent = Has(args, "--silent");
        var target = ResolveTarget(args);

        if (silent)
        {
            return RunSilent(uninstall, target, Has(args, "--desktop"), Has(args, "--run"));
        }

        var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        return application.Run(new InstallerWindow(uninstall, target));
    }

    private static int RunSilent(bool uninstall, string target, bool desktop, bool run)
    {
        var log = Path.Combine(Path.GetTempPath(), "GlassDesk-Setup.log");

        void Write(string message)
        {
            File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
        }

        try
        {
            File.WriteAllText(log, string.Empty);
            Write(uninstall ? $"卸载：{target}" : $"安装：{target}");

            var engine = new SetupEngine(Write);

            if (uninstall)
            {
                _ = engine.Uninstall(target);
            }
            else
            {
                _ = engine.Install(target, desktop);

                if (run)
                {
                    using var process = System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(SetupEngine.ExecutablePath(target))
                        {
                            WorkingDirectory = target,
                            UseShellExecute = true,
                        });
                }
            }

            Write("结果：成功");
            return 0;
        }
        catch (Exception ex)
        {
            Write($"结果：失败 — {ex.Message}");
            return 1;
        }
    }

    private static string ResolveTarget(string[] args)
    {
        foreach (var argument in args)
        {
            if (argument.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(argument["--dir=".Length..]);
            }
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("GLASSDESK_SETUP_DIR");

        return string.IsNullOrWhiteSpace(fromEnvironment)
            ? SetupEngine.DefaultTarget
            : Path.GetFullPath(fromEnvironment);
    }

    private static bool Has(string[] args, string flag)
        => Array.Exists(args, argument => string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase));
}
