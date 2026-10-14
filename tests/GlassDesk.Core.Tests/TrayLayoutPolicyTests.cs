using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;

namespace GlassDesk.Core.Tests;

public sealed class TrayLayoutPolicyTests
{
    [Theory]
    [InlineData("微信", "微信")]
    [InlineData("  Steam  ", "Steam")]
    [InlineData("QQ: 2719630640\n声音: 开启\n消息提醒框: 开启", "QQ: 2719630640")]
    [InlineData("网易邮箱大师\n", "网易邮箱大师")]
    public void ComposeId_KeepsOnlyTheFirstLine(string title, string expected)
    {
        Assert.Equal(expected, TrayLayoutPolicy.ComposeId(title));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n状态行")]
    public void ComposeId_ReturnsEmptyWhenThereIsNoName(string? title)
    {
        Assert.Equal(string.Empty, TrayLayoutPolicy.ComposeId(title));
    }

    [Fact]
    public void Arrange_ReturnsTheInputUntouchedWhenNothingWasCustomized()
    {
        var entries = new[] { Entry("a"), Entry("b") };

        var arranged = TrayLayoutPolicy.Arrange(entries, []);

        // 新用户看到的必须是通知区域的原始顺序；同一次调用里也不该产生新的列表。
        Assert.Same(entries, arranged);
    }

    [Fact]
    public void Arrange_RemovesHiddenEntries()
    {
        var entries = new[] { Entry("a"), Entry("b"), Entry("c") };
        var preferences = new[] { new TrayItemPreference { Id = "b", Hidden = true } };

        var arranged = TrayLayoutPolicy.Arrange(entries, preferences);

        Assert.Equal(["a", "c"], arranged.Select(entry => entry.Id));
    }

    [Fact]
    public void Arrange_PutsPinnedFirstThenRecordedOrderThenTheRestInOriginalOrder()
    {
        var entries = new[] { Entry("a"), Entry("b"), Entry("c"), Entry("d"), Entry("e") };
        var preferences = new[]
        {
            new TrayItemPreference { Id = "e", Pinned = true, Order = 5 },
            new TrayItemPreference { Id = "b", Order = 1 },
            new TrayItemPreference { Id = "d", Order = 3 },
        };

        var arranged = TrayLayoutPolicy.Arrange(entries, preferences);

        // 固定项（e）最前；已记录次序的按 Order（b=1、d=3）；没记录过的保持原顺序（a、c）。
        Assert.Equal(["e", "b", "d", "a", "c"], arranged.Select(entry => entry.Id));
    }

    [Fact]
    public void Arrange_MatchesIdsCaseInsensitively()
    {
        var entries = new[] { Entry("Steam"), Entry("WeChat") };
        var preferences = new[] { new TrayItemPreference { Id = "steam", Pinned = true } };

        var arranged = TrayLayoutPolicy.Arrange(entries, preferences);

        Assert.Equal(["Steam", "WeChat"], arranged.Select(entry => entry.Id));
    }

    [Fact]
    public void Arrange_KeepsEveryEntryWhenAPreferenceMatchesNothing()
    {
        var entries = new[] { Entry("a"), Entry("b") };
        var preferences = new[] { new TrayItemPreference { Id = "已经退出的程序", Pinned = true, Order = 0 } };

        var arranged = TrayLayoutPolicy.Arrange(entries, preferences);

        Assert.Equal(["a", "b"], arranged.Select(entry => entry.Id));
    }

    private static TrayIconEntry Entry(string id) => new(id, id, 0, 0, 20, 20);
}
