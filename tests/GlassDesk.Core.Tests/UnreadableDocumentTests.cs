using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

/// <summary>
/// 「文件存在但读不出来」不能被当成「还没有文件」。
/// </summary>
/// <remarks>
/// 这是数据丢失的入口：被别的进程占用、权限不足、被安全软件扣着的文档，
/// 原先会被报成首次运行并返回一份默认设置，用户的下一次保存随即把真实内容整个覆盖，
/// 而首次保存不写备份——原内容无处可寻。这里把那条路钉住。
/// </remarks>
public sealed class UnreadableDocumentTests
{
    [Fact]
    public void APresentButUnreadableSettingsFileIsNotReportedAsFirstRun()
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-locked-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);

        try
        {
            paths.EnsureCreated();

            var store = new SettingsStore(paths.SettingsFile);
            store.Load();
            store.Current.Appearance.AccentColor = "#FF0000";
            store.Save();

            // 锁住文件：任何共享模式之外的打开都会失败，正是「读不出来」的现场。
            using (new FileStream(paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var outcome = new SettingsStore(paths.SettingsFile).Load();

                Assert.NotEqual(LoadStatus.FirstRun, outcome.Status);
                Assert.True(outcome.NeedsUserAttention);
            }

            // 真实内容必须原封不动：这条路径不许写回任何东西。
            Assert.Contains("FF0000", File.ReadAllText(paths.SettingsFile), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void AMissingSettingsFileIsStillAFirstRun()
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-first-" + Guid.NewGuid().ToString("N"));

        try
        {
            var outcome = new SettingsStore(new AppPaths(root).SettingsFile).Load();

            // 与上一条配对：真的没有文件时仍然是首次运行，不受这次改动影响。
            Assert.Equal(LoadStatus.FirstRun, outcome.Status);
            Assert.False(outcome.NeedsUserAttention);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
