using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Media.Control;

namespace EchoType.Audio;

/// <summary>
/// Silences other apps while dictation is in progress: pauses media that
/// exposes play/pause (YouTube, Spotify, …), then mutes remaining playback
/// sessions that cannot be paused. Restores both when listening ends.
/// EchoType's own process and the Windows system-sounds session are left
/// alone so the start/paste/error beeps still play.
/// </summary>
internal sealed class BackgroundAudioMuter {

    private const int DeviceStateActive = 0x1;
    private const int DataFlowRender = 0;
    private const int RoleMultimedia = 1;
    private const int RoleCommunications = 2;
    private const int ClsCtxAll = 0x17;
    private const int AudioSessionStateActive = 1;
    private static readonly Guid IidAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    private readonly object _gate = new();
    private bool _held;
    private List<PausedMedia>? _paused;
    private List<ISimpleAudioVolume>? _muted;

    public void MuteOthers() {
        lock (_gate) {
            if (_held) {
                return;
            }
            _held = true;
        }

        List<PausedMedia> paused = [];
        List<ISimpleAudioVolume> muted = [];
        try {
            paused = PausePlaying();
            MuteAllRenderDevices(muted, paused);
        } catch (Exception ex) {
            Log.Write("audio: silence others failed: " + ex.Message);
            RestoreAll(paused, muted);
            lock (_gate) {
                _held = false;
            }
            return;
        }

        lock (_gate) {
            if (!_held) {
                RestoreAll(paused, muted);
                return;
            }
            _paused = paused;
            _muted = muted;
        }

        Log.Write("audio: paused " + paused.Count + " media session(s), muted "
            + muted.Count + " other playback session(s)");
    }

    public void Restore() {
        List<PausedMedia>? paused;
        List<ISimpleAudioVolume>? muted;
        lock (_gate) {
            if (!_held) {
                return;
            }
            _held = false;
            paused = _paused;
            muted = _muted;
            _paused = null;
            _muted = null;
        }
        RestoreAll(paused ?? [], muted ?? []);
        Log.Write("audio: restored other playback sessions");
    }

    private static List<PausedMedia> PausePlaying() {
        try {
            return Task.Run(PausePlayingAsync).GetAwaiter().GetResult();
        } catch (Exception ex) {
            Log.Write("audio: pause others failed: " + ex.Message);
            return [];
        }
    }

    private static async Task<List<PausedMedia>> PausePlayingAsync() {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        List<PausedMedia> paused = [];
        foreach (var session in manager.GetSessions()) {
            try {
                var info = session.GetPlaybackInfo();
                if (info.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) {
                    continue;
                }
                var controls = info.Controls;
                bool ok = false;
                if (controls.IsPauseEnabled) {
                    ok = await session.TryPauseAsync();
                } else if (controls.IsPlayPauseToggleEnabled) {
                    ok = await session.TryTogglePlayPauseAsync();
                }
                if (!ok) {
                    continue;
                }
                string name = session.SourceAppUserModelId;
                if (string.IsNullOrWhiteSpace(name)) {
                    name = "media";
                }
                paused.Add(new PausedMedia(session, name));
                Log.Write("audio: paused " + name);
            } catch (Exception ex) {
                Log.Write("audio: pause session skipped: " + ex.Message);
            }
        }
        return paused;
    }

    private static void ResumePaused(List<PausedMedia> paused) {
        if (paused.Count == 0) {
            return;
        }
        try {
            Task.Run(() => ResumePausedAsync(paused)).GetAwaiter().GetResult();
        } catch (Exception ex) {
            Log.Write("audio: resume others failed: " + ex.Message);
        }
    }

    private static async Task ResumePausedAsync(List<PausedMedia> paused) {
        foreach (var item in paused) {
            try {
                var info = item.Session.GetPlaybackInfo();
                if (info.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused) {
                    continue;
                }
                var controls = info.Controls;
                bool ok = false;
                if (controls.IsPlayEnabled) {
                    ok = await item.Session.TryPlayAsync();
                } else if (controls.IsPlayPauseToggleEnabled) {
                    ok = await item.Session.TryTogglePlayPauseAsync();
                }
                Log.Write(ok
                    ? "audio: resumed " + item.Name
                    : "audio: resume skipped " + item.Name);
            } catch (Exception ex) {
                Log.Write("audio: resume failed (" + item.Name + "): " + ex.Message);
            }
        }
    }

    private static void MuteAllRenderDevices(List<ISimpleAudioVolume> muted, List<PausedMedia> paused) {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try {
            MuteDefaultEndpoint(enumerator, RoleMultimedia, muted, paused);
            MuteDefaultEndpoint(enumerator, RoleCommunications, muted, paused);
            MuteEnumeratedEndpoints(enumerator, muted, paused);
        } finally {
            Release(enumerator);
        }
    }

    private static void MuteDefaultEndpoint(
        IMMDeviceEnumerator enumerator, int role, List<ISimpleAudioVolume> muted, List<PausedMedia> paused) {
        if (enumerator.GetDefaultAudioEndpoint(DataFlowRender, role, out IMMDevice? device) != 0 || device == null) {
            return;
        }
        try {
            MuteDeviceSessions(device, muted, paused);
        } catch (Exception ex) {
            Log.Write("audio: device mute failed: " + ex.Message);
        } finally {
            Release(device);
        }
    }

    private static void MuteEnumeratedEndpoints(
        IMMDeviceEnumerator enumerator, List<ISimpleAudioVolume> muted, List<PausedMedia> paused) {
        IMMDeviceCollection? devices = null;
        try {
            if (enumerator.EnumAudioEndpoints(DataFlowRender, DeviceStateActive, out devices) != 0 || devices == null) {
                return;
            }
            if (devices.GetCount(out int count) != 0) {
                return;
            }
            for (int i = 0; i < count; i++) {
                if (devices.Item(i, out IMMDevice? device) != 0 || device == null) {
                    continue;
                }
                try {
                    MuteDeviceSessions(device, muted, paused);
                } finally {
                    Release(device);
                }
            }
        } catch (Exception ex) {
            Log.Write("audio: endpoint enumeration skipped: " + ex.Message);
        } finally {
            Release(devices);
        }
    }

    private static void MuteDeviceSessions(IMMDevice device, List<ISimpleAudioVolume> muted, List<PausedMedia> paused) {
        Guid iid = IidAudioSessionManager2;
        if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object? raw) != 0 || raw is not IAudioSessionManager2 manager) {
            return;
        }
        IAudioSessionEnumerator? sessions = null;
        try {
            if (manager.GetSessionEnumerator(out sessions) != 0 || sessions == null) {
                return;
            }
            if (sessions.GetCount(out int count) != 0) {
                return;
            }
            int self = Environment.ProcessId;
            for (int i = 0; i < count; i++) {
                if (sessions.GetSession(i, out IAudioSessionControl? control) != 0 || control == null) {
                    continue;
                }
                if (!TryMuteSession(control, self, muted, paused)) {
                    Release(control);
                }
            }
        } finally {
            Release(sessions);
            Release(manager);
        }
    }

    private static bool TryMuteSession(
        IAudioSessionControl control, int selfPid, List<ISimpleAudioVolume> muted, List<PausedMedia> paused) {
        if (control is not IAudioSessionControl2 ctl2) {
            return false;
        }
        if (ctl2.GetState(out int state) == 0 && state != AudioSessionStateActive) {
            return false;
        }
        if (ctl2.IsSystemSoundsSession() == 0) {
            return false;
        }
        int pid = 0;
        ctl2.GetProcessId(out pid);
        if (pid == selfPid) {
            return false;
        }
        if (MatchesPausedApp(pid, paused)) {
            return false;
        }
        if (control is not ISimpleAudioVolume volume) {
            return false;
        }
        if (volume.GetMute(out int alreadyMuted) != 0 || alreadyMuted != 0) {
            return false;
        }
        if (volume.SetMute(1, IntPtr.Zero) != 0) {
            return false;
        }

        muted.Add(volume);
        Log.Write("audio: muted " + Describe(ctl2, pid));
        return true;
    }

    private static bool MatchesPausedApp(int pid, List<PausedMedia> paused) {
        if (paused.Count == 0 || pid <= 0) {
            return false;
        }
        string processName;
        try {
            using var process = Process.GetProcessById(pid);
            processName = process.ProcessName;
        } catch {
            return false;
        }
        if (string.IsNullOrWhiteSpace(processName)) {
            return false;
        }
        foreach (var item in paused) {
            if (item.Name.Contains(processName, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }
        return false;
    }

    private static void RestoreAll(List<PausedMedia> paused, List<ISimpleAudioVolume> muted) {
        RestoreVolumes(muted);
        ResumePaused(paused);
    }

    private sealed record PausedMedia(GlobalSystemMediaTransportControlsSession Session, string Name);

    private static string Describe(IAudioSessionControl2 ctl, int pid) {
        string name = "";
        if (ctl.GetDisplayName(out IntPtr display) == 0 && display != IntPtr.Zero) {
            name = Marshal.PtrToStringUni(display) ?? "";
            Marshal.FreeCoTaskMem(display);
        }
        if (string.IsNullOrWhiteSpace(name) && pid > 0) {
            try {
                using var process = Process.GetProcessById(pid);
                name = process.ProcessName;
            } catch {
                name = "pid=" + pid;
            }
        }
        return string.IsNullOrWhiteSpace(name) ? "session" : name;
    }

    private static void RestoreVolumes(List<ISimpleAudioVolume> muted) {
        foreach (var volume in muted) {
            try {
                volume.SetMute(0, IntPtr.Zero);
            } catch {
                // Session may already be gone (app closed while we had it muted).
            }
            Release(volume);
        }
    }

    private static void Release(object? com) {
        if (com == null) {
            return;
        }
        try {
            Marshal.ReleaseComObject(com);
        } catch {
            // Best effort — COM object may already be gone.
        }
    }

    // ---- Core Audio COM ----

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr properties);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2 {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint streamFlags, out IAudioSessionControl control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint streamFlags, out ISimpleAudioVolume volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        [PreserveSig] int RegisterSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
        [PreserveSig] int RegisterDuckNotification(IntPtr sessionId, IntPtr duckNotification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr duckNotification);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, IntPtr eventContext);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(IntPtr grouping, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notifications);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2 {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, IntPtr eventContext);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(IntPtr grouping, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notifications);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out int pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(int optOut);
    }

    [ComImport]
    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume {
        [PreserveSig] int SetMasterVolume(float level, IntPtr eventContext);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute(int mute, IntPtr eventContext);
        [PreserveSig] int GetMute(out int mute);
    }
}
