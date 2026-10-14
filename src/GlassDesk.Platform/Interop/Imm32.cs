using System.Runtime.InteropServices;

namespace GlassDesk.Platform.Interop;

/// <summary>
/// imm32 中的输入法上下文入口点。
/// </summary>
/// <remarks>
/// 输入法指示只需要一件事：当前输入法是处于「本地字符」模式还是英文直通。
/// 这一位不在键盘布局句柄里，只能向输入法上下文问，因此必须声明这几个入口点。
/// </remarks>
internal static partial class Imm32
{
    /// <summary>
    /// <c>IME_CMODE_NATIVE</c>：输入法处于本地字符模式（中文输入），而不是英文直通。
    /// </summary>
    internal const int ImeCmodeNative = 0x0001;

    /// <summary>
    /// 取窗口输入法上下文；窗口没有输入法或系统带的是普通键盘布局时返回 0。
    /// </summary>
    /// <remarks>
    /// 可以对别的进程的窗口调用：imm32 会把请求转给该窗口所属线程的输入法。
    /// 取到之后必须用 <see cref="ImmReleaseContext"/> 成对释放。
    /// </remarks>
    [LibraryImport("imm32.dll")]
    internal static partial nint ImmGetContext(nint window);

    [LibraryImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ImmReleaseContext(nint window, nint inputContext);

    /// <summary>取输入法的转换模式与句子模式。</summary>
    [LibraryImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ImmGetConversionStatus(nint inputContext, out int conversion, out int sentence);

    /// <summary>键盘布局句柄是否真的是输入法，而不是普通键盘布局。</summary>
    [LibraryImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ImmIsIME(nint keyboardLayout);
}
