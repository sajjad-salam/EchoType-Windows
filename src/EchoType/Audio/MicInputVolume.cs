using System.Runtime.InteropServices;

namespace EchoType.Audio;

/// <summary>
/// Raises the Windows microphone input slider to 100% when a recording starts.
/// Windows (and some capture clients) sometimes drop that endpoint volume; the
/// level here is the same percentage shown in Sound settings, not the live meter.
/// </summary>
internal static class MicInputVolume {

    private const int DataFlowCapture = 1;
    private const int RoleConsole = 0;
    private const int RoleMultimedia = 1;
    private const int RoleCommunications = 2;
    private const int ClsCtxAll = 0x17;
    private const float FullLevel = 1f;
    /// <summary>Treat anything under this as not already at 100%.</summary>
    private const float AlreadyFull = 0.999f;
    private static readonly Guid IidEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    public static void EnsureFull() {
        try {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            try {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool found = false;
                found |= TryRaiseDefault(enumerator, RoleConsole, seen);
                found |= TryRaiseDefault(enumerator, RoleMultimedia, seen);
                found |= TryRaiseDefault(enumerator, RoleCommunications, seen);
                if (!found) {
                    Log.Write("audio: no default microphone to set");
                }
            } finally {
                Release(enumerator);
            }
        } catch (Exception ex) {
            Log.Write("audio: mic volume check failed: " + ex.Message);
        }
    }

    private static bool TryRaiseDefault(IMMDeviceEnumerator enumerator, int role, HashSet<string> seen) {
        if (enumerator.GetDefaultAudioEndpoint(DataFlowCapture, role, out IMMDevice? device) != 0
            || device == null) {
            return false;
        }
        try {
            string? id = DeviceId(device);
            if (id != null && !seen.Add(id)) {
                return true;
            }
            Raise(device);
            return true;
        } catch (Exception ex) {
            Log.Write("audio: mic volume set failed: " + ex.Message);
            return true;
        } finally {
            Release(device);
        }
    }

    private static void Raise(IMMDevice device) {
        Guid iid = IidEndpointVolume;
        if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object? raw) != 0
            || raw is not IAudioEndpointVolume volume) {
            Log.Write("audio: mic volume endpoint unavailable");
            return;
        }
        try {
            bool raised = false;
            int percent = -1;
            if (volume.GetMasterVolumeLevelScalar(out float level) == 0) {
                percent = (int)Math.Round(Math.Clamp(level, 0f, 1f) * 100f);
                if (level < AlreadyFull) {
                    int hr = volume.SetMasterVolumeLevelScalar(FullLevel, IntPtr.Zero);
                    if (hr != 0) {
                        Log.Write("audio: mic volume set failed hr=0x" + hr.ToString("X8"));
                    } else {
                        raised = true;
                    }
                }
            } else {
                Log.Write("audio: mic volume read failed");
            }

            bool unmuted = false;
            if (volume.GetMute(out int muted) == 0 && muted != 0) {
                int hr = volume.SetMute(0, IntPtr.Zero);
                if (hr != 0) {
                    Log.Write("audio: mic unmute failed hr=0x" + hr.ToString("X8"));
                } else {
                    unmuted = true;
                }
            }

            if (!raised && !unmuted) {
                return;
            }
            string was = percent >= 0 ? percent + "%" : "an unknown level";
            if (raised && unmuted) {
                Log.Write("audio: mic input was muted at " + was + ", unmuted and set to 100%");
            } else if (raised) {
                Log.Write("audio: mic input was " + was + ", set to 100%");
            } else {
                Log.Write("audio: mic input was muted, unmuted");
            }
        } finally {
            Release(volume);
        }
    }

    private static string? DeviceId(IMMDevice device) {
        if (device.GetId(out IntPtr id) != 0 || id == IntPtr.Zero) {
            return null;
        }
        try {
            return Marshal.PtrToStringUni(id);
        } finally {
            Marshal.FreeCoTaskMem(id);
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

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
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

    /// <summary>
    /// Vtable order matches IAudioEndpointVolume. Only the slots through mute are declared;
    /// later methods are unused and must not be inserted ahead of these.
    /// </summary>
    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out int count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, IntPtr eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(int channel, float levelDb, IntPtr eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(int channel, float level, IntPtr eventContext);
        [PreserveSig] int GetChannelVolumeLevel(int channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(int channel, out float level);
        [PreserveSig] int SetMute(int mute, IntPtr eventContext);
        [PreserveSig] int GetMute(out int mute);
    }
}
