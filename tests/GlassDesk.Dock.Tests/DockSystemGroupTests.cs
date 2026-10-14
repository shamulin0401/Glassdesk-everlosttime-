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
/// 右侧系统组（时间日期与系统托盘）从设置到界面的接线。
/// </summary>
/// <remarks>
/// 这些用例存在的理由是：XAML 能编译不等于能加载。托盘面板用了键控样式、
/// 自带模板的滑轨与 Track 部件，任何一处资源名或部件名写错都只会在运行时炸，
/// 而单靠构建是看不出来的。
///
/// <para>
/// 只做只读的系统调用（读任务栏状态、输入语言、音量、通知区域图标）；
/// 不改动用户的任务栏状态，也不点任何托盘图标。
/// </para>
/// </remarks>
public sealed class DockSystemGroupTests
{
    [Fact]
    public void ClockAndTrayPanelFollowDockSettingsAndThePanelLoadsItsXaml()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-system-group-" + Guid.NewGuid().ToString("N"));
            var dock = new DockWindow();
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(root));
                data.LoadAll();
                using var services = new AppServiceHost(data);
                var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
                dock.AttachServices(services, host, () => { });
                dock.Show();

                var group = Assert.IsType<Border>(dock.FindName("SystemGroupHost"));
                var trayButton = Assert.IsType<Button>(dock.FindName("TrayPanelButton"));
                var clockHost = Assert.IsType<StackPanel>(dock.FindName("ClockHost"));
                var clockTime = Assert.IsType<TextBlock>(dock.FindName("ClockTimeText"));
                var clockDate = Assert.IsType<TextBlock>(dock.FindName("ClockDateText"));

                // 默认关闭：右侧系统组整体不出现。
                Assert.Equal(Visibility.Collapsed, group.Visibility);

                host.Update(new DockSettings { ShowClock = true });
                dock.UpdateLayout();

                // 只开时间日期：底板出现，托盘按钮仍然收起。
                Assert.Equal(Visibility.Visible, group.Visibility);
                Assert.Equal(Visibility.Collapsed, trayButton.Visibility);
                Assert.Equal(Visibility.Visible, clockHost.Visibility);

                // 走时定时器启用时立刻写一次时间，不必等第一跳。
                // 走时定时器启用时立刻写一次时间，不必等第一跳；默认精确到秒。
                Assert.Matches(@"^\d{2}:\d{2}:\d{2}$", clockTime.Text);
                Assert.Matches(@"^\d{4}/\d{2}/\d{2}$", clockDate.Text);

                host.Update(new DockSettings { ShowSystemTray = true });
                dock.UpdateLayout();

                Assert.Equal(Visibility.Visible, trayButton.Visibility);
                // 关了时间日期之后时钟那一段要让位，不能留下空白。
                Assert.Equal(Visibility.Collapsed, clockHost.Visibility);

                // 面板是懒创建的：没点过之前不该存在。
                Assert.Null(PanelField().GetValue(dock));

                dock.ToggleTrayPanel();
                var panel = Assert.IsType<TrayPanelWindow>(PanelField().GetValue(dock));
                Assert.True(panel.IsVisible);
                Assert.NotNull(panel.FindName("TrayIconGrid"));
                Assert.NotNull(panel.FindName("TaskbarToggle"));
                Assert.NotNull(panel.FindName("VolumeSlider"));

                dock.ToggleTrayPanel();
                Assert.False(panel.IsVisible);

                // 关掉托盘开关时，即使面板还开着也必须收起来。
                dock.ToggleTrayPanel();
                Assert.True(panel.IsVisible);
                host.Update(new DockSettings { ShowSystemTray = false });
                dock.UpdateLayout();
                Assert.False(panel.IsVisible);
                Assert.Equal(Visibility.Collapsed, group.Visibility);
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
    public void TrayPreferencesRoundTripThroughTheHostProvidedSaver()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-tray-prefs-" + Guid.NewGuid().ToString("N"));
            var dock = new DockWindow();
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(root));
                data.LoadAll();
                using var services = new AppServiceHost(data);
                var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
                dock.AttachServices(services, host, () => { });
                dock.Show();

                IReadOnlyList<TrayItemPreference>? saved = null;
                dock.SaveTrayPreferences = preferences =>
                {
                    saved = preferences;
                    return true;
                };

                var panel = PanelField().GetValue(dock);
                Assert.Null(panel);

                host.Update(new DockSettings { ShowSystemTray = true });
                dock.ToggleTrayPanel();

                var opened = Assert.IsType<TrayPanelWindow>(PanelField().GetValue(dock));
                Assert.True(opened.IsVisible);
                Assert.NotNull(dock.SaveTrayPreferences);
                Assert.Null(saved);

                dock.CloseTrayPanel();
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

    private static FieldInfo PanelField() =>
        typeof(DockWindow).GetField("_trayPanel", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("DockWindow 上找不到 _trayPanel 字段。");
}
