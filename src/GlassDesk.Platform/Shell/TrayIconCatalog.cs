using System.IO;
using Microsoft.Win32;

namespace GlassDesk.Platform.Shell;

/// <summary>
/// Windows 自己为某个通知区域图标保存的记录。
/// </summary>
/// <param name="Tooltip">登记时的提示文字；部分记录为空。</param>
/// <param name="ExecutablePath">所属程序的可执行文件路径；解析不出来时为空。</param>
/// <param name="SnapshotPng">图标本身，带 alpha 的 PNG 字节；没有时为空。</param>
/// <param name="Key">登记项的子键名，一条登记项的唯一标识。</param>
/// <remarks>
/// 不拿 <c>UID</c> 当标识：实测它<b>不是全局唯一</b>——不同程序都会出现 <c>1</c>、<c>3</c>
/// 这样的值，有些登记项干脆没有。子键名（一段哈希）才是一条登记项的身份，
/// 而且它在重启后保持稳定，用来存用户的固定/隐藏偏好才不会串。
/// </remarks>
public sealed record TrayIconRecord(string Tooltip, string? ExecutablePath, byte[]? SnapshotPng, string Key);

/// <summary>
/// 读取 Windows 为通知区域图标保存的快照：图标位图与它属于哪个程序。
/// </summary>
/// <remarks>
/// 位置是 <c>HKCU\Control Panel\NotifyIconSettings</c>——「设置 → 个性化 → 任务栏 →
/// 其他系统托盘图标」那一页读的就是它。
///
/// <para>
/// <b>这是唯一能把托盘图标与所属程序对上号的地方。</b>通知区域由 explorer 绘制：
/// 从 UI Automation 元素上读到的进程永远是 explorer，元素上也没有任何图像接口，
/// 所以既问不出「这个图标属于谁」，也拿不到位图。经典做法是去读
/// <c>ToolbarWindow32</c> 的按钮与图像列表，但 Windows 11 的通知区域已经改为 XAML 岛，
/// 整机范围内不存在 <c>ToolbarWindow32</c>／<c>SysPager</c>，那条路没有目标可读。
/// </para>
///
/// <para>
/// 快照是带 alpha 的 PNG（实测 20×20 到 256×256 不等），也就是系统此刻显示的那枚图标：
/// 既不必从任务栏截像素（截进来的是图标与任务栏背景的混合，粘到面板上是一块深色方块），
/// 也不必猜图标属于哪个 exe。
/// </para>
/// </remarks>
public sealed class TrayIconCatalog
{
    /// <summary>当前用户的托盘图标登记表。</summary>
    private const string RootPath = @"Control Panel\NotifyIconSettings";

    private TrayIconCatalog(IReadOnlyList<TrayIconRecord> records) => Records = records;

    /// <summary>登记表的短缓存：一次刷新内的多次读取合并成一次。</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(3);
    private static readonly object CacheGate = new();
    private static TrayIconCatalog? _cached;
    private static DateTime _cachedAtUtc;

    /// <summary>
    /// 丢掉登记表缓存，让下一次 <see cref="Load"/> 重新读注册表。
    /// </summary>
    /// <remarks>
    /// 用户在面板上按刷新、或面板每次自动刷新时调用：新装的程序、刚登录的第二个账号
    /// 都要立刻出现，不能等缓存过期。
    /// </remarks>
    public static void Invalidate()
    {
        lock (CacheGate)
        {
            _cached = null;
        }
    }

    /// <summary>登记过的全部记录；读不到时为空表。</summary>
    public IReadOnlyList<TrayIconRecord> Records { get; }

    /// <summary>
    /// 读一次当前用户登记过的托盘图标。
    /// </summary>
    /// <remarks>
    /// 读的内容很短命地缓存几秒：一次面板刷新里这张表会被读三遍
    /// （解析可见图标一遍、列折叠区一遍、按程序定位又是一遍），
    /// 每次都把几十个子键连同图标二进制重新读一遍纯属浪费。
    /// 缓存只有几秒，所以「新装的程序要能出现」仍然成立——
    /// 面板刷新一次通常远快于这个时限，于是三遍并成一遍。
    /// </remarks>
    public static TrayIconCatalog Load()
    {
        var now = DateTime.UtcNow;

        lock (CacheGate)
        {
            if (_cached is not null && now - _cachedAtUtc < CacheLifetime)
            {
                return _cached;
            }

            _cached = LoadUncached();
            _cachedAtUtc = now;
            return _cached;
        }
    }

    private static TrayIconCatalog LoadUncached()
    {
        var records = new List<TrayIconRecord>();

        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(RootPath);

            if (root is null)
            {
                return new TrayIconCatalog(records);
            }

            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);

                if (key is null)
                {
                    continue;
                }

                records.Add(new TrayIconRecord(
                    key.GetValue("InitialTooltip") as string ?? string.Empty,
                    ResolveExecutablePath(key.GetValue("ExecutablePath") as string),
                    key.GetValue("IconSnapshot") as byte[],
                    name));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
            or UnauthorizedAccessException or IOException)
        {
            // 注册表被策略锁住、键在枚举中途被删掉：当作「这一轮读不到」，返回已经读到的部分。
        }

        return new TrayIconCatalog(records);
    }

    /// <summary>
    /// 列出「所属程序正在运行、但此刻没有显示在通知区域」的登记项——也就是折叠区里的那些。
    /// </summary>
    /// <remarks>
    /// <b>刻意不去展开系统浮层枚举。</b>Windows 只在溢出浮层被打开时才把那些图标放进
    /// UI Automation 树，展开它会在用户屏幕上闪一下，而信息其实注册表里就有：
    /// 每条登记项都带图标快照与所属程序。
    ///
    /// <para>
    /// 因此判据是两条一起用：
    /// <list type="number">
    /// <item><b>所属程序正在运行</b>——登记表是累积的，装过又卸载、或早已退出的程序也留在里面。
    /// 只看登记表会把一堆早就没在跑的东西显示出来，而用户要的是「正在运行的软件」；</item>
    /// <item><b>没有出现在通知区域</b>——调用方把当前可见的那些程序告诉这里，
    /// 否则同一个图标会既在可见区出现一次，又被当成折叠区的再出现一次。</item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <param name="visibleExecutables">当前已显示在通知区域里的程序路径；比较忽略大小写。</param>
    public IReadOnlyList<TrayIconRecord> ListRunningButHidden(IReadOnlyCollection<string>? visibleExecutables)
    {
        var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var executable in visibleExecutables ?? [])
        {
            if (!string.IsNullOrWhiteSpace(executable))
            {
                visible.Add(executable);
            }
        }

        var runningNames = ReadRunningProcessNames();
        var hidden = new List<TrayIconRecord>();

        foreach (var record in Records)
        {
            if (string.IsNullOrWhiteSpace(record.ExecutablePath)
                || record.SnapshotPng is not { Length: > 0 }
                || visible.Contains(record.ExecutablePath)
                || !IsExecutableRunning(record.ExecutablePath, runningNames)

                // 外壳自己的宿主进程不显示：网络、音量、电源这些系统指示其实就登记在
                // explorer.exe 名下，用户要的是「自己打开的软件」，不是把系统指示再来一遍。
                || IsHostExecutable(record.ExecutablePath))
            {
                continue;
            }

            hidden.Add(record);
        }

        return hidden;
    }

    /// <summary>这条记录是不是由 Windows 自己的外壳宿主进程登记的。</summary>
    /// <remarks>
    /// 判据是<b>发行者</b>而不是进程名。我原先按名字列过一张表，那是猜的：
    /// 把 <c>SystrayComponent.exe</c> 当成系统组件排掉了，实际它来自
    /// <c>WindowsApps\CCU.WinUI_…</c> 这个第三方打包应用——正是用户说的
    /// 「隐藏到托盘的程序」里被漏掉的那一类。而真正的系统指示
    /// （网络、音量、电源、Windows 安全中心）都由 Microsoft 自己的组件登记。
    /// </remarks>
    private static bool IsHostExecutable(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);

        foreach (var host in HostProcessNames)
        {
            if (string.Equals(name, host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return string.Equals(ReadPublisher(path), "Microsoft Corporation", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>读可执行文件的发行者；读不到时返回 null。</summary>
    private static string? ReadPublisher(string path)
    {
        try
        {
            return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
            or ArgumentException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Windows 外壳自己承载通知区域图标的进程。
    /// </summary>
    /// <remarks>
    /// 只留真正的外壳宿主：这些进程可能从 <c>C:\Windows</c> 或 <c>C:\Windows\System32</c>
    /// 启动，而登记表里记的是其中之一，只比对某一条路径会漏掉
    /// （实测 explorer.exe 就是这样漏进来的）。其余的 Windows 组件交给发行者判断。
    /// </remarks>
    private static readonly string[] HostProcessNames =
    [
        "explorer",
        "rundll32",
        "sihost",
        "dllhost",
        "shellexperiencehost",
        "startmenuexperiencehost",
        "textinputhost",
        "applicationframehost",
    ];

    /// <summary>
    /// 当前正在运行的进程<b>名字</b>集合。
    /// </summary>
    /// <remarks>
    /// 只要名字，不去读每个进程的映像路径：读映像是逐进程开句柄，本机两百来个进程
    /// 实测要 1.6 秒，而这一层只是用来判断「这个登记项对应的程序还在不在」。
    /// 名字对上的那几个再单独核对完整路径（<see cref="IsExecutableRunning"/>），
    /// 于是开销从「全部进程」降到「名字相符的那几个」。
    ///
    /// <para>
    /// <b>刻意不缓存</b>：同一个软件多开（比如两个 QQ 账号）时，多出来的进程必须
    /// 立刻出现在面板上，哪怕是几秒的缓存也会让用户以为「第二个号没登录成功」。
    /// 名字快照本身很便宜，值得每次现读。
    /// </para>
    /// </remarks>
    private static HashSet<string> ReadRunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var process in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    // ProcessName 来自快照本身，不需要打开进程，因此这里很便宜。
                    if (!string.IsNullOrWhiteSpace(process.ProcessName))
                    {
                        names.Add(process.ProcessName);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return names;
        }

        return names;
    }

    /// <summary>
    /// 这个可执行文件此刻在不在运行。
    /// </summary>
    /// <remarks>
    /// 先用名字集合挡掉绝大多数登记项——登记表是累积的，里面大多是早就没在跑的程序，
    /// 这样一次映像读取都不用做。只有名字对上的才去看完整路径，
    /// 免得同名但不同位置的两个程序互相冒充。读不到映像时按「在运行」处理：
    /// 这一层的用途是决定要不要显示一个图标，漏掉一个真实存在的比多显示一个更糟。
    /// </remarks>
    private static bool IsExecutableRunning(string executablePath, HashSet<string> runningNames)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(executablePath);

        if (name.Length == 0 || !runningNames.Contains(name))
        {
            return false;
        }

        System.Diagnostics.Process[] candidates;

        try
        {
            candidates = System.Diagnostics.Process.GetProcessesByName(name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }

        try
        {
            // 只认「路径确实一致」。读到路径但不一致，就说明这一条登记项属于别的版本/别的程序：
            // 实测 NVIDIA 的显示容器在 DriverStore 里留下过多个旧版本登记项，
            // 若把「读不到映像」当成「在运行」，这些早就没用的登记项会全部冒出来。
            var sawUnreadable = false;

            // 与「读不到」区分开：只要有一条路径读到了，就不该再靠猜。
            var sawReadable = false;

            foreach (var process in candidates)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, executablePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    sawReadable = true;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                    or InvalidOperationException or NotSupportedException)
                {
                    sawUnreadable = true;
                }
            }

            // 只有一条路径都没读到（权限不足）时才倾向于显示：否则「有一个同名进程读不到」
            // 会把某个版本残留的登记项全部复活，那正是上面警告过的 NVIDIA 情形。
            return sawUnreadable && !sawReadable;
        }
        finally
        {
            foreach (var process in candidates)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// 把记录里的可执行文件路径还原成绝对路径。
    /// </summary>
    /// <remarks>
    /// 这个值常常长这样：<c>{6D809377-…}\Microsoft OneDrive\OneDrive.exe</c>——
    /// 前缀是一个 KNOWNFOLDERID 而不是盘符。必须问外壳要那个已知文件夹的真实位置：
    /// Program Files 可以被重定向，猜盘符会在重定向过的机器上指向一个不存在的地方。
    /// </remarks>
    private static string? ResolveExecutablePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();

        if (trimmed[0] != '{')
        {
            return trimmed;
        }

        var close = trimmed.IndexOf('}', StringComparison.Ordinal);

        if (close <= 0 || !Guid.TryParse(trimmed[1..close], out var folderId))
        {
            return trimmed;
        }

        var root = KnownFolders.Resolve(folderId);

        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var rest = trimmed[(close + 1)..].TrimStart('\\');
        return rest.Length == 0 ? root : Path.Combine(root, rest);
    }
}
