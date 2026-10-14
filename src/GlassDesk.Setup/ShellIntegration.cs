using System.IO;
using System.Runtime.InteropServices;

namespace GlassDesk.Setup;

/// <summary>
/// 快捷方式与常见目录。走 WScript.Shell（系统自带），不为一行 COM 调用再引互操作程序集。
/// </summary>
internal static class ShellIntegration
{
    internal static string StartMenuDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs");

    internal static string DesktopDirectory => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    internal static void CreateShortcut(string shortcutPath, string exePath, string workingDirectory, string iconPath)
    {
        var directory = Path.GetDirectoryName(shortcutPath);

        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("系统未注册 WScript.Shell，无法创建快捷方式。");

        dynamic shell = Activator.CreateInstance(shellType)!;

        try
        {
            dynamic link = shell.CreateShortcut(shortcutPath);
            link.TargetPath = exePath;
            link.WorkingDirectory = workingDirectory;
            link.IconLocation = iconPath;
            link.Description = "GlassDesk";
            link.Save();
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(shell);
        }
    }

    internal static void DeleteShortcut(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
