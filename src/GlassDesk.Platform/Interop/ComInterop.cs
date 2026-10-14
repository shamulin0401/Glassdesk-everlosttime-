using System.Runtime.InteropServices.Marshalling;

namespace GlassDesk.Platform.Interop;

/// <summary>
/// 全进程共用的 COM 包装器。
/// </summary>
/// <remarks>
/// 原先有八处各自持有 <c>new StrategyBasedComWrappers()</c>（图标提取、快捷方式读写、窗口身份、
/// 音频四处）。它们之间**没有状态可言**：所有调用都带
/// <c>CreateObjectFlags.UniqueInstance</c>，也就是绕过包装器内部的身份缓存、每次都新建一个
/// 唯一的包装对象，因此多份实例并不提供任何隔离，只是八份同样的东西。
///
/// <para>
/// 共用一个即可。各类仍保留自己的字段名（指向这一个），所以调用点一处都不用改。
/// </para>
/// </remarks>
internal static class ComInterop
{
    /// <summary>共享的包装器实例。</summary>
    internal static StrategyBasedComWrappers Shared { get; } = new();
}
