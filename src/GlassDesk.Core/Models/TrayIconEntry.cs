namespace GlassDesk.Core.Models;

/// <summary>
/// 通知区域里的一个托盘图标。
/// </summary>
/// <remarks>
/// 只记录「在哪里、叫什么、怎么标识」，不携带图标位图与任何 UI 类型：
/// 位置是屏幕物理像素，取像素与画出来都由界面层负责。
/// </remarks>
/// <param name="Id">
/// 稳定标识，同时就是显示名：由 <see cref="TrayLayoutPolicy.ComposeId"/> 从完整工具提示里
/// 折出的第一行。用户看到的就是它，写进 settings.json 的也是它。
/// </param>
/// <param name="Title">完整名称（UIA 给出的工具提示原文，可能多行），用于悬停提示。</param>
/// <param name="X">图标在屏幕上的物理像素左边界。</param>
/// <param name="Y">图标在屏幕上的物理像素上边界。</param>
/// <param name="Width">图标宽度（物理像素）。</param>
/// <param name="Height">图标高度（物理像素）。</param>
public sealed record TrayIconEntry(
    string Id,
    string Title,
    int X,
    int Y,
    int Width,
    int Height)
{
}
