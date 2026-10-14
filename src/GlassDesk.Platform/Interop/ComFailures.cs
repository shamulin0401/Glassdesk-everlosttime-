using System.Runtime.InteropServices;

namespace GlassDesk.Platform.Interop;

/// <summary>
/// COM 调用里「可预期」的失败。
/// </summary>
/// <remarks>
/// 音频那几个类原先各自带着一份**一模一样**的过滤器（两个类各一份，另有一处内联）。
/// 它们要挡住的都是同一类事：设备被拔掉、接口不支持、系统组件缺失、对象已成空——
/// 都属于「这一轮读不到」，应当降级为返回空表/false，而不是把异常抛给界面线程。
///
/// <para>
/// 收在一处是因为这段类型列表是**会需要维护**的东西（过去就补过
/// <see cref="NotSupportedException"/>）；三份各自演化迟早会不一致，而它们判定的
/// 恰恰是同一件事。各类保留一个同名私有转发，读起来仍然贴着调用点。
/// </para>
/// </remarks>
internal static class ComFailures
{
    /// <summary>这次失败是不是「可预期」的（即：应当降级，而不是向上抛）。</summary>
    internal static bool IsExpected(Exception ex) =>
        ex is COMException or InvalidCastException or DllNotFoundException or EntryPointNotFoundException
            or NotSupportedException or InvalidOperationException;
}
