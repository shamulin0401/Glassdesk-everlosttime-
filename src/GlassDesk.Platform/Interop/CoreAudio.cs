using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace GlassDesk.Platform.Interop;

/// <summary>
/// CoreAudio（WASAPI）里读写<strong>默认播放设备</strong>音量所需的标识与常量。
/// </summary>
/// <remarks>
/// 音量滑块必须读写系统音量本身，而不是发 <c>WM_APPCOMMAND</c> 去「按一下音量键」：
/// 后者只能一档一档地走，既无法表达滑块拖到某个百分比，也读不回当前值。
/// </remarks>
internal static class CoreAudioIds
{
    /// <summary><c>MMDeviceEnumerator</c> 组件类。</summary>
    internal static readonly Guid MMDeviceEnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    /// <summary><c>IMMDeviceEnumerator</c>。</summary>
    internal static readonly Guid MMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");

    /// <summary><c>IMMDevice</c>。</summary>
    internal static readonly Guid MMDevice = new("D666063F-1587-4E43-81F1-B948E807363F");

    /// <summary><c>IAudioEndpointVolume</c>。</summary>
    internal static readonly Guid AudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    /// <summary><c>EDataFlow.eRender</c>：播放设备。</summary>
    internal const int DataFlowRender = 0;

    /// <summary><c>EDataFlow.eCapture</c>：录音设备。</summary>
    internal const int DataFlowCapture = 1;

    /// <summary><c>ERole.eMultimedia</c>：任务栏音量图标跟随的正是这个角色。</summary>
    internal const int RoleMultimedia = 1;

    /// <summary><c>ERole.eConsole</c>。</summary>
    internal const int RoleConsole = 0;

    /// <summary><c>ERole.eCommunications</c>。</summary>
    internal const int RoleCommunications = 2;

    /// <summary><c>DEVICE_STATE_ACTIVE</c>：只列真正可用的设备，屏蔽掉「已拔出但没卸载」的那些。</summary>
    internal const int DeviceStateActive = 0x00000001;

    /// <summary>设备友好名属性的键，取的是端点自己的显示名。</summary>
    internal static readonly PropertyKey DeviceFriendlyName = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    /// <summary><c>IPolicyConfig</c> 的组件类。</summary>
    internal static readonly Guid PolicyConfigClass = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

    /// <summary><c>IPolicyConfig</c> 的接口标识。</summary>
    internal static readonly Guid PolicyConfig = new("F8679F50-850A-41CF-9C72-430F290290C8");

    /// <summary><c>IAudioSessionManager2</c>：在每个应用自己的音频会话上读写音量。</summary>
    internal static readonly Guid AudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    /// <summary><c>IAudioSessionControl2</c>：会话身份与所属进程。</summary>
    internal static readonly Guid AudioSessionControl2 = new("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D");

    /// <summary><c>ISimpleAudioVolume</c>：单个会话的音量。</summary>
    internal static readonly Guid SimpleAudioVolume = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");
}

/// <summary>
/// <c>IMMDeviceEnumerator</c>：取默认音频端点。
/// </summary>
/// <remarks>
/// 方法顺序即 vtable 槽位顺序，<strong>不能只声明用得上的那几个</strong>——
/// 少写一个，后面的方法就会落到错误的槽位上，而且不会报错。
/// 接口类型的出参一律用裸指针接，再由调用方经 <c>ComWrappers</c> 包装，
/// 与 <c>ShellCom</c> 的做法一致。
/// </remarks>
[GeneratedComInterface]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(int dataFlow, int role, out nint endpoint);

    [PreserveSig]
    int GetDevice(nint id, out nint device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(nint client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(nint client);
}

/// <summary><c>IMMDevice</c>：在一个端点设备上激活具体接口。</summary>
[GeneratedComInterface]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    [PreserveSig]
    int Activate(in Guid interfaceId, int classContext, nint activationParameters, out nint instance);

    [PreserveSig]
    int OpenPropertyStore(int access, out nint properties);

    /// <summary>取端点 id 字符串。归调用方所有，内容是 <c>CoTaskMemAlloc</c> 分配的 LPWSTR。</summary>
    [PreserveSig]
    int GetId(out nint id);

    [PreserveSig]
    int GetState(out int state);
}

/// <summary>
/// <c>IAudioEndpointVolume</c>：默认播放设备的音量与静音。
/// </summary>
/// <remarks>
/// 全部 18 个方法按原生顺序声明。本产品只用 <c>Get/SetMasterVolumeLevelScalar</c>
/// 与 <c>Get/SetMute</c>，但它们分别落在槽位 5、7、12、13，
/// 中间任何一个都不能省略。
/// </remarks>
[GeneratedComInterface]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
internal partial interface IAudioEndpointVolume
{
    [PreserveSig]
    int RegisterControlChangeNotify(nint notify);

    [PreserveSig]
    int UnregisterControlChangeNotify(nint notify);

    [PreserveSig]
    int GetChannelCount(out int count);

    [PreserveSig]
    int SetMasterVolumeLevel(float level, in Guid eventContext);

    [PreserveSig]
    int SetMasterVolumeLevelScalar(float level, in Guid eventContext);

    [PreserveSig]
    int GetMasterVolumeLevel(out float level);

    [PreserveSig]
    int GetMasterVolumeLevelScalar(out float level);

    [PreserveSig]
    int SetChannelVolumeLevel(int channel, float level, in Guid eventContext);

    [PreserveSig]
    int SetChannelVolumeLevelScalar(int channel, float level, in Guid eventContext);

    [PreserveSig]
    int GetChannelVolumeLevel(int channel, out float level);

    [PreserveSig]
    int GetChannelVolumeLevelScalar(int channel, out float level);

    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, in Guid eventContext);

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);

    [PreserveSig]
    int GetVolumeStepInfo(out int step, out int stepCount);

    [PreserveSig]
    int VolumeStepUp(in Guid eventContext);

    [PreserveSig]
    int VolumeStepDown(in Guid eventContext);

    [PreserveSig]
    int QueryHardwareSupport(out int mask);

    [PreserveSig]
    int GetVolumeRange(out float minimum, out float maximum, out float increment);
}

/// <summary><c>IMMDeviceCollection</c>：枚举出来的设备集合。</summary>
/// <remarks>槽位顺序即原生顺序，不能只声明用得上的那几个。</remarks>
[GeneratedComInterface]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
internal partial interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out int count);

    [PreserveSig]
    int Item(int index, out nint device);
}

/// <summary>
/// <c>IPolicyConfig</c>：把某个端点设为默认设备。
/// </summary>
/// <remarks>
/// <b>这是未公开接口</b>（没有 Windows SDK 头文件），但它是切换默认音频设备的唯一途径：
/// 公开的 CoreAudio 表面只能读取设备，不能改默认设备。所有设备切换类工具走的都是它，
/// 因此这里按广为验证的槽位顺序声明，并在调用失败时如实返回 <c>false</c>，
/// 而不是假定它一定可用。
///
/// <para>
/// 12 个方法全部按原生顺序保留：<c>SetDefaultEndpoint</c> 在第 11 槽，
/// 中间少写一个就会落到错误的槽位上。
/// </para>
/// </remarks>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
internal partial interface IPolicyConfig
{
    [PreserveSig]
    int GetMixFormat(string deviceName, out nint format);

    [PreserveSig]
    int GetDeviceFormat(string deviceName, int isDefault, out nint format);

    [PreserveSig]
    int ResetDeviceFormat(string deviceName);

    [PreserveSig]
    int SetDeviceFormat(string deviceName, nint endpointFormat, nint mixFormat);

    [PreserveSig]
    int GetProcessingPeriod(string deviceName, int isDefault, out long defaultPeriod, out long minimumPeriod);

    [PreserveSig]
    int SetProcessingPeriod(string deviceName, nint period);

    [PreserveSig]
    int GetShareMode(string deviceName, out nint mode);

    [PreserveSig]
    int SetShareMode(string deviceName, nint mode);

    [PreserveSig]
    int GetPropertyValue(string deviceName, in PropertyKey key, out PropVariant value);

    [PreserveSig]
    int SetPropertyValue(string deviceName, in PropertyKey key, in PropVariant value);

    /// <summary>把 <paramref name="deviceName"/>（端点 id）设为该角色的默认设备。</summary>
    [PreserveSig]
    int SetDefaultEndpoint(string deviceName, int role);

    [PreserveSig]
    int SetEndpointVisibility(string deviceName, int visible);
}

/// <summary>
/// <c>IAudioSessionManager2</c>：拿到某个端点上所有应用音频会话的枚举器。
/// </summary>
/// <remarks>
/// 前两个方法来自基接口 <c>IAudioSessionManager</c>，必须一起声明：
/// 继承来的方法占着 vtable 最前面的槽位，跳过它们会让
/// <c>GetSessionEnumerator</c> 落到错误的槽位上。
/// </remarks>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
internal partial interface IAudioSessionManager2
{
    [PreserveSig]
    int GetAudioSessionControl(nint audioSessionGuid, int streamFlags, out nint sessionControl);

    [PreserveSig]
    int GetSimpleAudioVolume(nint audioSessionGuid, int streamFlags, out nint audioVolume);

    [PreserveSig]
    int GetSessionEnumerator(out nint sessionEnumerator);

    [PreserveSig]
    int RegisterSessionNotification(nint sessionNotification);

    [PreserveSig]
    int UnregisterSessionNotification(nint sessionNotification);

    [PreserveSig]
    int RegisterDuckNotification(string sessionId, nint duckNotification);

    [PreserveSig]
    int UnregisterDuckNotification(nint duckNotification);
}

/// <summary><c>IAudioSessionEnumerator</c>：遍历端点上的会话。</summary>
[GeneratedComInterface]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
internal partial interface IAudioSessionEnumerator
{
    [PreserveSig]
    int GetCount(out int sessionCount);

    [PreserveSig]
    int GetSession(int sessionIndex, out nint session);
}

/// <summary>
/// <c>IAudioSessionControl2</c>：会话的身份与控制。
/// </summary>
/// <remarks>前九个方法来自基接口 <c>IAudioSessionControl</c>，占着最前面的槽位。</remarks>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
internal partial interface IAudioSessionControl2
{
    [PreserveSig]
    int GetState(out int state);

    [PreserveSig]
    int GetDisplayName(out nint displayName);

    [PreserveSig]
    int SetDisplayName(string displayName, nint eventContext);

    [PreserveSig]
    int GetIconPath(out nint iconPath);

    [PreserveSig]
    int SetIconPath(string iconPath, nint eventContext);

    [PreserveSig]
    int GetGroupingParam(out Guid groupingParam);

    [PreserveSig]
    int SetGroupingParam(in Guid groupingParam, nint eventContext);

    [PreserveSig]
    int RegisterAudioSessionNotification(nint client);

    [PreserveSig]
    int UnregisterAudioSessionNotification(nint client);

    /// <summary>会话标识；同一次会话在进程存活期间保持不变。</summary>
    [PreserveSig]
    int GetSessionIdentifier(out nint sessionIdentifier);

    /// <summary>会话实例标识：同一个应用多次启动会得到不同的值，用它区分具体是哪一次。</summary>
    [PreserveSig]
    int GetSessionInstanceIdentifier(out nint sessionInstanceIdentifier);

    /// <summary>这个会话属于哪个进程；系统声音返回 0。</summary>
    [PreserveSig]
    int GetProcessId(out int processId);

    [PreserveSig]
    int IsSystemSoundsSession();

    [PreserveSig]
    int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool preference);
}

/// <summary><c>ISimpleAudioVolume</c>：单个会话的音量与静音。</summary>
[GeneratedComInterface]
[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
internal partial interface ISimpleAudioVolume
{
    [PreserveSig]
    int SetMasterVolume(float level, in Guid eventContext);

    [PreserveSig]
    int GetMasterVolume(out float level);

    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, in Guid eventContext);

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}
