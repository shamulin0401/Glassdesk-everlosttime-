namespace GlassDesk.Core.Models;

/// <summary>
/// 把「现在有哪些顶层窗口」折成「Dock 右侧要显示哪几个应用磁贴」。
/// </summary>
/// <remarks>
/// 纯函数：每次窗口集合变化都要重算，而枚举本身可能失败，
/// 分组必须永远能算出来，也不能有副作用。
///
/// <para>
/// <b>必须排除本进程自己的窗口。</b>Dock、启动台、设置中心都是可见、有标题、
/// 无属主、非工具窗口的顶层窗口，正好符合窗口枚举的候选规则；不排除的话
/// GlassDesk 会把自己的三个窗口当成「正在运行的应用」显示在自己的 Dock 上。
/// </para>
/// </remarks>
public static class RunningAppPolicy
{
    /// <summary>
    /// 为一个还没有归属的窗口造分组键：优先应用身份，其次可执行文件路径，最后退回进程 id。
    /// </summary>
    /// <remarks>
    /// 只在<strong>新建</strong>组时用到；已有组的归属见 <see cref="Group"/> 的合并规则。
    /// </remarks>
    public static string ComposeKey(string? appId, string? imagePath, uint processId)
    {
        if (!string.IsNullOrWhiteSpace(appId))
        {
            return "aumid:" + appId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            return "path:" + imagePath.Trim();
        }

        return "pid:" + processId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 按应用把窗口分组，排除本进程自己的窗口；组的顺序按各组首个窗口出现的顺序。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>两个身份任一相同即视为同一应用。</b>只看应用身份会把「同一 exe 的部分窗口
    /// 没有显式身份」拆成两个磁贴；只看路径又会把「打包应用 / 多进程宿主的不同 exe」
    /// 拆成两个。所以先按任一身份找已归属的组，命中就并过去，并把另一个身份也登记到
    /// 同一个组——后出现的身份因此不会另起一组。
    /// </para>
    /// <para>
    /// 保持「首次出现」的顺序而不是排序，是为了让磁贴位置稳定：
    /// 窗口枚举是 z 序，用户切到某个窗口时它排到最前，磁贴跟着跳会让整条右侧
    /// 无法形成固定的肌肉记忆。已存在的组只追加窗口，不改变自身位置。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<RunningAppGroup> Group(
        IReadOnlyList<RunningWindow> windows,
        uint ownProcessId)
    {
        ArgumentNullException.ThrowIfNull(windows);

        var order = new List<string>();
        var buckets = new Dictionary<string, List<RunningWindow>>(StringComparer.OrdinalIgnoreCase);
        var appIds = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var imagePaths = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // 身份 → 已归属的组键。两个字典指向同一个组时，两个身份就算并到了一起。
        var byAppId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byImagePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var window in windows)
        {
            if (window.ProcessId == ownProcessId)
            {
                continue;
            }

            // Windows 自己的外壳宿主不算「用户打开的应用」。
            //
            // 输入体验（TextInputHost）、开始菜单/搜索/外壳体验宿主这些窗口同样满足
            // 「无主、可见、有标题」，于是会以「TextInputHost / Windows 输入体验」这种名字
            // 混进运行条，用户既没打开过它、也点不出什么。
            //
            // 名单刻意**很短**：只收「确定不是用户应用」的宿主。
            // 尤其不能把 ApplicationFrameHost 收进来——所有 UWP 应用都由它承载，
            // 排掉它等于把用户打开的那些应用一起藏掉。
            if (window.ImagePath is { Length: > 0 } hostPath)
            {
                var hostName = System.IO.Path.GetFileNameWithoutExtension(hostPath);

                if (hostName is "TextInputHost" or "StartMenuExperienceHost" or "SearchHost"
                    or "ShellExperienceHost" or "SearchApp" or "Widgets" or "WidgetService" or "LockApp")
                {
                    continue;
                }
            }

            var appId = string.IsNullOrWhiteSpace(window.AppId) ? null : window.AppId.Trim();
            var imagePath = string.IsNullOrWhiteSpace(window.ImagePath) ? null : window.ImagePath.Trim();

            string? key = null;

            if (appId is not null)
            {
                _ = byAppId.TryGetValue(appId, out key);
            }

            if (key is null && imagePath is not null
                && byImagePath.TryGetValue(imagePath, out var pathKey)
                && (appId is null
                    || appIds[pathKey] is null
                    || string.Equals(appIds[pathKey], appId, StringComparison.OrdinalIgnoreCase)))
            {
                // 只有在「两边都带着身份、且身份不同」时才拒绝按映像路径并组。
                //
                // 身份不同却共享同一个宿主进程是常态（ApplicationFrameHost.exe 承载所有 UWP 应用，
                // chrome.exe、java.exe 这类多进程宿主同理），凭宿主路径强行并组会把两个应用
                // 变成一个磁贴——这正是 WindowMatcher 那边明文禁止的事，这里不能留第二个入口。
                //
                // 但另外两种情形必须继续允许：
                //   · 本窗口没有身份（经典 Win32 程序的常态）——它就该并进那个已认领的组；
                //   · 目标组还没有身份——由本窗口把身份补上（先建组、后认领的反向顺序）。
                key = pathKey;
            }

            if (key is null)
            {
                key = ComposeKey(appId, imagePath, window.ProcessId);

                // 组合出来的键也必须能被后来的窗口找到，否则同一个进程的第二个窗口
                // 会重新组合出同一个键、把已有桶清空（丢掉第一个窗口）再排一次序，
                // 结果是一个窗口消失、同一应用出现两个磁贴。
                if (!buckets.ContainsKey(key))
                {
                    buckets[key] = [];
                    appIds[key] = null;
                    imagePaths[key] = null;
                    order.Add(key);
                }
            }

            if (appId is not null)
            {
                byAppId[appId] = key;
                appIds[key] ??= appId;
            }

            if (imagePath is not null)
            {
                byImagePath[imagePath] = key;
                imagePaths[key] ??= imagePath;
            }

            buckets[key].Add(window);
        }

        var groups = new List<RunningAppGroup>(order.Count);

        foreach (var key in order)
        {
            groups.Add(new RunningAppGroup(key, appIds[key], imagePaths[key], buckets[key]));
        }

        return groups;
    }
}
