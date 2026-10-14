using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Core.Models;
using GlassDesk.Platform.Shell;

namespace GlassDesk.Dock.Tray;

/// <summary>
/// 折叠区里的一个托盘软件。
/// </summary>
/// <param name="Id">面板内部的标识，带 <c>overflow:</c> 前缀，与可见图标的 id 区分开。</param>
/// <param name="Title">显示名，取自登记表的提示文字，取不到时退回可执行文件名。</param>
/// <param name="ExecutablePath">所属程序；点它时按这个路径去激活或启动。</param>
/// <param name="Icon">登记表里的图标快照，与可见图标同源。</param>
public sealed record TrayOverflowItem(string Id, string Title, string ExecutablePath, ImageSource Icon);

/// <summary>折叠区条目 id 的构造与拆解。</summary>
/// <remarks>
/// 形如 <c>overflow:&lt;登记项子键名&gt;|&lt;可执行文件路径&gt;</c>：
/// 子键名用来区分「同一个软件的多条登记项」（两个 QQ 账号就是两条，它们的
/// 程序路径与显示名都一样，只有子键名不同），路径则让「点一下该做什么」不必回查登记表。
/// </remarks>
public static class TrayOverflowId
{
    /// <summary>前缀。</summary>
    public const string Prefix = "overflow:";

    /// <summary>分隔标识与路径的字符。</summary>
    private const char Separator = '|';

    /// <summary>拼一个折叠区条目的 id。</summary>
    public static string Compose(string key, string executablePath) => $"{Prefix}{key}{Separator}{executablePath}";

    /// <summary>从一个 id 里取出可执行文件路径；不是折叠区条目时返回 null。</summary>
    public static string? TryReadPath(string id)
    {
        if (!id.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var index = id.IndexOf(Separator, StringComparison.Ordinal);
        return index >= 0 && index + 1 < id.Length ? id[(index + 1)..] : null;
    }
}

/// <summary>
/// 为通知区域的托盘图标取到它真正的图标位图。
/// </summary>
/// <remarks>
/// 通知区域由 explorer 绘制，UI Automation 报出的进程永远是 explorer，元素上也没有图像接口，
/// 所以「这个图标属于哪个程序」必须从别处问。按可靠程度依次尝试：
///
/// <list type="number">
/// <item><b>Windows 自己的登记表</b>（<see cref="TrayIconCatalog"/>）：按提示文字对上号，
/// 直接拿它保存的图标 PNG。那是系统此刻真的画在通知区域上的那枚图标，最准；</item>
/// <item><b>登记表里的可执行文件名</b>：有些程序（实测 QQ）登记的提示是空的，
/// 但它的 exe 名恰好就是界面上的名字，用这一点对上号；</item>
/// <item><b>按名字找运行中的窗口</b>：拿名字去已运行的顶层窗口里找同一个程序，
/// 从它的 exe 取图标。便宜，所以排在要读磁盘的开始菜单前面；</item>
/// <item><b>「开始」菜单</b>：按名字找到快捷方式，解析它的目标 exe 再取图标。
/// 这一条覆盖「界面上的名字与程序内部名字毫无关系」的情况——实测微信就是这样：
/// 托盘提示是「微信」，而 Weixin.exe 的文件描述是「Weixin」，两者没有任何公共前缀。</item>
/// </list>
///
/// <para>
/// 四条都失败才返回 <c>null</c>，由面板画占位图。
/// </para>
///
/// <para>
/// 不从任务栏截像素作为兜底：截进来的是图标与任务栏背景的混合，
/// 粘到面板上就是一块块深色方块，比占位图更糟。
/// </para>
/// </remarks>
public sealed class TrayIconResolver
{
    /// <summary>名字短于这个长度就不做模糊匹配，免得把无关的东西认成同一个。</summary>
    private const int MinimumFuzzyLength = 2;

    /// <summary>已经成功解析过的图标。只记成功项，失败必须能在下一次刷新时重试。</summary>
    private readonly Dictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>「开始」菜单快照，第一次真的要用时才扫。</summary>
    private IReadOnlyList<StartMenuShortcut>? _shortcuts;

    /// <summary>
    /// 为一批托盘图标解析位图，结果与输入按下标一一对应，解析不到的项是 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 登记表与顶层窗口快照各只取一次：逐个图标各读一遍注册表、各枚举一遍所有窗口，
    /// 会把一次刷新变成几十次系统调用。本方法要做注册表、磁盘与 COM 调用，
    /// 必须在后台线程上调用。
    /// </remarks>
    public IReadOnlyList<ImageSource?> Resolve(IReadOnlyList<TrayIconEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var resolved = new ImageSource?[entries.Count];

        if (entries.Count == 0)
        {
            return resolved;
        }

        var catalog = TrayIconCatalog.Load();
        var byExecutable = IndexByExecutable(catalog.Records);
        IReadOnlyList<WindowIdentityInfo>? windows = null;

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];

            if (_cache.TryGetValue(entry.Id, out var cached))
            {
                resolved[index] = cached;
                continue;
            }

            var record = FindRecord(entry.Id, catalog.Records);
            ImageSource? icon = FromRecord(record) ?? ExtractFromExecutable(record?.ExecutablePath);

            if (icon is null)
            {
                windows ??= WindowLocator.EnumerateTopLevelWindows();
                icon = FromRunningWindow(entry.Id, windows);
            }

            if (icon is null)
            {
                icon = FromStartMenu(entry.Id, byExecutable);
            }

            if (icon is not null)
            {
                _cache[entry.Id] = icon;
            }

            resolved[index] = icon;
        }

        return resolved;
    }

    /// <summary>
    /// 界面上的名字可能带状态后缀，也可能被冒号分成「程序名 + 细节」。
    /// </summary>
    /// <remarks>
    /// UI Automation 报的是当前状态（<c>"QQ: 2719630640"</c>），
    /// 而程序自己的名字往往只是冒号前那一段（<c>"QQ"</c>）。
    /// </remarks>
    private static List<string> NameCandidates(string id)
    {
        var candidates = new List<string>(2) { id.Trim() };

        var colon = id.IndexOf(':', StringComparison.Ordinal);

        if (colon > 0)
        {
            var head = id[..colon].Trim();

            if (head.Length >= MinimumFuzzyLength && !candidates.Contains(head, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(head);
            }
        }

        return candidates;
    }

    private static Dictionary<string, TrayIconRecord> IndexByExecutable(IReadOnlyList<TrayIconRecord> records)
    {
        var index = new Dictionary<string, TrayIconRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in records)
        {
            if (!string.IsNullOrWhiteSpace(record.ExecutablePath))
            {
                index[record.ExecutablePath] = record;
            }
        }

        return index;
    }

    /// <summary>
    /// 按提示文字在登记表里找这一条。
    /// </summary>
    /// <remarks>
    /// 先要完全相等，再退到前缀关系——两边都可能更长：界面报的是当前状态，
    /// 登记表存的是首次登记的提示（<c>"Steam - 正在同步"</c> 对应界面上的 <c>"Steam"</c>）。
    /// 前缀匹配取最长的那条，免得 <c>"QQ"</c> 抢走 <c>"QQ音乐"</c> 的图标。
    /// </remarks>
    /// <summary>
    /// 在登记表里找这个图标对应的那一条：先按提示文字，再按可执行文件名。
    /// </summary>
    /// <remarks>
    /// 返回记录而不是图标，因为调用方有时要的是「它属于哪个程序」（判断折叠区要排除谁），
    /// 有时要的是图标。两条匹配顺序与从前一致。
    /// </remarks>
    private static TrayIconRecord? FindRecord(string id, IReadOnlyList<TrayIconRecord> records)
        => MatchByTooltip(id, records) ?? MatchByExecutableName(id, records);

    /// <summary>
    /// 按提示文字匹配。
    /// </summary>
    /// <remarks>
    /// 先要完全相等，再退到前缀关系——两边都可能更长：界面报的是当前状态，
    /// 登记表存的是首次登记的提示（<c>"Steam - 正在同步"</c> 对应界面上的 <c>"Steam"</c>）。
    /// 前缀匹配取最长的那条，免得 <c>"QQ"</c> 抢走 <c>"QQ音乐"</c> 的图标。
    /// </remarks>
    private static TrayIconRecord? MatchByTooltip(string id, IReadOnlyList<TrayIconRecord> records)
    {
        if (id.Length < MinimumFuzzyLength)
        {
            return null;
        }

        TrayIconRecord? best = null;

        foreach (var record in records)
        {
            var tooltip = record.Tooltip.Trim();

            if (tooltip.Length < MinimumFuzzyLength)
            {
                continue;
            }

            if (string.Equals(tooltip, id, StringComparison.OrdinalIgnoreCase))
            {
                return record;
            }

            if (!tooltip.StartsWith(id, StringComparison.OrdinalIgnoreCase)
                && !id.StartsWith(tooltip, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (best is null || tooltip.Length > best.Tooltip.Trim().Length)
            {
                best = record;
            }
        }

        return best;
    }

    /// <summary>
    /// 按登记表里记的可执行文件名匹配。
    /// </summary>
    /// <remarks>
    /// 只做完全相等，不做前缀：<c>QQMusic.exe</c> 的前缀正是 <c>QQ</c>，
    /// 一旦允许前缀，QQ 的图标就会被 QQ 音乐顶掉。
    /// </remarks>
    private static TrayIconRecord? MatchByExecutableName(string id, IReadOnlyList<TrayIconRecord> records)
    {
        var candidates = NameCandidates(id);

        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.ExecutablePath))
            {
                continue;
            }

            var fileName = Path.GetFileNameWithoutExtension(record.ExecutablePath);

            if (fileName.Length < MinimumFuzzyLength)
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                if (string.Equals(fileName, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return record;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 列出折叠区里的第三方软件：所属程序正在运行、但此刻没显示在通知区域的那些。
    /// </summary>
    /// <remarks>
    /// 与可见图标同源（<see cref="TrayIconCatalog"/> 的快照 PNG），所以画出来是同一套真实图标。
    /// 判据与「不重复显示」的理由见 <see cref="TrayIconCatalog.ListRunningButHidden"/>；
    /// 这里负责的是把可见项对应的程序路径先算出来——包括只能靠「开始」菜单才对上号的那些，
    /// 否则微信这类会被当成折叠区里的图标再显示一次。
    /// </remarks>
    public IReadOnlyList<TrayOverflowItem> ListOverflow(IReadOnlyList<TrayIconEntry> visible)
    {
        ArgumentNullException.ThrowIfNull(visible);

        var catalog = TrayIconCatalog.Load();
        var byExecutable = IndexByExecutable(catalog.Records);
        var visibleExecutables = new List<string>(visible.Count);

        foreach (var entry in visible)
        {
            var path = FindRecord(entry.Id, catalog.Records)?.ExecutablePath;

            if (string.IsNullOrWhiteSpace(path)
                && FindShortcut(entry.Id) is { } shortcut
                && ShortcutResolver.Resolve(shortcut.Path) is { Target.Length: > 0 } info)
            {
                path = info.Target;
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                visibleExecutables.Add(path);
            }
        }

        var items = new List<TrayOverflowItem>();

        foreach (var record in catalog.ListRunningButHidden(visibleExecutables))
        {
            if (FromRecord(record) is not { } icon || string.IsNullOrWhiteSpace(record.ExecutablePath))
            {
                continue;
            }

            var title = string.IsNullOrWhiteSpace(record.Tooltip)
                ? Path.GetFileNameWithoutExtension(record.ExecutablePath)
                : record.Tooltip.Trim();

            // 用登记项的 UID 做标识，而不是程序路径或显示名：
            // 同一个软件多开时（两个 QQ 账号）会有多条登记项，它们的程序路径与显示名
            // 都可能一模一样，按那两个去重就会把第二个号吃掉。
            items.Add(new TrayOverflowItem(
                TrayOverflowId.Compose(record.Key, record.ExecutablePath),
                title,
                record.ExecutablePath,
                icon));
        }

        return items;
    }

    /// <summary>
    /// 拿名字去已运行的顶层窗口里找同一个程序，再从它的可执行文件取图标。
    /// </summary>
    private static BitmapSource? FromRunningWindow(string id, IReadOnlyList<WindowIdentityInfo> windows)
    {
        foreach (var window in windows)
        {
            if (string.IsNullOrEmpty(window.ImagePath) || !MatchesWindow(id, window.Title))
            {
                continue;
            }

            var icon = ExtractFromExecutable(window.ImagePath);

            if (icon is not null)
            {
                return icon;
            }
        }

        return null;
    }

    /// <summary>
    /// 按名字在「开始」菜单里找快捷方式，解析它的目标程序。
    /// </summary>
    /// <remarks>
    /// 只解析命中的那一条：把整个菜单的快捷方式全解析一遍是几百次 COM 调用，
    /// 而这里每次刷新最多只有几个图标需要走到这一步。
    /// </remarks>
    private ImageSource? FromStartMenu(string id, Dictionary<string, TrayIconRecord> byExecutable)
    {
        if (FindShortcut(id) is not { } shortcut)
        {
            return null;
        }

        // 快捷方式的目标正好有登记记录时优先用登记表：那才是系统此刻画在通知区域上的那枚图标。
        if (ShortcutResolver.Resolve(shortcut.Path) is not { } info || string.IsNullOrWhiteSpace(info.Target))
        {
            return null;
        }

        if (byExecutable.TryGetValue(info.Target, out var record)
            && FromRecord(record) is { } fromRecord)
        {
            return fromRecord;
        }

        return ExtractFromExecutable(info.Target);
    }

    private StartMenuShortcut? FindShortcut(string id)
    {
        if (id.Length < MinimumFuzzyLength)
        {
            return null;
        }

        _shortcuts ??= StartMenuScanner.Scan().Shortcuts;

        StartMenuShortcut? best = null;

        foreach (var shortcut in _shortcuts)
        {
            var name = shortcut.Name.Trim();

            if (name.Length < MinimumFuzzyLength)
            {
                continue;
            }

            if (string.Equals(name, id, StringComparison.OrdinalIgnoreCase))
            {
                return shortcut;
            }

            if (!name.StartsWith(id, StringComparison.OrdinalIgnoreCase)
                && !id.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (best is null || name.Length > best.Name.Trim().Length)
            {
                best = shortcut;
            }
        }

        return best;
    }

    /// <summary>
    /// 这个托盘图标属于哪个程序；认不出来时返回 null。
    /// </summary>
    /// <remarks>
    /// 给「退出」这类需要作用到程序本身的操作使用：图标只是界面，
    /// 要结束的是它背后那个进程。
    /// </remarks>
    public string? ResolveExecutable(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (TrayOverflowId.TryReadPath(id) is { Length: > 0 } overflowPath)
        {
            return overflowPath;
        }

        var catalog = TrayIconCatalog.Load();

        if (FindRecord(id, catalog.Records)?.ExecutablePath is { Length: > 0 } path)
        {
            return path;
        }

        if (FindShortcut(id) is { } shortcut
            && ShortcutResolver.Resolve(shortcut.Path) is { Target.Length: > 0 } info)
        {
            return info.Target;
        }

        return null;
    }

    private static bool MatchesWindow(string id, string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || id.Length < MinimumFuzzyLength)
        {
            return false;
        }

        if (string.Equals(id, title, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var candidate in NameCandidates(id))
        {
            if (candidate.Length < MinimumFuzzyLength)
            {
                continue;
            }

            if (title.Contains(candidate, StringComparison.OrdinalIgnoreCase)
                || candidate.Contains(title, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 用登记表里的图标快照。它是带 alpha 的 PNG，直接解码即可。
    /// </summary>
    private static BitmapImage? FromRecord(TrayIconRecord? record)
    {
        if (record?.SnapshotPng is not { Length: > 0 } png)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(png, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            // OnLoad：整块读完再释放流，否则这里 using 掉之后图像就成了空的。
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException
            or IOException or OverflowException)
        {
            return null;
        }
    }

    private static BitmapSource? ExtractFromExecutable(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        return ToImageSource(IconExtractor.Extract(imagePath));
    }

    private static BitmapSource? ToImageSource(ExtractedIcon? icon)
    {
        if (icon is null || icon.Width <= 0 || icon.Height <= 0)
        {
            return null;
        }

        // 取到的像素是直通 alpha（非预乘），所以用 Bgra32 而不是 Pbgra32。
        var bitmap = BitmapSource.Create(
            icon.Width,
            icon.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            icon.Bgra,
            icon.Width * 4);

        // 在后台线程创建、要交给界面线程画，必须冻结。
        bitmap.Freeze();
        return bitmap;
    }
}
