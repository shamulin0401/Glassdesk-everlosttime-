namespace GlassDesk.Core.Models.Settings;

/// <summary>
/// 用户对单个托盘图标做的调整：排序、隐藏与固定。
/// </summary>
/// <remarks>
/// 只描述「用户希望怎样」，不保存图标本身：托盘图标每次会话都可能变，
/// 持久化的必须是可以重新匹配回去的标识，而不是一次性的句柄或位图。
/// </remarks>
public sealed class TrayItemPreference
{
    /// <summary>
    /// 稳定标识：由托盘图标的显示名与所属进程的可执行文件路径拼成。
    /// </summary>
    /// <remarks>
    /// 两者都参与，是因为同一个进程可以贡献多个图标（例如资源管理器），
    /// 而同一个名字也可能来自不同程序；只取其一都会把不同的图标混成一个。
    /// </remarks>
    public string Id { get; set; } = string.Empty;

    /// <summary>在面板里的排序位置，小的排前面。</summary>
    public int Order { get; set; }

    /// <summary>在 GlassDesk 面板里隐藏这一项（不改动 Windows 通知区域本身）。</summary>
    public bool Hidden { get; set; }

    /// <summary>固定在面板最前面，排在所有未固定的图标之前。</summary>
    public bool Pinned { get; set; }
}
