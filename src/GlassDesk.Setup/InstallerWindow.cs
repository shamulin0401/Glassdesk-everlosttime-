using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GlassDesk.Setup;

/// <summary>
/// 安装向导窗口。界面用代码搭建，不引 XAML——少一个文件，也避免与自定义入口点冲突。
/// </summary>
internal sealed class InstallerWindow : Window
{
    private readonly bool _uninstall;
    private readonly SetupEngine _engine;
    private readonly TextBox _path;
    private readonly CheckBox _desktop;
    private readonly CheckBox _run;
    private readonly ProgressBar _progress;
    private readonly TextBlock _status;

    internal InstallerWindow(bool uninstall, string target)
    {
        _uninstall = uninstall;
        _engine = new SetupEngine(SetStatus);

        Title = uninstall ? "卸载 GlassDesk" : "安装 GlassDesk";
        Width = 560;
        Height = 320;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));
        FontFamily = new FontFamily("Segoe UI");

        _path = new TextBox { Text = target, Padding = new Thickness(6, 4, 6, 4), IsEnabled = !uninstall };
        _desktop = new CheckBox { Content = "创建桌面快捷方式", Margin = new Thickness(0, 10, 0, 0), IsEnabled = !uninstall };
        _run = new CheckBox { Content = "立即运行 GlassDesk", Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        _progress = new ProgressBar { Height = 6, Margin = new Thickness(0, 14, 0, 8), IsIndeterminate = false, Value = 0 };
        _status = new TextBlock { Text = "准备就绪。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };

        Content = BuildLayout(uninstall);
    }

    private StackPanel BuildLayout(bool uninstall)
    {
        var panel = new StackPanel { Margin = new Thickness(24) };

        panel.Children.Add(new TextBlock
        {
            Text = uninstall ? "卸载 GlassDesk" : "GlassDesk 1.0",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
        });

        panel.Children.Add(new TextBlock
        {
            Text = uninstall
                ? "将删除程序文件、快捷方式与卸载信息。你的个人设置（数据目录）不会被删除。"
                : "用户级安装，不需要管理员权限。约 119 MB。",
            Margin = new Thickness(0, 6, 0, 16),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
        });

        if (!uninstall)
        {
            var row = new DockPanel();

            var browse = new Button { Content = "浏览…", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
            browse.Click += OnBrowse;
            DockPanel.SetDock(browse, Dock.Right);

            _ = row.Children.Add(browse);
            _ = row.Children.Add(_path);
            panel.Children.Add(new TextBlock { Text = "安装位置", Margin = new Thickness(0, 0, 0, 4) });
            panel.Children.Add(row);
            panel.Children.Add(_desktop);
        }

        panel.Children.Add(_progress);
        panel.Children.Add(_status);
        panel.Children.Add(_run);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };

        var primary = new Button
        {
            Content = uninstall ? "卸载" : "安装",
            Padding = new Thickness(20, 6, 20, 6),
            IsDefault = true,
        };
        primary.Click += OnPrimary;

        var cancel = new Button { Content = "取消", Padding = new Thickness(20, 6, 20, 6), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        cancel.Click += (_, _) => Close();

        _ = buttons.Children.Add(primary);
        _ = buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        return panel;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择安装位置", Multiselect = false };

        if (Directory.Exists(_path.Text))
        {
            dialog.InitialDirectory = _path.Text;
        }

        if (dialog.ShowDialog(this) == true)
        {
            _path.Text = dialog.FolderName;
        }
    }

    private async void OnPrimary(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            button.IsEnabled = false;
        }

        _progress.IsIndeterminate = true;

        try
        {
            // 先在本线程（UI 线程）把需要的东西读成普通值。
            //
            // 不能把控件本身带进 Task.Run：CheckBox 是 DispatcherObject，
            // 在后台线程读它的 IsChecked 会抛「The calling thread cannot access this object
            // because a different thread owns it」——这条踩过一次。
            var target = _path.Text.Trim();
            var desktopShortcut = _desktop.IsChecked == true;

            if (_uninstall)
            {
                _ = await Task.Run(() => _engine.Uninstall(target));
            }
            else
            {
                _ = await Task.Run(() => _engine.Install(target, desktopShortcut));
                _run.Visibility = Visibility.Visible;
                _run.IsChecked = true;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"出错：{ex.Message}");
            _ = MessageBox.Show(this, ex.Message, "GlassDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _progress.IsIndeterminate = false;
            _progress.Value = 100;

            if (sender is Button primaryButton)
            {
                primaryButton.IsEnabled = true;
            }
        }

        if (_run.Visibility == Visibility.Visible && _run.IsChecked == true)
        {
            StartInstalled(_path.Text.Trim());
            Close();
        }
    }

    private static void StartInstalled(string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(SetupEngine.ExecutablePath(target))
            {
                WorkingDirectory = target,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // 启动失败不影响「已安装」这个事实。
        }
    }

    private void SetStatus(string message)
        => Dispatcher.Invoke(() => _status.Text = message);
}
