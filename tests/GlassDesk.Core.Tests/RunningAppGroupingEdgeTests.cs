using GlassDesk.Core.Models;

namespace GlassDesk.Core.Tests;

/// <summary>
/// 运行窗口分组的边界：身份不同的窗口不许并成一个磁贴，组合键不许丢掉窗口。
/// </summary>
/// <remarks>
/// 两条都由探针在真实数据上复现过：
/// <list type="number">
/// <item>两个 AUMID 不同、只共享宿主进程映像路径的窗口（<c>ApplicationFrameHost.exe</c>
/// 承载所有 UWP 应用就是这种）被并成一个磁贴——用户看到的是一张属于另一个应用的图标；</item>
/// <item>同一个进程的第二个「无身份、无路径」窗口会重新组合出同一个键，
/// 把已有桶清空并再排一次序，于是第一个窗口消失、同一应用出现两个磁贴。</item>
/// </list>
/// </remarks>
public sealed class RunningAppGroupingEdgeTests
{
    private static RunningWindow Window(nint handle, uint processId, string? imagePath, string? appId)
        => new(handle, $"窗口 {handle}", processId, false, imagePath, appId);

    [Fact]
    public void 身份不同的窗口不因共享宿主进程而被并成一个磁贴()
    {
        const string Host = @"C:\Windows\System32\ApplicationFrameHost.exe";
        var windows = new[]
        {
            Window(11, 900, Host, "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"),
            Window(12, 900, Host, "Microsoft.WindowsStore_8wekyb3d8bbwe!App"),
        };

        var groups = RunningAppPolicy.Group(windows, ownProcessId: 1);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.Single(group.Windows));
    }

    [Fact]
    public void 同一进程的多个无身份窗口合成一组且一个都不丢()
    {
        var windows = new[]
        {
            Window(1, 900, imagePath: null, appId: null),
            Window(2, 900, imagePath: null, appId: null),
        };

        var groups = RunningAppPolicy.Group(windows, ownProcessId: 1);

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Windows.Count);
        Assert.Contains(group.Windows, window => window.Handle == 1);
        Assert.Contains(group.Windows, window => window.Handle == 2);
    }

    [Fact]
    public void 无身份的窗口仍会并进已认领的组_身份由它旁边的窗口补上()
    {
        const string Path = @"C:\Program Files\App\app.exe";
        var windows = new[]
        {
            // 先来的没有身份，先把组建起来。
            Window(1, 111, Path, appId: null),
            // 后来的带着身份，共享同一映像路径：应并进同一组，并把身份补上。
            Window(2, 222, Path, "厂商.应用"),
        };

        var group = Assert.Single(RunningAppPolicy.Group(windows, ownProcessId: 1));

        Assert.Equal(2, group.Windows.Count);
    }

    [Fact]
    public void 外壳宿主进程不出现在运行条上()
    {
        // 「Windows 输入体验」这类宿主同样满足「无主、可见、有标题」，
        // 不排掉就会以 TextInputHost 的名义混进运行条——用户既没打开过它，也点不出什么。
        var windows = new[]
        {
            Window(1, 900, @"C:\Windows\System32\TextInputHost.exe", appId: null),
            Window(2, 901, @"C:\Program Files\App\app.exe", appId: null),
        };

        var group = Assert.Single(RunningAppPolicy.Group(windows, ownProcessId: 1));

        Assert.Equal(2, group.Windows[0].Handle);
    }

    [Fact]
    public void 承载UWP应用的宿主不会被误排掉()
    {
        // 反向约束：ApplicationFrameHost 承载**所有** UWP 应用，
        // 一旦把它列进宿主名单，用户打开的那些应用会被一起藏掉。
        var windows = new[]
        {
            Window(1, 900, @"C:\Windows\System32\ApplicationFrameHost.exe", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"),
        };

        Assert.Single(RunningAppPolicy.Group(windows, ownProcessId: 1));
    }
}
