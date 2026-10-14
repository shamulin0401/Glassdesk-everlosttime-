using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Contracts;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Dock.Tray;
using GlassDesk.Platform;
using GlassDesk.Platform.Input;
using GlassDesk.Platform.Shell;
using GlassDesk.UI.Controls;
using ShapePath = System.Windows.Shapes.Path;

namespace GlassDesk.Dock;

/// <summary>
/// 程序坞右侧的系统托盘面板：收纳通知区域图标，并提供一小组系统控制�?/// </summary>
/// <remarks>
/// 只做收纳与转发：
/// <list type="bullet">
///   <item>托盘图标来自 <see cref="SystemTrayReader"/>，点一下就是把该图标的默认操作转给原程序�?/item>
///   <item>用户的排序、隐藏与固定只作用于本面板，不改�?Windows 通知区域本身�?/item>
///   <item>「隐藏系统任务栏」写的是外壳自己的自动隐藏状态，不碰任务栏窗口�?/item>
/// </list>
///
/// <para>
/// 面板由程序坞创建与定位，因此自己不决定位置；它只负责内容与状态�?/// </para>
/// </remarks>
public partial class TrayPanelWindow : GlassWindow
{
    /// <summary>输入法与音量轮询间隔：两者都可能被别的程序改掉，面板开着时要跟上�?/summary>
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(1);

    /// <summary>拖动重排时携带的数据格式�?/summary>
    private const string TrayDragFormat = "GlassDesk.TrayIcon";

    private readonly SystemTrayReader _reader = new();
    private readonly TrayIconResolver _resolver = new();
    private readonly SystemVolume _volume = SystemVolume.Open();
    private readonly DispatcherTimer _statusTimer;

    private IReadOnlyList<TrayIconEntry> _entries = [];
    private List<TrayOverflowItem> _overflow = [];
    private IReadOnlyList<TrayItemPreference> _preferences = [];
    private MenuItem? _restoreMenuItem;
    private string? _dragId;
    private Point _dragOrigin;
    private bool _refreshing;

    /// <summary>状态轮询的计数，用来把「隔几次重读一遍托盘」摊开。</summary>
    private int _tickCount;

    /// <summary>每隔这么多次状态轮询重读一遍托盘（当前每秒一次，即约每 3 秒）。</summary>
    private const int RefreshEveryTicks = 3;
    private bool _loadingControls;

    public TrayPanelWindow()
    {
        InitializeComponent();

        PanelCornerRadius = new CornerRadius(22);
        GlassSurface.IsBackdropActive = () => IsVisible;

        _statusTimer = new DispatcherTimer { Interval = StatusInterval };
        _statusTimer.Tick += OnStatusTick;
    }

    /// <summary>
    /// 用户改动排序、隐藏或固定后触发，参数是新的完整偏好表�?    /// </summary>
    /// <remarks>
    /// 面板不持有设置存储：只有宿主能落盘，所以这里只把结果报出去�?    /// </remarks>
    public event EventHandler<IReadOnlyList<TrayItemPreference>>? PreferencesChanged;

    /// <summary>已保存的托盘偏好快照，由程序坞在保存后回填�?/summary>
    public IReadOnlyList<TrayItemPreference> Preferences
    {
        get => _preferences;
        set => _preferences = value ?? [];
    }

    /// <summary>
    /// 读「隐藏系统任务栏」此刻的真实状态；未注入时直接问系统�?    /// </summary>
    /// <remarks>
    /// 由宿主提供而不是面板自己去隐藏：隐藏任务栏要配套拉起守护进程、落盘意图�?    /// 并在退出时恢复，那些都是宿主的职责，面板只做界面�?    /// </remarks>
    public Func<bool>? IsTaskbarHidden { get; set; }

    /// <summary>隐藏或恢复系统任务栏；未注入时直接对系统做。返回是否达到目标状态�?/summary>
    public Func<bool, bool>? SetTaskbarHidden { get; set; }

    /// <summary>本窗口的 HWND，供程序坞定位面板�?/summary>
    internal nint PanelHandle => Handle;

    /// <summary>面板当前�?DIP 尺寸，供程序坞换算成物理像素�?/summary>
    internal Size PanelSizeDip => new(Width, ActualHeight > 0 ? ActualHeight : DesiredSize.Height);

    /// <summary>打开面板并刷新内容�?/summary>
    public void OpenPanel()
    {
        Show();
        _ = WindowActivation.TryBringToForeground(Handle);
        UpdateStatusControls();

        // 每次打开都回到系统控制页：合成器用过一次之后，面板若停在那一页，
        // 下次打开看到的就不是开关和滑块，而是一份没有重建过的设备/会话列表
        // （列表只在点进合成器时才构建）。
        SystemControlsPanel.Visibility = Visibility.Visible;
        MixerPanel.Visibility = Visibility.Collapsed;

        // 设备列表只在打开面板与展开下拉时重新枚举，不进每秒的轮询。
        RefreshAudioDevices();
        _ = RefreshAsync();
        _statusTimer.Start();
    }

    /// <summary>收起面板，但保留窗口供下次复用�?/summary>
    public void ClosePanel()
    {
        _statusTimer.Stop();
        Hide();
    }

    /// <summary>
    /// 重新读取通知区域并按偏好重建图标网格�?    /// </summary>
    /// <remarks>
    /// 刻意不抛异常：刷新既可能由按钮触发，也可能由设置变化触发�?    /// 还有定时路径，任何一处让异常逃出去都会变成未观察的任务异常�?    /// 读取失败时保留上一次的网格，比清空成一片空白更接近「暂时读不到」的真相�?    /// </remarks>
    public async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;

        try
        {
            // 每次刷新都重新读登记表：刚登录的第二个账号、刚启动的程序必须立刻出现，
            // 不能等那几秒的缓存过期。
            TrayIconCatalog.Invalidate();

            var raw = await _reader.ReadAsync().ConfigureAwait(true);
            var arranged = TrayLayoutPolicy.Arrange(raw, _preferences);
            var icons = await Task.Run(() => _resolver.Resolve(arranged)).ConfigureAwait(true);

            // 折叠区里的那些与可见图标并排显示：它们也是用户打开的软件，
            // 只是 Windows 把图标收进了溢出浮层。图标同样来自登记表的快照�?
var overflow = await Task.Run(() => _resolver.ListOverflow(arranged)).ConfigureAwait(true);

            _entries = arranged;

            // 折叠区那条路必须也尊重「隐藏」：它按「程序在跑、但没显示在通知区域」重新算了一遍，
            // 不看偏好，于是用户刚隐藏掉的图标会立刻被它原样加回来——界面上表现为「隐藏没反应」，
            // 而设置里已经记下了隐藏。用户自己的偏好是最终裁决，这里按 id 再筛一次。
            _overflow = [];

            foreach (var item in overflow)
            {
                if (TryGetPreference(item.Id)?.Hidden == true)
                {
                    continue;
                }

                _overflow.Add(item);
            }
            BuildCells(arranged, icons, _overflow);
            UpdateRestoreMenu();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 通知区域正在重建�?UIA 会抛各种异常，这里按「这一轮读不到」处理�?
}
        finally
        {
            _refreshing = false;
        }
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _statusTimer.Stop();
        _statusTimer.Tick -= OnStatusTick;
        _volume.Dispose();
        base.OnClosed(e);
    }

    private void BuildCells(
        IReadOnlyList<TrayIconEntry> entries,
        IReadOnlyList<ImageSource?> icons,
        List<TrayOverflowItem> overflow)
    {
        TrayIconGrid.Children.Clear();

        var hasEntries = entries.Count + overflow.Count > 0;
        IconScroll.Visibility = hasEntries ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = hasEntries ? Visibility.Collapsed : Visibility.Visible;

        for (var index = 0; index < entries.Count; index++)
        {
            TrayIconGrid.Children.Add(CreateCell(entries[index].Id, entries[index].Title, icons[index]));
        }

        foreach (var item in overflow)
        {
            TrayIconGrid.Children.Add(CreateCell(item.Id, item.Title, item.Icon));
        }
    }

    private Button CreateCell(string id, string tooltip, ImageSource? icon)
    {
        var button = new Button
        {
            Style = (Style)FindResource("TrayCellButton"),
            Tag = id,
            ToolTip = tooltip,
            AllowDrop = true,
            Content = CreateCellContent(icon),
        };

        AutomationProperties.SetName(button, id);

        button.Click += OnCellClick;
        button.PreviewMouseLeftButtonDown += OnCellMouseDown;
        button.PreviewMouseMove += OnCellMouseMove;
        button.DragOver += OnCellDragOver;
        button.Drop += OnCellDrop;
        button.ContextMenu = CreateCellMenu(id);
        return button;
    }

    private FrameworkElement CreateCellContent(ImageSource? icon)
    {
        if (icon is not null)
        {
            return new Image
            {
                Source = icon,
                Width = 22,
                Height = 22,
                Stretch = Stretch.Uniform,
            };
        }

        // 解析不到图标（托盘程序没有顶层窗口）时画占位图，而不是留一个空格子�?
// 格子必须仍然可点，用户的排序/隐藏/固定也仍然要能作用在它上面�?
return new ShapePath
        {
            Width = 20,
            Height = 20,
            Stretch = Stretch.Uniform,
            Stroke = SecondaryForegroundBrush,
            StrokeThickness = 1.6,
            Fill = Brushes.Transparent,
            Data = Geometry.Parse("M6,4 H18 Q20,4 20,6 V18 Q20,20 18,20 H6 Q4,20 4,18 V6 Q4,4 6,4 Z M8,9 H16 M8,13 H16"),
        };
    }

    private ContextMenu CreateCellMenu(string id)
    {
        var menu = new ContextMenu();

        // 每次打开时重建，而不是创建格子时写死：进程随时可能多开或退出，
        // 写死的列表会让「结束哪一个」指向一个早就不存在的进程。
        menu.Opened += (_, _) => FillCellMenu(menu, id);
        return menu;
    }

    private void FillCellMenu(ContextMenu menu, string id)
    {
        menu.Items.Clear();

        var pinned = TryGetPreference(id)?.Pinned == true;

        var pin = new MenuItem { Header = pinned ? "取消固定" : "固定到最前" };
        pin.Click += (_, _) => TogglePinned(id);

        var hide = new MenuItem { Header = "在面板中隐藏" };
        hide.Click += (_, _) => HideEntry(id);

        menu.Items.Add(pin);
        menu.Items.Add(hide);
        menu.Items.Add(new Separator());
        menu.Items.Add(BuildQuitMenuItem(id));
    }

    /// <summary>
    /// 构造「退出」菜单项。
    /// </summary>
    /// <remarks>
    /// <b>不列出进程，也不在界面上写「结束进程」这类功能描写。</b>
    /// 同一个程序开了多个进程时（两个 QQ 账号就是同一个 exe 的两个进程），
    /// 光凭程序路径无法知道用户点的是哪一个账号，所以这一项在这种情况下不可用，
    /// 而不是替他赌一把——猜错就会关掉正在用的那个账号。
    /// </remarks>
    private MenuItem BuildQuitMenuItem(string id)
    {
        var executable = ResolveExecutable(id);

        if (string.IsNullOrWhiteSpace(executable))
        {
            return Unavailable("无法确定它属于哪个程序。");
        }

        var processes = ApplicationQuitter.ListProcesses(executable);

        if (processes.Count == 0)
        {
            return Unavailable("它此刻没有在运行。");
        }

        if (processes.Count > 1)
        {
            // 多开时不去猜「点的是哪一个」——路径相同，没有可查的对应关系。
            // 标签按用户要求统一写「退出」，动作是结束这个程序的全部进程。
            var all = new MenuItem { Header = "退出" };
            all.Click += (_, _) => QuitAll(executable);
            return all;
        }

        var only = new MenuItem { Header = "退出" };

        var processId = processes[0].ProcessId;
        only.Click += (_, _) => QuitProcess(processId);
        return only;
    }

    private static MenuItem Unavailable(string reason) => new()
    {
        Header = "退出",
        IsEnabled = false,
        ToolTip = reason,
    };

    /// <summary>结束用户指明的那一个进程：先请它自己关，叫不动才结束它。</summary>
    private void QuitProcess(int processId)
    {
        ClosePanel();

        // 换个线程去做：它要等宽限期、必要时还要再等结束确认，整个流程可达数秒。
        // 直接调用会把 Dispatcher 线程（Dock、启动台、设置中心共用）冻住这么久。
        _ = Task.Run(() => ApplicationQuitter.TryQuitProcess(processId, TimeSpan.FromSeconds(2)));
    }

    /// <summary>结束这个程序此刻的全部进程——由用户点「全部退出」明确要求时才走这里。</summary>
    private void QuitAll(string executable)
    {
        ClosePanel();
        _ = Task.Run(() => ApplicationQuitter.TryQuitAll(executable, TimeSpan.FromSeconds(2)));
    }

    /// <summary>这个托盘图标属于哪个可执行文件；折叠区的条目直接带着路径。</summary>
    private string? ResolveExecutable(string id)
    {
        foreach (var item in _overflow)
        {
            if (string.Equals(item.Id, id, StringComparison.Ordinal))
            {
                return item.ExecutablePath;
            }
        }

        return _resolver.ResolveExecutable(id);
    }

    private TrayItemPreference? TryGetPreference(string id)
    {
        foreach (var preference in _preferences)
        {
            if (string.Equals(preference.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return preference;
            }
        }

        return null;
    }

    private void OnCellClick(object sender, RoutedEventArgs e)
    {
        // 一次「按下即松开」的普通点击不会走拖动那条清空路径，这里必须自己清掉：
        // 留着它会让状态轮询里的自动重读一直以为正在拖动，从此再也不刷新托盘。
        _dragId = null;

        if (sender is not Button { Tag: string id })
        {
            return;
        }

        // 折叠区里的图标没�?UI Automation 元素，转发不了「默认操作」，
        // 只能按程序路径把它的窗口提到前面；没有窗口就把它启动起来�?
foreach (var item in _overflow)
        {
            if (string.Equals(item.Id, id, StringComparison.Ordinal))
            {
                ClosePanel();
                ActivateOverflowItem(item);
                return;
            }
        }

        _ = ActivateEntryAsync(id);
    }

    private static void ActivateOverflowItem(TrayOverflowItem item)
    {
        var candidates = WindowLocator.FindWindowsForImage(item.ExecutablePath);

        if (candidates.Count > 0)
        {
            var outcome = LaunchService.ActivateWindow(candidates[0].Handle);

            if (outcome.Outcome == ActivationOutcome.Activated)
            {
                return;
            }
        }

        // 它可能是个没有窗口的纯托盘程序（QQ 就是）：**不要**在这里替它启动第二份。
        // 用户要的是「调用正在运行的那一个」，多开一份是另一回事；
        // 而托盘程序真正的那套自绘菜单，只有 Windows 的溢出浮层能给出。
        if (ApplicationQuitter.IsRunning(item.ExecutablePath))
        {
            return;
        }

        _ = ShellExecutor.Open(item.ExecutablePath, null, null);
    }

    /// <summary>把点击转给原程序，并先收起面板——与通知区域自己的行为一致�?/summary>
    private async Task ActivateEntryAsync(string id)
    {
        ClosePanel();
        await _reader.TryInvokeAsync(id).ConfigureAwait(true);
    }

    private void OnCellMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            _dragId = id;
            _dragOrigin = e.GetPosition(this);
        }
    }

    private void OnCellMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragId is null || e.LeftButton != MouseButtonState.Pressed || sender is not Button button)
        {
            return;
        }

        var offset = e.GetPosition(this) - _dragOrigin;

        if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var id = _dragId;
        _dragId = null;
        _ = DragDrop.DoDragDrop(button, new DataObject(TrayDragFormat, id), DragDropEffects.Move);
    }

    private static void OnCellDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(TrayDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnCellDrop(object sender, DragEventArgs e)
    {
        if (sender is Button { Tag: string target } && e.Data.GetData(TrayDragFormat) is string source)
        {
            Reorder(source, target);
        }
    }

    /// <summary>
    /// �?<paramref name="sourceId"/> 挪到 <paramref name="targetId"/> 的位置�?    /// </summary>
    /// <remarks>
    /// 重排之后把整个可见次序整体写进偏好，而不是只记相对位置：
    /// 面板是按「固定项优先、其余按 Order」算出来的，只写一部分会让下一次计�?    /// 与用户刚才拖出来的次序对不上�?    /// </remarks>
    private void Reorder(string sourceId, string targetId)
    {
        if (string.Equals(sourceId, targetId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var order = new List<string>(_entries.Count);

        foreach (var entry in _entries)
        {
            order.Add(entry.Id);
        }

        var from = order.FindIndex(id => string.Equals(id, sourceId, StringComparison.OrdinalIgnoreCase));
        var to = order.FindIndex(id => string.Equals(id, targetId, StringComparison.OrdinalIgnoreCase));

        if (from < 0 || to < 0)
        {
            return;
        }

        order.RemoveAt(from);
        order.Insert(to, sourceId);

        var next = ClonePreferences();
        var byId = new Dictionary<string, TrayItemPreference>(StringComparer.OrdinalIgnoreCase);

        foreach (var preference in next)
        {
            byId[preference.Id] = preference;
        }

        for (var index = 0; index < order.Count; index++)
        {
            if (!byId.TryGetValue(order[index], out var preference))
            {
                preference = new TrayItemPreference { Id = order[index] };
                next.Add(preference);
                byId[order[index]] = preference;
            }

            preference.Order = index;
        }

        ApplyPreferences(next);
    }

    private void TogglePinned(string id)
    {
        var next = ClonePreferences();
        var index = next.FindIndex(preference => string.Equals(preference.Id, id, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            next.Add(new TrayItemPreference { Id = id });
            index = next.Count - 1;
        }

        var preference = next[index];

        if (preference.Pinned)
        {
            preference.Pinned = false;
        }
        else
        {
            preference.Pinned = true;
            preference.Hidden = false;
            preference.Order = NextOrder(next);
        }

        ApplyPreferences(next);
    }

    private void HideEntry(string id)
    {
        var next = ClonePreferences();
        var index = next.FindIndex(preference => string.Equals(preference.Id, id, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            next.Add(new TrayItemPreference { Id = id });
            index = next.Count - 1;
        }

        next[index].Hidden = true;
        ApplyPreferences(next);
    }

    /// <summary>把隐藏掉的图标全部放回来�?/summary>
    private void RestoreHidden()
    {
        var next = ClonePreferences();
        var restored = false;

        foreach (var preference in next)
        {
            if (!preference.Hidden)
            {
                continue;
            }

            preference.Hidden = false;
            restored = true;
        }

        if (restored)
        {
            ApplyPreferences(next);
        }
    }

    private void ApplyPreferences(List<TrayItemPreference> next)
    {
        _preferences = next;
        PreferencesChanged?.Invoke(this, next);
        _ = RefreshAsync();
    }

    private List<TrayItemPreference> ClonePreferences()
    {
        var clone = new List<TrayItemPreference>(_preferences.Count);

        foreach (var preference in _preferences)
        {
            clone.Add(new TrayItemPreference
            {
                Id = preference.Id,
                Order = preference.Order,
                Hidden = preference.Hidden,
                Pinned = preference.Pinned,
            });
        }

        return clone;
    }

    private static int NextOrder(List<TrayItemPreference> preferences)
    {
        var maximum = -1;

        foreach (var preference in preferences)
        {
            maximum = Math.Max(maximum, preference.Order);
        }

        return maximum + 1;
    }

    /// <summary>隐藏项在面板里没有入口，所以把「全部恢复」挂在面板背景的右键菜单上�?/summary>
    private void UpdateRestoreMenu()
    {
        var hidden = 0;

        foreach (var preference in _preferences)
        {
            if (preference.Hidden)
            {
                hidden++;
            }
        }

        _restoreMenuItem ??= CreateRestoreMenuItem();
        _restoreMenuItem.Header = $"恢复隐藏的图标（{hidden}）";
        _restoreMenuItem.IsEnabled = hidden > 0;
        PanelContent.ContextMenu ??= CreatePanelMenu();
    }

    private MenuItem CreateRestoreMenuItem()
    {
        var item = new MenuItem { Header = "恢复隐藏的图标" };
        item.Click += (_, _) => RestoreHidden();
        return item;
    }

    private ContextMenu CreatePanelMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(_restoreMenuItem);
        return menu;
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    /// <summary>
    /// 每秒一次：把系统状态拉进控件，并每隔几次顺带重读一遍托盘。
    /// </summary>
    /// <remarks>
    /// 面板开着的时候用户可能刚登录第二个账号、或刚退出一个程序，
    /// 只在打开时读一次会让面板停在旧状态。重读按秒计数的间隔做（而不是每秒），
    /// 因为一次重读要读注册表、解码图标、核对进程，没必要每秒来一遍；
    /// 拖动排序进行中则跳过，免得把用户拖到一半的格子重建掉。
    /// </remarks>
    private void OnStatusTick(object? sender, EventArgs e)
    {
        UpdateStatusControls();

        if (++_tickCount % RefreshEveryTicks != 0 || _dragId is not null)
        {
            return;
        }

        _ = RefreshAsync();
    }

    /// <summary>
    /// 把任务栏、输入法与音量的真实状态拉进控件�?    /// </summary>
    /// <remarks>
    /// 会短暂打开 <see cref="_loadingControls"/>：给滑块与开关赋值会再次触发它们�?    /// 变更事件，不挡住的话每次轮询都会把读到的值当成用户操作写回系统�?    /// </remarks>
    private void UpdateStatusControls()
    {
        _loadingControls = true;

        try
        {
            TaskbarToggle.IsChecked = ReadTaskbarHidden();

            var language = InputLanguageIndicator.CurrentLabel();
            InputLanguageText.Text = language;
            InputLanguageText.Visibility = string.IsNullOrEmpty(language) ? Visibility.Collapsed : Visibility.Visible;

            if (_volume.TryGetLevel(out var level, out var muted))
            {
                VolumeRow.Visibility = Visibility.Visible;
                VolumeSlider.Value = muted ? 0 : level;
                VolumeText.Text = muted ? "静音" : $"{level * 100:0}%";
            }
            else
            {
                // 系统没有可用播放设备：收起整行，而不是留一个拨不动的滑块�?
VolumeRow.Visibility = Visibility.Collapsed;
            }

            // 默认设备可能被别的程序改掉：只用轻量的默认查询同步选择，不重建列表�?
SyncDeviceSelection(OutputDeviceCombo, input: false);
            SyncDeviceSelection(InputDeviceCombo, input: true);
        }
        finally
        {
            _loadingControls = false;
        }
    }

    private void OnMixerOpenClick(object sender, RoutedEventArgs e)
    {
        BuildMixer();
        SystemControlsPanel.Visibility = Visibility.Collapsed;
        MixerPanel.Visibility = Visibility.Visible;
    }

    private void OnMixerBackClick(object sender, RoutedEventArgs e)
    {
        MixerPanel.Visibility = Visibility.Collapsed;
        SystemControlsPanel.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 搭出「输出设�?+ 音量合成器」两栏�?    /// </summary>
    /// <remarks>
    /// 两栏都按当前状态重建，因为设备与会话都是动态的：换了默认设备、某个程序停止发声，
    /// 列表就该跟着变。行里各自的滑块只在用户拖动时写回系统�?    /// </remarks>
    private void BuildMixer()
    {
        BuildMixerDevices();
        BuildMixerSessions();
    }

    private void BuildMixerDevices()
    {
        MixerDeviceList.Children.Clear();

        var devices = AudioDevices.List(input: false);

        foreach (var device in devices)
        {
            var button = new Button
            {
                Style = (Style)FindResource("MixerDeviceRow"),
                Tag = device.Id,
                Content = BuildMixerDeviceContent(device),
                ToolTip = device.Name,
            };

            button.Click += OnMixerDeviceClick;
            MixerDeviceList.Children.Add(button);
        }

        if (devices.Count == 0)
        {
            MixerDeviceList.Children.Add(Note("没有可用的输出设备"));
        }
    }

    private Grid BuildMixerDeviceContent(AudioDevice device)
    {
        var row = new Grid();

        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var glyph = new System.Windows.Shapes.Path
        {
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 0, 8, 0),
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = device.IsDefault ? AccentBrush : SecondaryForegroundBrush,
            Data = Geometry.Parse("M3,9 V15 H7 L12,20 V4 L7,9 Z M16.5,12 C16.5,10.23 15.48,8.71 14,7.97 V16.02 C15.48,15.29 16.5,13.77 16.5,12 Z"),
        };

        var label = new TextBlock
        {
            Text = device.Name,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = device.IsDefault ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = device.IsDefault ? ForegroundBrush : SecondaryForegroundBrush,
        };

        Grid.SetColumn(glyph, 0);
        Grid.SetColumn(label, 1);
        row.Children.Add(glyph);
        row.Children.Add(label);
        return row;
    }

    private void OnMixerDeviceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
        {
            return;
        }

        // 切默认设备要一点时间，界面等它生效后再重建，免得重建出来的还是旧状态�?
_ = AudioDevices.TrySetDefault(id, input: false);
        BuildMixer();
    }

    private void BuildMixerSessions()
    {
        MixerSessionList.Children.Clear();

        // 第一行是设备本身的音量——Windows 的合成器也是这么排的�?
MixerSessionList.Children.Add(BuildDeviceVolumeRow());

        var sessions = AudioSessions.List();

        foreach (var session in sessions)
        {
            MixerSessionList.Children.Add(BuildSessionRow(session));
        }

        MixerHint.Text = sessions.Count == 0
            ? "此刻没有应用在播放声音；应用开始发声后会出现在这里"
            : "只列出此刻有声音会话的应用；关掉那些程序后对应行会消失";
    }

    private Grid BuildDeviceVolumeRow()
    {
        var (row, slider, readout) = BuildMixerRowShell(LoadExecutableIcon(null), "系统");

        _loadingControls = true;

        try
        {
            if (_volume.TryGetLevel(out var level, out var muted))
            {
                slider.Value = muted ? 0 : level;
                readout.Text = muted ? "静音" : $"{level * 100:0}%";
            }
        }
        finally
        {
            _loadingControls = false;
        }

        slider.ValueChanged += (_, args) =>
        {
            if (_loadingControls)
            {
                return;
            }

            readout.Text = $"{args.NewValue * 100:0}%";

            if (_volume.TrySetLevel(args.NewValue) && args.NewValue > 0)
            {
                _ = _volume.TrySetMuted(false);
            }
        };

        return row;
    }

    private Grid BuildSessionRow(AudioSession session)
    {
        var (row, slider, readout) = BuildMixerRowShell(LoadExecutableIcon(session.ExecutablePath), session.Name);

        slider.ToolTip = string.IsNullOrEmpty(session.ExecutablePath) ? session.Name : session.ExecutablePath;

        _loadingControls = true;

        try
        {
            slider.Value = session.Muted ? 0 : session.Volume;
            readout.Text = session.Muted ? "静音" : $"{session.Volume * 100:0}%";
        }
        finally
        {
            _loadingControls = false;
        }

        slider.ValueChanged += (_, args) =>
        {
            if (_loadingControls)
            {
                return;
            }

            readout.Text = $"{args.NewValue * 100:0}%";

            if (AudioSessions.TrySetVolume(session.Id, args.NewValue) && args.NewValue > 0)
            {
                _ = AudioSessions.TrySetMute(session.Id, false);
            }
        };

        return row;
    }

    /// <summary>一行合成器的骨架：[图标] [名称] [滑块] [百分比]�?/summary>
    private (Grid Row, Slider Slider, TextBlock Readout) BuildMixerRowShell(ImageSource? icon, string name)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };

        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var glyph = new Image
        {
            Source = icon,
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.Uniform,
        };

        var label = new TextBlock
        {
            Text = name,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ForegroundBrush,
        };

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            SmallChange = 0.02,
            LargeChange = 0.1,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)FindResource("TrayVolumeSlider"),
        };

        var readout = new TextBlock
        {
            MinWidth = 34,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            Foreground = SecondaryForegroundBrush,
        };

        Grid.SetColumn(glyph, 0);
        Grid.SetColumn(label, 1);
        Grid.SetColumn(slider, 2);
        Grid.SetColumn(readout, 3);
        row.Children.Add(glyph);
        row.Children.Add(label);
        row.Children.Add(slider);
        row.Children.Add(readout);
        return (row, slider, readout);
    }

    /// <summary>取可执行文件的图标；取不到时返回 null，由单元格画占位字形�?/summary>
    private static BitmapSource? LoadExecutableIcon(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        var extracted = IconExtractor.Extract(executablePath);

        if (extracted is null || extracted.Width <= 0 || extracted.Height <= 0)
        {
            return null;
        }

        var bitmap = BitmapSource.Create(
            extracted.Width, extracted.Height, 96, 96,
            PixelFormats.Bgra32, null, extracted.Bgra, extracted.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private TextBlock Note(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Margin = new Thickness(0, 4, 0, 4),
        TextWrapping = TextWrapping.Wrap,
        Foreground = SecondaryForegroundBrush,
    };

    /// <summary>重新枚举输入/输出设备并填进下拉框；没有设备的方向整行收起�?/summary>
    private void RefreshAudioDevices()
    {
        var wasLoading = _loadingControls;

        // 填列表期间的选择变化不是用户操作，不能让它反过来去写系统�?
_loadingControls = true;

        try
        {
            FillDeviceCombo(OutputDeviceCombo, OutputDeviceRow, input: false);
            FillDeviceCombo(InputDeviceCombo, InputDeviceRow, input: true);
        }
        finally
        {
            _loadingControls = wasLoading;
        }
    }

    private static void FillDeviceCombo(ComboBox combo, Grid row, bool input)
    {
        var devices = AudioDevices.List(input);

        combo.Items.Clear();

        foreach (var device in devices)
        {
            combo.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Id });

            if (device.IsDefault)
            {
                combo.SelectedIndex = combo.Items.Count - 1;
            }
        }

        row.Visibility = devices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>把下拉框的选择对齐到系统当前的默认设备；已经一致时什么都不做�?/summary>
    private static void SyncDeviceSelection(ComboBox combo, bool input)
    {
        if (combo.Items.Count == 0)
        {
            return;
        }

        var defaultId = AudioDevices.GetDefaultId(input);

        if (defaultId is null)
        {
            return;
        }

        if (combo.SelectedItem is ComboBoxItem { Tag: string current }
            && string.Equals(current, defaultId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SelectDevice(combo, defaultId);
    }

    private static void SelectDevice(ComboBox combo, string? deviceId)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Tag is string id && string.Equals(id, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private void OnDeviceDropDownOpened(object sender, EventArgs e) => RefreshAudioDevices();

    private void OnOutputDeviceChanged(object sender, SelectionChangedEventArgs e)
        => ApplyDeviceSelection(OutputDeviceCombo, input: false);

    private void OnInputDeviceChanged(object sender, SelectionChangedEventArgs e)
        => ApplyDeviceSelection(InputDeviceCombo, input: true);

    private void ApplyDeviceSelection(ComboBox combo, bool input)
    {
        if (_loadingControls || combo.SelectedItem is not ComboBoxItem { Tag: string id } || string.IsNullOrEmpty(id))
        {
            return;
        }

        if (AudioDevices.TrySetDefault(id, input))
        {
            return;
        }

        // 系统拒绝了这次切换（策略、设备被占用）：把选择拨回真实默认，不让界面撒谎�?
_loadingControls = true;

        try
        {
            SelectDevice(combo, AudioDevices.GetDefaultId(input));
        }
        finally
        {
            _loadingControls = false;
        }
    }

    private void OnTaskbarToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingControls)
        {
            return;
        }

        var requested = TaskbarToggle.IsChecked == true;

        if (SetTaskbarHidden?.Invoke(requested) ?? SystemTaskbar.TrySetHidden(requested))
        {
            return;
        }

        // 没能生效（外壳没在跑、或宿主拒绝了这次改动）：把开关拨回真实状态，不让界面撒谎�?
_loadingControls = true;

        try
        {
            TaskbarToggle.IsChecked = ReadTaskbarHidden();
        }
        finally
        {
            _loadingControls = false;
        }
    }

    /// <summary>任务栏此刻是否隐藏着：优先问宿主，它才知道是不是自己藏的�?/summary>
    private bool ReadTaskbarHidden() => IsTaskbarHidden?.Invoke() ?? SystemTaskbar.IsHidden();

    private void OnVolumeValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingControls)
        {
            return;
        }

        VolumeText.Text = $"{e.NewValue * 100:0}%";

        if (!_volume.TrySetLevel(e.NewValue))
        {
            return;
        }

        // 用户主动把音量推起来就是在要求出声，顺手解除静音�?
if (e.NewValue > 0)
        {
            _ = _volume.TrySetMuted(false);
        }
    }
}
