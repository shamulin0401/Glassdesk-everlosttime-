using System.Runtime.CompilerServices;

namespace GlassDesk.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Capture helpers own native resources only. Loading the desktop Application
        // and its theme dictionaries before the pipe handshake delays every cold reveal.
        if (UI.BackdropCaptureWorker.RunIfRequested(args, out var exitCode)) return exitCode;

        // 任务栏守护进程同理，必须在不创建 WPF 应用、不取单实例互斥体、不碰数据目录的前提下跑完：
        // 它只是一个等父进程结束的旁观者。
        if (Startup.TaskbarGuardProbe.RunIfRequested(args, out var guardExitCode)) return guardExitCode;

        return RunDesktop();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunDesktop()
    {
        var application = new App();
        application.InitializeComponent();
        return application.Run();
    }
}
