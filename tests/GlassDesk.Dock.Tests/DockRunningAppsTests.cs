using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.UI.Settings;

namespace GlassDesk.Dock.Tests;

/// <summary>
/// Dock 右侧「打开的应用」那一条的接线与护栏。
/// </summary>
/// <remarks>
/// 运行中的应用来自真实窗口枚举，所以这里不断言「一定有几个磁贴」——
/// 那取决于跑测试时机器上开着什么。断言的是与环境无关的性质：
/// 开关控制它出不出现、运行磁贴用自己的 id 命名空间、
/// 并且永远不进固定列表与固定计数。
/// </remarks>
public sealed class DockRunningAppsTests
{
    [Fact]
    public void RunningStripIsGatedByItsSettingAndKeepsItsOwnIdNamespace()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-running-apps-" + Guid.NewGuid().ToString("N"));
            var dock = new DockWindow();
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(root));
                data.LoadAll();
                using var services = new AppServiceHost(data);
                var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
                dock.AttachServices(services, host, () => { });
                dock.Show();

                var hostControl = Assert.IsType<ContentControl>(dock.FindName("RunningTileStripHost"));
                var separator = Assert.IsType<Border>(dock.FindName("RunningGroupSeparator"));

                // 默认关闭：开关关着时即使系统里有一堆窗口，这一条也不成立。
                _ = services.Running.Reconcile();
                dock.RefreshTiles();
                dock.UpdateLayout();
                Assert.Empty(PinnedLayout(dock) ?? []);
                Assert.Empty(RunningTilesOf(dock));
                Assert.Equal(Visibility.Collapsed, hostControl.Visibility);
                Assert.Equal(Visibility.Collapsed, separator.Visibility);

                host.Update(new DockSettings { ShowRunningApps = true });
                _ = services.Running.Reconcile();
                dock.RefreshTiles();
                dock.UpdateLayout();

                var tiles = RunningTilesOf(dock);

                // 每个运行磁贴都必须待在自己的命名空间里：带 entry: 前缀会被当成内置入口，
                // 不带前缀则会被固定磁贴的排序与落盘逻辑当成应用库条目。
                foreach (var tile in tiles)
                {
                    Assert.StartsWith("running:", tile.Id, StringComparison.Ordinal);
                    Assert.NotEmpty(tile.Group.Windows);
                }

                // 竖线只在两条都非空时出现，否则会出现一条悬空的线。
                var pinnedCount = dock.TileCount;
                Assert.Equal(tiles.Count > 0 && pinnedCount > 0, separator.Visibility == Visibility.Visible);
                Assert.Equal(tiles.Count > 0, hostControl.Visibility == Visibility.Visible);
            }
            finally
            {
                dock.Close();
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        });
    }

    [Fact]
    public void RunningTilesNeverEnterThePinnedLayoutOrThePinnedTileCount()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-running-layout-" + Guid.NewGuid().ToString("N"));
            var dock = new DockWindow();
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(root));
                data.LoadAll();
                using var services = new AppServiceHost(data);
                var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
                dock.AttachServices(services, host, () => { });
                dock.Show();

                host.Update(new DockSettings
                {
                    ShowRunningApps = true,
                    MergePinnedWithRunning = true,
                });
                _ = services.Running.Reconcile();
                dock.RefreshTiles();
                dock.UpdateLayout();

                // TileCount 只数固定磁贴——多个既有测试依赖这一点，
                // 运行磁贴混进去会让它们全部失守。
                Assert.Equal(0, dock.TileCount);
                Assert.Empty(PinnedLayout(dock) ?? []);

                // 不合并时也不该写进布局：布局记的是「用户固定了哪些应用」。
                host.Update(new DockSettings
                {
                    ShowRunningApps = true,
                    MergePinnedWithRunning = false,
                });
                dock.RefreshTiles();
                dock.UpdateLayout();
                Assert.Equal(0, dock.TileCount);
                Assert.Empty(PinnedLayout(dock) ?? []);
            }
            finally
            {
                dock.Close();
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        });
    }

    private static IReadOnlyList<string>? PinnedLayout(DockWindow dock)
        => Field<object?>(dock, "_services") is AppServiceHost services
            ? services.Layouts.Dock.PinnedItemIds
            : null;

    private static IReadOnlyList<RunningTile> RunningTilesOf(DockWindow dock)
        => Field<List<RunningTile>>(dock, "_running") ?? [];

    private static T? Field<T>(DockWindow dock, string name)
    {
        var field = typeof(DockWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"DockWindow 上找不到 {name} 字段。");
        return (T?)field.GetValue(dock);
    }
}
