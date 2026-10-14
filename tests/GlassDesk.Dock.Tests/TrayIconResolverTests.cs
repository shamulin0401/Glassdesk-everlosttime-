using GlassDesk.Core.Models;
using GlassDesk.Dock.Tray;
using GlassDesk.Platform.Shell;

namespace GlassDesk.Dock.Tests;

/// <summary>
/// 托盘图标解析的降级行为。
/// </summary>
/// <remarks>
/// 这里刻意不断言「某个图标一定解析得出来」——那取决于跑测试时机器上开着哪些程序、
/// 注册表里登记了什么，是环境而不是代码的性质。断言的是无论环境如何都必须成立的两件事：
/// 结果与输入一一对应，以及任何一步失败都只是让这个图标退回占位图，而不是抛出去。
///
/// <para>
/// 第二条尤其重要：解析链里包含注册表读取、磁盘上的 exe、以及
/// <c>ShortcutResolver</c> 的 COM 调用，任何一步都可能在这台机器上失败、在另一台上成功。
/// </para>
/// </remarks>
public sealed class TrayIconResolverTests
{
    [Fact]
    public void Resolve_AlignsWithInputAndDegradesInsteadOfThrowing()
    {
        var entries = new[]
        {
            new TrayIconEntry("完全不存在的应用 42", "完全不存在的应用 42", 0, 0, 20, 20),
            new TrayIconEntry("另一个不存在的应用 99", "另一个不存在的应用 99", 0, 0, 0, 0),
            new TrayIconEntry("x", "x", 0, 0, 20, 20),
        };

        var icons = new TrayIconResolver().Resolve(entries);

        // 与输入一一对应，面板才能按下标取图标。
        Assert.Equal(entries.Length, icons.Count);

        // 认不出来就是 null（占位图），不能抛，也不能凭空给一个别的程序的图标。
        Assert.All(icons, Assert.Null);
    }

    [Fact]
    public void Resolve_ReturnsEmptyForEmptyInput()
    {
        Assert.Empty(new TrayIconResolver().Resolve([]));
    }

    [Fact]
    public void Resolve_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new TrayIconResolver().Resolve(null!));
    }

    [Fact]
    public void Catalog_LoadIsSafeAndEveryRecordCarriesSomethingUsable()
    {
        // 读注册表可能被策略挡掉；那时要返回空表而不是抛异常。
        var catalog = TrayIconCatalog.Load();
        Assert.NotNull(catalog.Records);

        // 登记表里至少要有图标快照或所属程序之一，否则这条记录对调用方毫无用处。
        Assert.All(catalog.Records, record =>
            Assert.True(
                record.SnapshotPng is { Length: > 0 } || !string.IsNullOrWhiteSpace(record.ExecutablePath),
                "登记记录既没有图标快照也没有可执行文件路径。"));
    }
}
