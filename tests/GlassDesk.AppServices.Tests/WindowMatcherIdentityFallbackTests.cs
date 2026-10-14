using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices.Tests;

/// <summary>
/// 带身份的条目遇到「<strong>没有</strong>身份的窗口」时，仍要按映像路径认领。
/// </summary>
/// <remarks>
/// 这是「合并固定图标与运行中的应用」失效的根因：从快捷方式导入的条目通常是
/// 「Target 是解析出的 exe、身份来自 .lnk 的 AUMID」（本机 107 个开始菜单快捷方式里有 24 个如此），
/// 而产品启动的是 exe 本身，身份不会传给进程——于是经典 Win32 窗口两侧都读不出身份。
/// 原先这种条目只走身份那一条路，一个窗口都匹配不到：运行圆点不亮、点击点不亮已有窗口、
/// 合并固定图标也无从下手（ConsumedByPinned 拿到空集合）。
///
/// <para>
/// 下面第二条把放宽的边界钉住：<b>身份不同</b>依旧不算匹配，即使映像路径一模一样。
/// 第三条防止同一窗口被身份与路径各计一次。
/// </para>
/// </remarks>
public sealed class WindowMatcherIdentityFallbackTests
{
    private static WindowIdentityInfo Window(
        nint handle,
        string? imagePath = null,
        string? windowAumid = null,
        string? processAumid = null)
        => new(handle, "窗口", 1000 + (uint)handle, false, imagePath, windowAumid, processAumid);

    private static AppEntry ProgramEntry(string target, string? aumid) => new()
    {
        Id = "e1",
        Kind = AppItemKind.Program,
        Target = target,
        AppUserModelId = aumid,
    };

    [Fact]
    public void 带身份的条目仍认领没有身份的同类窗口()
    {
        var entry = ProgramEntry(@"C:\Windows\System32\charmap.exe", "MSEdge");
        var snapshot = new[] { Window(1, imagePath: @"C:\Windows\System32\charmap.exe") };

        var handle = Assert.Single(WindowMatcher.MatchWindows(entry, snapshot, entry.AppUserModelId)).Handle;

        Assert.Equal(1, handle);
    }

    [Fact]
    public void 身份不同的窗口依旧不算匹配_即使映像路径相同()
    {
        var entry = ProgramEntry(@"C:\Windows\System32\charmap.exe", "MSEdge");
        var snapshot = new[]
        {
            Window(1, imagePath: @"C:\Windows\System32\charmap.exe", windowAumid: "另一个应用的身份"),
        };

        Assert.Empty(WindowMatcher.MatchWindows(entry, snapshot, entry.AppUserModelId));
    }

    [Fact]
    public void 身份已匹配的窗口不会被路径再计入一次()
    {
        var entry = ProgramEntry(@"C:\Windows\System32\charmap.exe", "MSEdge");
        var snapshot = new[]
        {
            Window(1, imagePath: @"C:\Windows\System32\charmap.exe", windowAumid: "MSEdge"),
        };

        Assert.Single(WindowMatcher.MatchWindows(entry, snapshot, entry.AppUserModelId));
    }
}
