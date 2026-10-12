namespace EchoType.Audio;

/// <summary>
/// Shared Core Audio helpers. The MMDeviceEnumerator is a per-process
/// singleton, so its runtime wrapper is shared across every caller. Creating
/// it through a typed [ComImport] class makes the second class to ask fail
/// with "Unable to cast MMDeviceEnumerator to MMDeviceEnumerator"; creating
/// it from the CLSID returns a plain object each caller casts to its own
/// interface instead.
/// </summary>
internal static class CoreAudio {

    private static readonly Type DeviceEnumeratorType =
        Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), throwOnError: true)!;

    public static object CreateDeviceEnumerator() {
        return Activator.CreateInstance(DeviceEnumeratorType)!;
    }
}
