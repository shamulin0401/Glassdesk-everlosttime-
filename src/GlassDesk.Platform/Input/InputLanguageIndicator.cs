using System.Globalization;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform.Input;

/// <summary>
/// 当前输入语言与输入法状态，对应系统控制区里的「中」。
/// </summary>
/// <remarks>
/// 读的是<strong>前台窗口</strong>所在线程的键盘布局，而不是调用线程的：
/// 用户看的是正在打字的那个窗口用的是哪种输入法，本进程自己的布局无关紧要。
///
/// <para>
/// 语言 id 的低 16 位来自 <c>HKL</c>，其中真正区分语言的是低 10 位。
/// 中文与其他语言的区别在于：中文要在「中」与「英」之间再分一次，
/// 而这一位不在布局句柄里，只能问输入法上下文（<c>IME_CMODE_NATIVE</c>）。
/// 输入法上下文取不到时按中文模式显示——用户装了中文输入法却看到「英」，
/// 比反过来更让人困惑。
/// </para>
/// </remarks>
public static class InputLanguageIndicator
{
    /// <summary><c>LANG_CHINESE</c> 的主语言 id。</summary>
    private const int Chinese = 0x04;

    /// <summary><c>LANG_JAPANESE</c> 的主语言 id。</summary>
    private const int Japanese = 0x11;

    /// <summary><c>LANG_KOREAN</c> 的主语言 id。</summary>
    private const int Korean = 0x12;

    /// <summary><c>LANGID</c> 里主语言的位掩码。</summary>
    private const int PrimaryLanguageMask = 0x03FF;

    /// <summary>
    /// 取当前输入语言的短标签：中文输入法返回「中」或「英」，
    /// 日文与韩文返回各自的字符，其余返回两位语言代码；读不到时返回空串。
    /// </summary>
    /// <remarks>
    /// 调用方（系统控制区）把空串当作「没有可显示的内容」并收起这一行，
    /// 因此这里不抛异常，也不返回占位文本。
    /// </remarks>
    public static string CurrentLabel()
    {
        try
        {
            var window = User32.GetForegroundWindow();
            var threadId = window == 0 ? 0u : User32.GetWindowThreadProcessId(window, out _);
            var languageId = (int)(User32.GetKeyboardLayout(threadId).ToInt64() & 0xFFFF);

            return (languageId & PrimaryLanguageMask) switch
            {
                Chinese => IsNativeInputMode(window) ? "中" : "英",
                Japanese => "あ",
                Korean => "한",
                _ => TwoLetterName(languageId),
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return string.Empty;
        }
    }

    /// <summary>输入法是否处于本地字符模式（中文输入）而不是英文直通。</summary>
    private static bool IsNativeInputMode(nint window)
    {
        var context = Imm32.ImmGetContext(window);

        if (context == 0)
        {
            return true;
        }

        try
        {
            return Imm32.ImmGetConversionStatus(context, out var conversion, out _)
                && (conversion & Imm32.ImeCmodeNative) != 0;
        }
        finally
        {
            _ = Imm32.ImmReleaseContext(window, context);
        }
    }

    /// <summary>把语言 id 折成两位大写语言代码；系统不认识这个 id 时退回十六进制写法。</summary>
    private static string TwoLetterName(int languageId)
    {
        try
        {
            return CultureInfo.GetCultureInfo(languageId).TwoLetterISOLanguageName.ToUpperInvariant();
        }
        catch (CultureNotFoundException)
        {
            return languageId.ToString("X4", CultureInfo.InvariantCulture);
        }
    }
}
