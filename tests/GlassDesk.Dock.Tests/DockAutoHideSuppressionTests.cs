using System.IO;
using System.Reflection;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Storage;
using GlassDesk.UI.Settings;

namespace GlassDesk.Dock.Tests;

/// <summary>
/// 自动隐藏的抑制计数不许被漏掉、也不许被整体替换弄丢。
/// </summary>
/// <remarks>
/// 两条都对应真实故障（探针在真窗口上复现过）：
/// <list type="number">
/// <item>托盘面板**自己**收起时不通知 Dock（点托盘图标转发、右键退出程序、点折叠区条目
/// 都走它自己的 <c>ClosePanel</c>），计数永久停在 1 → 自动隐藏的轮询直接返回，
/// Dock 再也不收起来，会一直盖在全屏程序上；</item>
/// <item>改任何 Dock 设置都会整体重建状态机（<c>OnDockSettingsChanged</c> 第一句），
/// 新建的控制器计数为 0 → 面板还开着，Dock 却从它底下收走了。</item>
/// </list>
/// 抑制计数是私有状态，这里用反射读它——没有别的办法从外部观察"是否还被压着"。
/// </remarks>
public sealed class DockAutoHideSuppressionTests
{
    [Fact]
    public void 托盘面板自己收起后不再压住自动隐藏()
    {
        StaTest.Run(() =>
        {
            WithDock((dock, _) =>
            {
                Assert.Equal(0, SuppressionOf(dock));

                dock.ToggleTrayPanel();
                dock.UpdateLayout();

                var panel = TrayPanelOf(dock);
                Assert.NotNull(panel);
                Assert.True(SuppressionOf(dock) > 0);

                // 面板自己收起——正是原先漏掉的那条路。
                panel!.ClosePanel();
                dock.UpdateLayout();

                Assert.Equal(0, SuppressionOf(dock));
            });
        });
    }

    [Fact]
    public void 重建状态机不会丢掉抑制余额()
    {
        StaTest.Run(() =>
        {
            WithDock((dock, _) =>
            {
                dock.ToggleTrayPanel();
                dock.UpdateLayout();

                var before = SuppressionOf(dock);
                Assert.True(before > 0);

                RebuildAutoHideOf(dock);

                // 设置变了，但面板还开着：余额必须跟着搬过去，否则 Dock 会从面板底下收走。
                Assert.Equal(before, SuppressionOf(dock));
            });
        });
    }

    private static void WithDock(Action<DockWindow, AppServiceHost> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-suppress-" + Guid.NewGuid().ToString("N"));
        var data = new GlassDeskDataStore(new AppPaths(root));
        DockWindow? dock = null;

        try
        {
            data.LoadAll();
            using var services = new AppServiceHost(data);
            var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
            dock = new DockWindow();
            dock.AttachServices(services, host, () => { });
            dock.Show();
            dock.UpdateLayout();

            body(dock, services);
        }
        finally
        {
            dock?.Close();

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static int SuppressionOf(DockWindow dock)
    {
        var controller = Field(dock, "_autoHide")!;

        return (int)controller.GetType()
            .GetProperty("SuppressCount", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(controller)!;
    }

    private static TrayPanelWindow? TrayPanelOf(DockWindow dock) => (TrayPanelWindow?)Field(dock, "_trayPanel");

    private static void RebuildAutoHideOf(DockWindow dock)
        => typeof(DockWindow).GetMethod("RebuildAutoHide", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dock, null);

    private static object? Field(DockWindow dock, string name)
        => typeof(DockWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dock);
}
