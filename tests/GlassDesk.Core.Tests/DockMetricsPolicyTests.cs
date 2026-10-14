using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;

namespace GlassDesk.Core.Tests;

public sealed class DockMetricsPolicyTests
{
    [Fact]
    public void DefaultSettings_KeepTheHistoricMinimumFrameLength()
    {
        // 两个新开关默认关闭，因此外框长度下限必须与加入系统组之前完全一致（22 + 56×3 = 190）。
        var settings = new DockSettings();

        Assert.Equal(168, DockMetricsPolicy.BuiltinEntryWidthDip(settings));
        Assert.Equal(0, DockMetricsPolicy.SystemGroupWidthDip(settings));
        Assert.Equal(190, DockMetricsPolicy.MinimumFrameLengthDip(settings));
    }

    [Theory]
    [InlineData(false, false, false, 0)]
    [InlineData(true, false, false, 46)]
    [InlineData(false, true, false, 82)]
    [InlineData(true, true, false, 116)]

    // 精确到秒以后 HH:mm:ss 比日期那行还宽，时间块要跟着加宽 28。
    [InlineData(false, true, true, 110)]
    [InlineData(true, true, true, 144)]

    // 时钟关着时，「精确到秒」不占宽度——它只在时钟开着时才有意义。
    [InlineData(true, false, true, 46)]
    public void SystemGroupWidth_CountsOnlyTheEnabledParts(bool tray, bool clock, bool seconds, double expected)
    {
        var settings = new DockSettings { ShowSystemTray = tray, ShowClock = clock, ShowClockSeconds = seconds };

        Assert.Equal(expected, DockMetricsPolicy.SystemGroupWidthDip(settings));
    }

    [Theory]
    [InlineData(false, false, false, 190)]
    [InlineData(true, false, false, 236)]
    [InlineData(false, true, false, 272)]
    [InlineData(true, true, false, 306)]
    [InlineData(false, true, true, 300)]
    [InlineData(true, true, true, 334)]
    public void MinimumFrameLength_GrowsWithTheSystemGroup(bool tray, bool clock, bool seconds, double expected)
    {
        var settings = new DockSettings { ShowSystemTray = tray, ShowClock = clock, ShowClockSeconds = seconds };

        Assert.Equal(expected, DockMetricsPolicy.MinimumFrameLengthDip(settings));
    }

    [Fact]
    public void ClockSeconds_AreOnByDefault_SoTheWidthMustReserveRoomForThem()
    {
        // 默认开启就必须默认留出宽度，否则时间会在固定外框里被裁掉。
        var defaults = new DockSettings();

        Assert.True(defaults.ShowClockSeconds);
        Assert.Equal(DockMetricsPolicy.ClockWidthDip + DockMetricsPolicy.ClockSecondsExtraWidthDip,
            DockMetricsPolicy.ClockWidthFor(defaults));

        Assert.Equal(DockMetricsPolicy.ClockWidthDip,
            DockMetricsPolicy.ClockWidthFor(new DockSettings { ShowClockSeconds = false }));
    }

    [Fact]
    public void MinimumFrameLength_NeverDropsBelowTheProductFloor()
    {
        var settings = new DockSettings
        {
            ShowLauncherEntry = false,
            ShowThisPcEntry = false,
            ShowDesktopIconsEntry = false,
        };

        Assert.Equal(0, DockMetricsPolicy.BuiltinEntryWidthDip(settings));
        Assert.Equal(DockSettings.MinimumFrameLengthDip, DockMetricsPolicy.MinimumFrameLengthDip(settings));
    }

    [Fact]
    public void MinimumFrameLength_ScalesWithTheOverallSize()
    {
        var settings = new DockSettings { SizeScale = 1.5 };

        Assert.Equal(285, DockMetricsPolicy.MinimumFrameLengthDip(settings));
    }

    [Fact]
    public void MinimumFrameLength_ClampsAnOutOfRangeSizeScale()
    {
        var settings = new DockSettings { SizeScale = 10 };

        // 越界的倍率按最大倍率算，而不是让下限跟着一起失控。
        Assert.Equal(190 * DockSettings.MaximumSizeScale, DockMetricsPolicy.MinimumFrameLengthDip(settings));
    }

    [Fact]
    public void Widths_RejectNullSettings()
    {
        Assert.Throws<ArgumentNullException>(() => DockMetricsPolicy.BuiltinEntryWidthDip(null!));
        Assert.Throws<ArgumentNullException>(() => DockMetricsPolicy.SystemGroupWidthDip(null!));
        Assert.Throws<ArgumentNullException>(() => DockMetricsPolicy.MinimumFrameLengthDip(null!));
    }
}
