using System.Runtime.InteropServices;
using CastDesk.Diagnostics;

namespace CastDesk.Interop;

/// <summary>
/// Makes the Windows volume mixer show "CastDesk" (with CastDesk's icon) for the TV app's sound.
/// </summary>
/// <remarks>
/// The sound is played by WebView2's audio process (msedgewebview2.exe), so the mixer would label it
/// "Microsoft Edge WebView2". Every few seconds this finds the audio sessions owned by CastDesk's
/// WebView2 processes, on every playback device, and sets their display name and icon through the Core
/// Audio API (IAudioSessionControl). Sessions of other apps, including Edge itself, are never touched.
/// </remarks>
internal sealed class AudioSessionRenamer : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(3);

    private readonly string _displayName;
    private readonly string _iconPath;
    private readonly Func<int?> _browserProcessId;
    private readonly System.Threading.Timer _timer;
    private readonly HashSet<string> _renamedSessions = new(StringComparer.Ordinal);
    private int _checking;

    /// <param name="displayName">The name to show, such as "CastDesk".</param>
    /// <param name="iconPath">An icon resource path, such as "C:\...\CastDesk.exe,0".</param>
    /// <param name="browserProcessId">The WebView2 browser process ID, or null before it exists.</param>
    public AudioSessionRenamer(string displayName, string iconPath, Func<int?> browserProcessId)
    {
        _displayName = displayName;
        _iconPath = iconPath;
        _browserProcessId = browserProcessId;
        _timer = new System.Threading.Timer(_ => Check(), null, CheckInterval, CheckInterval);
    }

    private void Check()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            if (_browserProcessId() is not int browserProcessId) return;
            var ownProcesses = ProcessTree.GetDescendantsAndSelf(browserProcessId);
            RenameSessions(ownProcesses);
        }
        catch (Exception exception)
        {
            // On a timer thread, where any exception would end CastDesk. Marshal.ThrowExceptionForHR turns some
            // HRESULTs into other types (E_ACCESSDENIED into UnauthorizedAccessException, for one).
            AppLog.Write("host", $"Could not rename the volume mixer entry: {exception.Message}");
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    private void RenameSessions(HashSet<int> ownProcesses)
    {
        var enumerator = (CoreAudio.IMMDeviceEnumerator)new CoreAudio.MMDeviceEnumerator();
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(CoreAudio.DataFlowRender, CoreAudio.DeviceStateActive, out var devices));
            Marshal.ThrowExceptionForHR(devices.GetCount(out var deviceCount));
            try
            {
                for (uint deviceIndex = 0; deviceIndex < deviceCount; deviceIndex++)
                {
                    if (devices.Item(deviceIndex, out var device) != 0) continue;
                    try
                    {
                        RenameDeviceSessions(device, ownProcesses);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(devices);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>Renames CastDesk's sessions on one device. Every COM object is released, even when a call fails.</summary>
    private void RenameDeviceSessions(CoreAudio.IMMDevice device, HashSet<int> ownProcesses)
    {
        var managerId = typeof(CoreAudio.IAudioSessionManager2).GUID;
        if (device.Activate(ref managerId, CoreAudio.ClsCtxAll, IntPtr.Zero, out var managerObject) != 0) return;
        var manager = (CoreAudio.IAudioSessionManager2)managerObject;
        try
        {
            if (manager.GetSessionEnumerator(out var sessions) != 0) return;
            try
            {
                sessions.GetCount(out var sessionCount);
                for (var sessionIndex = 0; sessionIndex < sessionCount; sessionIndex++)
                {
                    if (sessions.GetSession(sessionIndex, out var session) != 0) continue;
                    try
                    {
                        RenameIfOwn(session, ownProcesses);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(session);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(sessions);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }
    }

    private void RenameIfOwn(CoreAudio.IAudioSessionControl2 session, HashSet<int> ownProcesses)
    {
        if (session.GetProcessId(out var processId) != 0 || !ownProcesses.Contains((int)processId)) return;
        if (session.GetSessionInstanceIdentifier(out var instanceId) != 0) return;

        session.GetDisplayName(out var currentName);
        if (currentName == _displayName) return;

        var eventContext = Guid.Empty;
        var nameResult = session.SetDisplayName(_displayName, ref eventContext);
        session.SetIconPath(_iconPath, ref eventContext);
        lock (_renamedSessions)
        {
            if (_renamedSessions.Add(instanceId))
                AppLog.Write("host", nameResult == 0
                    ? $"Volume mixer: named WebView2's audio (process {processId}) \"{_displayName}\"."
                    : $"Volume mixer: Windows refused to rename WebView2's audio (0x{nameResult:X8}).");
        }
    }

    public void Dispose() => _timer.Dispose();

    /// <summary>The parts of the Core Audio COM API this class uses.</summary>
    private static class CoreAudio
    {
        public const int DataFlowRender = 0;
        public const int DeviceStateActive = 0x1;
        public const int ClsCtxAll = 0x17;

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        public class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid interfaceId, int classContext, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int streamFlags, out IntPtr sessionControl);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int streamFlags, out IntPtr audioVolume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
        }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionControl2
        {
            // IAudioSessionControl
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
            [PreserveSig] int GetGroupingParam(out Guid groupingParam);
            [PreserveSig] int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
            // IAudioSessionControl2
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetProcessId(out uint processId);
        }
    }
}
