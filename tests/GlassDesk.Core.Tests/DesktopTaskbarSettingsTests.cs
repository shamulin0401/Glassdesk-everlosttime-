using GlassDesk.Core.Models.Settings;

namespace GlassDesk.Core.Tests;

public sealed class DesktopTaskbarSettingsTests
{
    [Fact]
    public void ResetPage_KeepsTheTaskbarVisibilityIntentThatIsCurrentlyInForce()
    {
        // 「隐藏系统任务栏」由托盘面板的开关控制，重置这一页不能悄悄改掉它：
        // 设置说「显示」而任务栏实际藏着，两边就会不一致，直到下次启动。
        var settings = new GlassDeskSettings();
        settings.DesktopTaskbar.HideTaskbar = true;
        settings.DesktopTaskbar.TaskbarAutoHideBeforeHide = true;

        var reset = SettingsPageEditor.ResetPage(settings, "桌面与任务栏");

        Assert.True(reset.DesktopTaskbar.HideTaskbar);
        Assert.True(reset.DesktopTaskbar.TaskbarAutoHideBeforeHide);

        // 这一页里真正属于「默认值」的那些仍然要回到默认。
        Assert.NotNull(reset.DesktopTaskbar.ToggleDesktopIcons);
    }

    [Fact]
    public void ResetPage_LeavesTheOriginalSettingsUntouched()
    {
        var settings = new GlassDeskSettings();
        settings.DesktopTaskbar.HideTaskbar = true;

        _ = SettingsPageEditor.ResetPage(settings, "桌面与任务栏");

        Assert.True(settings.DesktopTaskbar.HideTaskbar);
    }

    [Fact]
    public void NewSettings_DoNotHideTheTaskbarByDefault()
    {
        // 默认必须是「不打扰 Windows」：新用户不该在第一次启动时发现任务栏不见了。
        Assert.False(new DesktopTaskbarSettings().HideTaskbar);
        Assert.False(new DesktopTaskbarSettings().TaskbarAutoHideBeforeHide);
    }
}
