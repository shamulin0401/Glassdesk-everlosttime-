using System.Windows.Automation;
using GlassDesk.Core.Models;

namespace GlassDesk.Dock.Tray;

/// <summary>
/// 读取 Windows 通知区域（系统托盘）里由各程序注册的图标，并能代替用户去点它们。
/// </summary>
/// <remarks>
/// 走 UI Automation，而不是经典的 <c>ToolbarWindow32</c> 消息枚举：
/// Windows 11 的通知区域已经交给 XAML 岛渲染，实测在整机范围内
/// <c>ToolbarWindow32</c>、<c>SysPager</c> 一个都不存在，
/// <c>TB_GETBUTTON</c> 那条路在新系统上没有任何目标可读。
///
/// <para>
/// 实测结构（Windows 11 10.0.26100）：<c>Shell_TrayWnd</c> 之下，
/// 由程序自己注册的托盘图标是 <c>SystemTray.AccentButton</c> 类、
/// <c>AutomationId</c> 为 <c>NotifyItemIcon</c> 的按钮，其子节点是一个 <c>Image</c>，
/// 矩形正好是图标的可见范围。系统自己那些（网络、音量、电源、时钟、输入指示）
/// 用的是 <c>SystemTrayIcon</c>，所以按 <c>NotifyItemIcon</c> 过滤就避开了它们——
/// 面板自带系统控制区，不需要重复一遍系统状态。
/// </para>
///
/// <para>
/// 全部读取都放在后台线程上：UIA 的跨进程调用会等目标响应，
/// 在界面线程上做会让面板在 explorer 忙的时候卡住。
/// </para>
/// </remarks>
public sealed class SystemTrayReader
{
    /// <summary>任务栏窗口类名。</summary>
    private const string TaskbarClass = "Shell_TrayWnd";

    /// <summary>由程序自己注册的托盘图标的自动化标识。</summary>
    private const string TrayItemAutomationId = "NotifyItemIcon";

    /// <summary>最近一次读取留下的自动化元素，供 <see cref="TryInvokeAsync"/> 使用。</summary>
    private readonly List<(string Id, AutomationElement Element)> _elements = [];

    /// <summary>重新读取通知区域里的托盘图标。</summary>
    public async Task<IReadOnlyList<TrayIconEntry>> ReadAsync()
    {
        return await Task.Run(Read).ConfigureAwait(true);
    }

    /// <summary>
    /// 调用某个托盘图标的默认操作，等同于用户左键点它。
    /// </summary>
    /// <remarks>
    /// 用 UIA 的默认操作而不是伪造鼠标点击：点击坐标会受任务栏缩放、DPI 与
    /// 图标移动影响，而默认操作是目标程序自己声明的语义动作。
    /// </remarks>
    public async Task<bool> TryInvokeAsync(string id)
    {
        return await Task.Run(() => TryInvoke(id)).ConfigureAwait(true);
    }

    private IReadOnlyList<TrayIconEntry> Read()
    {
        var entries = new List<TrayIconEntry>();
        _elements.Clear();

        try
        {
            var taskbar = AutomationElement.RootElement.FindFirst(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ClassNameProperty, TaskbarClass));

            if (taskbar is null)
            {
                return entries;
            }

            var items = taskbar.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, TrayItemAutomationId));

            foreach (AutomationElement item in items)
            {
                var entry = Describe(item);

                if (entry is null)
                {
                    continue;
                }

                _elements.Add((entry.Id, item));
                entries.Add(entry);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 通知区域重建（explorer 重启、分辨率或缩放切换）期间 UIA 会抛各种异常。
            // 这一轮当作「读不到」返回已经读到的部分，下一次刷新会重新来过。
        }

        return entries;
    }

    private static TrayIconEntry? Describe(AutomationElement item)
    {
        try
        {
            var title = item.Current.Name ?? string.Empty;
            var id = TrayLayoutPolicy.ComposeId(title);

            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            var (x, y, width, height) = ReadIconBounds(item);
            return new TrayIconEntry(id, title, x, y, width, height);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// 取图标自身的矩形。
    /// </summary>
    /// <remarks>
    /// 优先用那个 <c>Image</c> 子节点的矩形：按钮本身比图标宽（实测按钮 35×40 而图标 20×20），
    /// 用按钮矩形取像素会把任务栏背景一起取进来。
    /// </remarks>
    private static (int X, int Y, int Width, int Height) ReadIconBounds(AutomationElement item)
    {
        var target = item.FindFirst(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Image)) ?? item;

        var bounds = target.Current.BoundingRectangle;

        if (bounds.IsEmpty || double.IsInfinity(bounds.Width) || double.IsInfinity(bounds.Height))
        {
            return default;
        }

        return ((int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height);
    }

    private bool TryInvoke(string id)
    {
        try
        {
            foreach (var (elementId, element) in _elements)
            {
                if (!string.Equals(elementId, id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)
                    && pattern is InvokePattern invoke)
                {
                    invoke.Invoke();
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 目标程序在点击之前退出了，或者它的 UIA 提供程序无响应。
        }

        return false;
    }
}
