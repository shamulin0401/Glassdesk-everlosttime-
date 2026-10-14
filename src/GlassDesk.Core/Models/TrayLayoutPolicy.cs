using GlassDesk.Core.Models.Settings;

namespace GlassDesk.Core.Models;

/// <summary>
/// 把「通知区域现在有什么」与「用户希望怎么排」合到一起。
/// </summary>
/// <remarks>
/// 纯函数，不碰任何系统接口：托盘面板每次刷新都要重新算一遍，
/// 而 UIA 读取与屏幕取像素都可能失败，排序本身必须永远能算出来。
/// </remarks>
public static class TrayLayoutPolicy
{
    /// <summary>
    /// 由托盘图标的完整名称折出稳定标识。
    /// </summary>
    /// <remarks>
    /// UIA 给出的名称是完整工具提示，可能带多行状态
    /// （实测例子：<c>"QQ: 2719630640\n声音: 开启\n消息提醒框: 开启"</c>），
    /// 而状态会随用户操作变化。只取第一行并去掉首尾空白，
    /// 用户的排序、隐藏与固定才能在状态变化之后继续匹配到同一个图标。
    ///
    /// <para>
    /// 保留大小写原样、由调用方用 <c>OrdinalIgnoreCase</c> 比较，而不是在这里折成小写：
    /// 标识会原样写进 settings.json 供用户查看，可读性比统一大小写更有价值。
    /// </para>
    /// </remarks>
    public static string ComposeId(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var lineBreak = title.IndexOf('\n');
        var firstLine = lineBreak >= 0 ? title[..lineBreak] : title;
        return firstLine.Trim();
    }

    /// <summary>
    /// 按用户偏好排列托盘图标：隐藏的去掉，固定的排最前，其余按记录的次序，未记录的保持原顺序。
    /// </summary>
    /// <remarks>
    /// 没有偏好时原样返回输入——这正是新用户看到的状态，也让这条路径零分配。
    /// </remarks>
    public static IReadOnlyList<TrayIconEntry> Arrange(
        IReadOnlyList<TrayIconEntry> entries,
        IReadOnlyList<TrayItemPreference>? preferences)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (preferences is null || preferences.Count == 0)
        {
            return entries;
        }

        var byId = new Dictionary<string, TrayItemPreference>(StringComparer.OrdinalIgnoreCase);

        foreach (var preference in preferences)
        {
            if (!string.IsNullOrEmpty(preference.Id))
            {
                byId[preference.Id] = preference;
            }
        }

        var candidates = new List<(TrayIconEntry Entry, int Group, int Order, int Index)>(entries.Count);

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];

            if (!byId.TryGetValue(entry.Id, out var preference))
            {
                // 没被用户动过的图标排在已记录项的后面，组内保持系统给的顺序。
                candidates.Add((entry, 1, int.MaxValue, index));
                continue;
            }

            if (preference.Hidden)
            {
                continue;
            }

            candidates.Add((entry, preference.Pinned ? 0 : 1, preference.Order, index));
        }

        candidates.Sort(static (left, right) =>
        {
            var group = left.Group.CompareTo(right.Group);
            return group != 0 ? group : left.Order != right.Order
                ? left.Order.CompareTo(right.Order)
                : left.Index.CompareTo(right.Index);
        });

        var arranged = new List<TrayIconEntry>(candidates.Count);

        foreach (var candidate in candidates)
        {
            arranged.Add(candidate.Entry);
        }

        return arranged;
    }
}
