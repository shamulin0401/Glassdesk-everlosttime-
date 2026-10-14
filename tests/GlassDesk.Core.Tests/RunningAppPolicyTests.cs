using GlassDesk.Core.Models;

namespace GlassDesk.Core.Tests;

public sealed class RunningAppPolicyTests
{
    private const uint OwnProcess = 4242;

    [Fact]
    public void ComposeKey_PrefersAppIdentityThenImagePathThenProcess()
    {
        Assert.Equal("aumid:MSEdge", RunningAppPolicy.ComposeKey("MSEdge", @"C:\edge.exe", 7));
        Assert.Equal(@"path:C:\edge.exe", RunningAppPolicy.ComposeKey(null, @"C:\edge.exe", 7));
        Assert.Equal(@"path:C:\edge.exe", RunningAppPolicy.ComposeKey("   ", @"C:\edge.exe", 7));
        Assert.Equal("pid:7", RunningAppPolicy.ComposeKey(null, null, 7));
    }

    [Fact]
    public void Group_NeverShowsGlassDeskOwnWindows()
    {
        // Dock、启动台、设置中心都是可见、有标题、无属主的顶层窗口，正好符合枚举候选规则。
        var windows = new[]
        {
            Window(1, "GlassDesk Dock", OwnProcess),
            Window(2, "GlassDesk 启动台", OwnProcess),
            Window(3, "记事本", 900),
        };

        var groups = RunningAppPolicy.Group(windows, OwnProcess);

        var group = Assert.Single(groups);
        Assert.Equal([3], group.Windows.Select(window => window.Handle));
    }

    [Fact]
    public void Group_CollectsEveryWindowOfTheSameApplicationIntoOneTile()
    {
        var windows = new[]
        {
            Window(1, "文档 1 - 记事本", 900, path: @"C:\notepad.exe"),
            Window(2, "文档 2 - 记事本", 901, path: @"C:\notepad.exe", minimized: true),
            Window(3, "计算器", 902, path: @"C:\calc.exe"),
        };

        var groups = RunningAppPolicy.Group(windows, OwnProcess);

        Assert.Equal(2, groups.Count);
        Assert.Equal(@"path:C:\notepad.exe", groups[0].Key);
        Assert.Equal([1, 2], groups[0].Windows.Select(window => window.Handle));
        Assert.Equal(1, groups[0].MinimizedCount);
        Assert.False(groups[0].HasSingleWindow);
        Assert.True(groups[1].HasSingleWindow);
    }

    [Fact]
    public void Group_MergesDifferentProcessesThatShareAnAppIdentity()
    {
        // 打包应用与多进程宿主的窗口来自不同进程与不同 exe，靠应用身份才能归一。
        var windows = new[]
        {
            Window(1, "标签页一", 10, path: @"C:\browser\app.exe", appId: "MSEdge"),
            Window(2, "标签页二", 11, path: @"C:\browser\sub\app.exe", appId: "MSEdge"),
        };

        var groups = RunningAppPolicy.Group(windows, OwnProcess);

        var group = Assert.Single(groups);
        Assert.Equal("aumid:MSEdge", group.Key);
        Assert.Equal(2, group.Windows.Count);
        Assert.Equal("MSEdge", group.AppId);
    }

    [Fact]
    public void Group_KeepsFirstAppearanceOrderSoTilesDoNotJumpAround()
    {
        var windows = new[]
        {
            Window(1, "A", 900, path: @"C:\a.exe"),
            Window(2, "B", 901, path: @"C:\b.exe"),
            // a.exe 又来一个窗口：只能追加到已有的组，不能把这一组挪到最前。
            Window(3, "A2", 902, path: @"C:\a.exe"),
        };

        var groups = RunningAppPolicy.Group(windows, OwnProcess);

        Assert.Equal([@"path:C:\a.exe", @"path:C:\b.exe"], groups.Select(group => group.Key));
        Assert.Equal([1, 3], groups[0].Windows.Select(window => window.Handle));
    }

    [Fact]
    public void Group_BackfillsAppIdentityThatOnlyOneWindowCarries()
    {
        // 同一进程的窗口共享可执行文件路径（枚举时按进程记忆），
        // 但显式应用身份是逐窗口读的，可能只有其中一个窗口带。
        var windows = new[]
        {
            Window(1, "无身份的窗口", 900, path: @"C:\late.exe", appId: null),
            Window(2, "同一进程的窗口", 900, path: @"C:\late.exe", appId: "LateApp"),
        };

        var groups = RunningAppPolicy.Group(windows, OwnProcess);

        var group = Assert.Single(groups);
        Assert.Equal(@"path:C:\late.exe", group.Key);
        Assert.Equal("LateApp", group.AppId);
        Assert.Equal([1, 2], group.Windows.Select(window => window.Handle));
    }

    [Fact]
    public void Group_ReturnsNothingWhenOnlyOurOwnWindowsAreOpen()
    {
        var windows = new[] { Window(1, "GlassDesk Dock", OwnProcess) };

        Assert.Empty(RunningAppPolicy.Group(windows, OwnProcess));
    }

    [Fact]
    public void Group_RejectsNullWindows()
    {
        Assert.Throws<ArgumentNullException>(() => RunningAppPolicy.Group(null!, OwnProcess));
    }

    private static RunningWindow Window(
        nint handle,
        string title,
        uint processId,
        string? path = null,
        string? appId = null,
        bool minimized = false)
        => new(handle, title, processId, minimized, path, appId);
}
