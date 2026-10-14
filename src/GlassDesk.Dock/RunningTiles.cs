using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Platform.Shell;

namespace GlassDesk.Dock;

/// <summary>
/// Dock 右侧的一个「正在运行的应用」磁贴。
/// </summary>
/// <param name="Id">磁贴 id，带 <c>running:</c> 前缀，与内置入口和应用库条目区分开。</param>
/// <param name="Group">这一组窗口，点击与悬停预览都基于它。</param>
/// <param name="DisplayName">展示名与悬停提示的主标题。</param>
/// <param name="ToolTip">完整悬停提示。</param>
/// <param name="Icon">应用图标；取不到时为 <c>null</c>，磁贴退回字形。</param>
public sealed record RunningTile(
    string Id,
    RunningAppGroup Group,
    string DisplayName,
    string ToolTip,
    ImageSource? Icon);

/// <summary>
/// 把「现在开着哪些窗口」变成 Dock 右侧的磁贴。
/// </summary>
/// <remarks>
/// 分组本身是 <see cref="RunningAppPolicy"/> 里的纯函数；这里只负责三件带副作用的事：
/// 应用「合并固定图标」规则把已固定的应用窗口摘出去、给每一组取一个像样的名字、
/// 以及取图标。三者都按需缓存，因为它们都要碰文件系统。
/// </remarks>
internal sealed class RunningTiles
{
    /// <summary>磁贴 id 前缀。不能是 <c>entry:</c>：那个前缀是内置入口的契约，会被排除出排序与布局。</summary>
    internal const string IdPrefix = "running:";

    private readonly Dictionary<string, ImageSource> _icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按当前设置与窗口快照生成磁贴；开关关闭或没有窗口时返回空。</summary>
    public IReadOnlyList<RunningTile> Build(AppServiceHost? services, DockSettings settings, double scale)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (services is null || !settings.ShowRunningApps)
        {
            return [];
        }

        var snapshot = services.Running.LastWindows;

        if (snapshot.Count == 0)
        {
            return [];
        }

        var consumed = settings.MergePinnedWithRunning ? ConsumedByPinned(services) : null;

        var windows = new List<RunningWindow>(snapshot.Count);

        foreach (var window in snapshot)
        {
            if (consumed is not null && consumed.Contains(window.Handle))
            {
                continue;
            }

            windows.Add(new RunningWindow(
                window.Handle,
                window.Title,
                window.ProcessId,
                window.IsMinimized,
                window.ImagePath,
                window.EffectiveAumid));
        }

        var groups = RunningAppPolicy.Group(windows, (uint)Environment.ProcessId);

        if (groups.Count == 0)
        {
            return [];
        }

        var tiles = new List<RunningTile>(groups.Count);

        foreach (var group in groups)
        {
            var name = ResolveName(group, services);
            tiles.Add(new RunningTile(
                IdPrefix + group.Key,
                group,
                name,
                BuildToolTip(name, group),
                ResolveIcon(group, services, scale)));
        }

        return tiles;
    }

    /// <summary>
    /// 已被固定图标显示的窗口句柄。
    /// </summary>
    /// <remarks>
    /// 用「窗口」而不是「应用身份」做合并依据，是因为判定「这条固定项对应哪个正在运行的应用」
    /// 正是 <see cref="WindowMatcher"/> 已经在做的事（身份优先、退回可执行文件路径），
    /// 再写一套并行规则迟早会和它不一致。
    /// </remarks>
    private static HashSet<nint> ConsumedByPinned(AppServiceHost services)
    {
        var consumed = new HashSet<nint>();
        var pinned = services.Layouts.Dock.PinnedItemIds;

        foreach (var id in pinned)
        {
            var entry = services.Library.Find(id);

            if (entry is null || !entry.IsUsable)
            {
                continue;
            }

            foreach (var window in services.Running.FindWindows(entry))
            {
                _ = consumed.Add(window.Handle);
            }
        }

        return consumed;
    }

    /// <summary>
    /// 给一组窗口取名字：应用库里的名字最准，其次是可执行文件自己的描述，
    /// 再其次是文件名，最后才退回窗口标题。
    /// </summary>
    /// <remarks>
    /// 顺序是有意的：窗口标题通常是「未命名 - 记事本」这种带文档名的串，
    /// 拿它当应用名会让磁贴提示读起来像文档列表；而 <c>FileDescription</c>
    /// 是程序自己声明的显示名（记事本、计算器），与系统任务管理器里看到的一致。
    /// </remarks>
    private static string ResolveName(RunningAppGroup group, AppServiceHost services)
    {
        if (FindLibraryEntry(group, services) is { DisplayName: { Length: > 0 } libraryName })
        {
            return libraryName;
        }

        if (!string.IsNullOrWhiteSpace(group.ImagePath))
        {
            var description = TryReadFileDescription(group.ImagePath);

            if (!string.IsNullOrWhiteSpace(description))
            {
                return description;
            }

            var fileName = Path.GetFileNameWithoutExtension(group.ImagePath);

            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
        }

        return group.Windows.Count > 0 && !string.IsNullOrWhiteSpace(group.Windows[0].Title)
            ? group.Windows[0].Title.Trim()
            : "正在运行的应用";
    }

    private static string? TryReadFileDescription(string imagePath)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(imagePath).FileDescription?.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or System.ComponentModel.Win32Exception)
        {
            // 程序集信息读不出来是常事（绿色版、被裁剪过的 exe、权限不足），不是错误。
            return null;
        }
    }

    /// <summary>在这组窗口的身份或可执行文件路径上找一条应用库记录。</summary>
    private static Core.Models.AppEntry? FindLibraryEntry(RunningAppGroup group, AppServiceHost services)
    {
        foreach (var entry in services.Library.Items)
        {
            if (!entry.IsUsable || string.IsNullOrWhiteSpace(entry.Target))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(group.AppId)
                && !string.IsNullOrWhiteSpace(entry.AppUserModelId)
                && string.Equals(entry.AppUserModelId, group.AppId, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }

            if (!string.IsNullOrWhiteSpace(group.ImagePath)
                && string.Equals(entry.Target, group.ImagePath, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private ImageSource? ResolveIcon(RunningAppGroup group, AppServiceHost services, double scale)
    {
        var cacheKey = group.ImagePath ?? group.Key;

        if (_icons.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var icon = ResolveLibraryIcon(group, services) ?? ExtractFromExecutable(group.ImagePath, scale);

        if (icon is not null)
        {
            _icons[cacheKey] = icon;
        }

        return icon;
    }

    /// <summary>应用库已经有了按内容缓存的 PNG，优先用它，能得到与固定图标一致的画质。</summary>
    private static BitmapImage? ResolveLibraryIcon(RunningAppGroup group, AppServiceHost services)
    {
        var entry = FindLibraryEntry(group, services);

        if (entry is null || string.IsNullOrWhiteSpace(entry.IconSource))
        {
            return null;
        }

        var cached = services.Icons.EnsureFor(entry.IconSource);

        return cached is null ? null : LoadFrozen(cached.Path);
    }

    /// <summary>直接从可执行文件取图标；这条路径覆盖不在应用库里的程序。</summary>
    private static BitmapSource? ExtractFromExecutable(string? imagePath, double scale)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        var extracted = IconExtractor.Extract(imagePath);

        if (extracted is null || extracted.Width <= 0 || extracted.Height <= 0)
        {
            return null;
        }

        // 取到的像素是直通 alpha（非预乘），所以用 Bgra32 而不是 Pbgra32。
        var bitmap = BitmapSource.Create(
            extracted.Width,
            extracted.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            extracted.Bgra,
            extracted.Width * 4);

        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapImage? LoadFrozen(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or NotSupportedException)
        {
            return null;
        }
    }

    private static string BuildToolTip(string name, RunningAppGroup group)
    {
        var minimized = group.MinimizedCount;

        var summary = group.Windows.Count == 1
            ? "1 个窗口"
            : minimized > 0
                ? $"{group.Windows.Count} 个窗口（{minimized} 个已最小化）"
                : $"{group.Windows.Count} 个窗口";

        return $"{name} · {summary}";
    }
}
