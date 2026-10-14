using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using Microsoft.Win32;

namespace GlassDesk.Setup;

/// <summary>
/// 安装/卸载的实际动作。与界面无关，界面与静默模式共用同一份逻辑。
/// </summary>
/// <remarks>
/// 布局与 <c>packaging\GlassDesk-Capture.iss</c> 保持一致：用户级（不弹 UAC）、
/// 装到 <c>%LOCALAPPDATA%\Programs\GlassDesk</c>、开始菜单快捷方式必建、
/// 桌面快捷方式可选、卸载信息写在 <c>HKCU\...\Uninstall\GlassDesk.Independent_is1</c>。
/// 载荷（<c>payload.zip</c>）内嵌在本 exe 里，所以安装包是单个文件。
/// </remarks>
internal sealed class SetupEngine
{
    private const string AppId = "GlassDesk.Independent_is1";
    private const string DisplayName = "GlassDesk";
    private const string Publisher = "GlassDesk";
    private const string PayloadResource = "payload.zip";
    private const string ShortcutName = "GlassDesk.lnk";
    private const string MainExecutable = "GlassDesk.exe";

    internal SetupEngine(Action<string> report) => Report = report;

    private Action<string> Report { get; }

    /// <summary>安装；返回安装的版本号。</summary>
    internal string Install(string target, bool desktopShortcut)
    {
        StopRunningInstances(target);

        Directory.CreateDirectory(target);
        Report($"正在解压到 {target} …");

        using (var payload = OpenPayload())
        {
            ZipFile.ExtractToDirectory(payload, target, overwriteFiles: true);
        }

        var version = ReadVersion(Path.Combine(target, MainExecutable));
        Report($"正在创建快捷方式 …");

        // 与 .iss 一样另存一份带版本号的图标，供快捷方式与「应用和功能」使用。
        var icon = Path.Combine(target, "GlassDesk.ico");
        var versionedIcon = Path.Combine(target, $"GlassDesk-{version}.ico");

        if (File.Exists(icon) && !File.Exists(versionedIcon))
        {
            File.Copy(icon, versionedIcon);
        }

        var exe = Path.Combine(target, MainExecutable);
        var iconPath = File.Exists(versionedIcon) ? versionedIcon : exe;

        ShellIntegration.CreateShortcut(Path.Combine(ShellIntegration.StartMenuDirectory, ShortcutName), exe, target, iconPath);

        if (desktopShortcut)
        {
            ShellIntegration.CreateShortcut(Path.Combine(ShellIntegration.DesktopDirectory, ShortcutName), exe, target, iconPath);
        }

        WriteUninstallEntry(target, version, iconPath);
        Report($"GlassDesk {version} 已安装。");
        return version;
    }

    /// <summary>卸载；返回是否把安装目录也删掉了。</summary>
    internal bool Uninstall(string target)
    {
        Report("正在移除快捷方式与卸载信息 …");

        ShellIntegration.DeleteShortcut(Path.Combine(ShellIntegration.StartMenuDirectory, ShortcutName));
        ShellIntegration.DeleteShortcut(Path.Combine(ShellIntegration.DesktopDirectory, ShortcutName));

        Registry.CurrentUser.DeleteSubKeyTree(
            $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}", throwOnMissingSubKey: false);

        if (!Directory.Exists(target))
        {
            Report("GlassDesk 已卸载。");
            return true;
        }

        StopRunningInstances(target);
        Report($"正在删除 {target} …");

        for (var attempt = 0; attempt < 3 && Directory.Exists(target); attempt++)
        {
            try
            {
                Directory.Delete(target, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(400);
            }
        }

        var removed = !Directory.Exists(target);

        Report(removed
            ? "GlassDesk 已卸载。"
            : "部分文件仍被占用，未能删除；请注销后手动删除该目录。");

        return removed;
    }

    /// <summary>结束从安装目录启动的 GlassDesk 进程（它们锁着自己的文件）。</summary>
    private static void StopRunningInstances(string target)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)) + Path.DirectorySeparatorChar;

        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(MainExecutable)))
        {
            try
            {
                var path = process.MainModule?.FileName;

                if (path is null || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(5000);
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // 进程恰好退出，或权限不够读它的模块路径：都不是致命错误。
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void WriteUninstallEntry(string target, string version, string iconPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(
            $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}", writable: true)
            ?? throw new InvalidOperationException("无法写入卸载信息。");

        var self = Environment.ProcessPath ?? Path.Combine(target, "GlassDesk-Setup.exe");

        key.SetValue("DisplayName", DisplayName);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", Publisher);
        key.SetValue("InstallLocation", target);
        key.SetValue("DisplayIcon", iconPath);
        key.SetValue("UninstallString", $"\"{self}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{self}\" --uninstall --silent");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static string ReadVersion(string exePath)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(exePath).ProductVersion;

            if (!string.IsNullOrWhiteSpace(version))
            {
                return version.Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
        }

        return "1.0";
    }

    private static System.IO.Stream OpenPayload()
        => typeof(SetupEngine).Assembly.GetManifestResourceStream(PayloadResource)
            ?? throw new InvalidOperationException(
                "这个安装包没有内置载荷（构建时缺少 payload.zip）。请重新生成安装包。");

    /// <summary>默认安装目录：与 .iss 的 <c>{localappdata}\Programs\GlassDesk</c> 一致。</summary>
    internal static string DefaultTarget => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GlassDesk");

    /// <summary>安装完成后要启动的程序。</summary>
    internal static string ExecutablePath(string target) => Path.Combine(target, MainExecutable);
}
