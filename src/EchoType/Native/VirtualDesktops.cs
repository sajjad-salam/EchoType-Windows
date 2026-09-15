using System.Runtime.InteropServices;

namespace EchoType.Native;

/// <summary>
/// Switch to the virtual desktop that hosts a window. Uses the documented
/// IVirtualDesktopManager where possible, and the undocumented ImmersiveShell
/// IVirtualDesktopManagerInternal (versioned by OS build) to actually switch
/// the current desktop. A helper-window fallback covers builds whose COM IID
/// we don't know yet.
/// </summary>
internal static class VirtualDesktops {

    private static readonly Guid ImmersiveShellClsid = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid VirtualDesktopManagerInternalClsid = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
    private static readonly Guid VirtualDesktopManagerClsid = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");
    private static readonly Guid AppOnAllDesktops = new("BB64D5B7-4DE3-4AB2-A87C-DB7601AEA7DC");
    private static readonly Guid WindowOnAllDesktops = new("C2DDEA68-66F2-4CF9-8264-1BFD00FBBBAC");

    private static IVirtualDesktopManager? _manager;
    private static object? _internal;
    private static Guid _internalIid;
    private static IApplicationViewCollection? _views;
    private static bool _comInitAttempted;

    public static bool TryGetDesktopId(IntPtr hwnd, out Guid desktopId) {
        desktopId = Guid.Empty;
        var manager = Manager();
        if (manager == null || hwnd == IntPtr.Zero) {
            return false;
        }
        try {
            return manager.GetWindowDesktopId(hwnd, out desktopId) == 0 && desktopId != Guid.Empty;
        } catch (Exception ex) {
            Log.Write("paste: GetWindowDesktopId threw: " + ex.Message);
            return false;
        }
    }

    public static bool IsOnCurrentDesktop(IntPtr hwnd) {
        var manager = Manager();
        if (manager == null || hwnd == IntPtr.Zero) {
            return true; // unknown — don't block restore
        }
        try {
            return manager.IsWindowOnCurrentVirtualDesktop(hwnd, out int onCurrent) == 0 && onCurrent != 0;
        } catch {
            return true;
        }
    }

    /// <summary>Switch the current virtual desktop to the one that hosts <paramref name="hwnd"/>.</summary>
    public static bool SwitchToDesktopOf(IntPtr hwnd) {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) {
            return false;
        }
        if (IsOnCurrentDesktop(hwnd)) {
            return true;
        }
        if (!TryGetDesktopId(hwnd, out Guid id) || IsPinnedDesktop(id)) {
            return IsOnCurrentDesktop(hwnd);
        }

        if (TrySwitchInternal(id) && WaitUntilOnCurrent(hwnd, 800)) {
            return true;
        }
        if (TrySwitchWithHelperWindow(id, hwnd)) {
            return true;
        }
        return IsOnCurrentDesktop(hwnd);
    }

    /// <summary>
    /// IApplicationView.SwitchTo activates the window and often follows it
    /// across virtual desktops. Best-effort; failures are ignored.
    /// </summary>
    public static bool TrySwitchToView(IntPtr hwnd) {
        EnsureCom();
        if (_views == null || hwnd == IntPtr.Zero) {
            return false;
        }
        try {
            if (_views.GetViewForHwnd(hwnd, out IApplicationView view) != 0 || view == null) {
                return false;
            }
            view.SwitchTo();
            view.SetFocus();
            return true;
        } catch (Exception ex) {
            Log.Write("paste: SwitchTo view failed: " + ex.Message);
            return false;
        }
    }

    private static bool TrySwitchInternal(Guid desktopId) {
        EnsureCom();
        if (_internal == null) {
            return false;
        }
        try {
            if (_internalIid == typeof(IVirtualDesktopManagerInternal24H2).GUID) {
                var mgr = (IVirtualDesktopManagerInternal24H2)_internal;
                var desktop = mgr.FindDesktop(ref desktopId);
                if (desktop == null) {
                    return false;
                }
                mgr.SwitchDesktop(desktop);
                return true;
            }
            if (_internalIid == typeof(IVirtualDesktopManagerInternal11).GUID) {
                var mgr = (IVirtualDesktopManagerInternal11)_internal;
                var desktop = mgr.FindDesktop(ref desktopId);
                if (desktop == null) {
                    return false;
                }
                mgr.SwitchDesktop(IntPtr.Zero, desktop);
                return true;
            }
            if (_internalIid == typeof(IVirtualDesktopManagerInternal10).GUID) {
                var mgr10 = (IVirtualDesktopManagerInternal10)_internal;
                var desktop10 = mgr10.FindDesktop(ref desktopId);
                if (desktop10 == null) {
                    return false;
                }
                mgr10.SwitchDesktop(desktop10);
                return true;
            }
            return false;
        } catch (Exception ex) {
            Log.Write("paste: SwitchDesktop internal failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Documented fallback: move a window we own onto the target desktop and
    /// activate it, which causes Windows to switch there.
    /// </summary>
    private static bool TrySwitchWithHelperWindow(Guid desktopId, IntPtr targetHwnd) {
        var manager = Manager();
        if (manager == null) {
            return false;
        }
        SwitcherForm? form = null;
        try {
            form = new SwitcherForm();
            form.Show();
            Guid id = desktopId;
            if (manager.MoveWindowToDesktop(form.Handle, ref id) != 0) {
                return false;
            }
            form.Activate();
            NativeMethods.SetForegroundWindow(form.Handle);
            return WaitUntilOnCurrent(targetHwnd, 800);
        } catch (Exception ex) {
            Log.Write("paste: helper-window desktop switch failed: " + ex.Message);
            return false;
        } finally {
            try {
                form?.Close();
                form?.Dispose();
            } catch { /* best effort */ }
        }
    }

    private static bool WaitUntilOnCurrent(IntPtr hwnd, int timeoutMs) {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) {
            if (IsOnCurrentDesktop(hwnd)) {
                return true;
            }
            Thread.Sleep(40);
        }
        return IsOnCurrentDesktop(hwnd);
    }

    private static bool IsPinnedDesktop(Guid id) =>
        id == AppOnAllDesktops || id == WindowOnAllDesktops;

    private static IVirtualDesktopManager? Manager() {
        EnsureCom();
        return _manager;
    }

    private static void EnsureCom() {
        if (_comInitAttempted) {
            return;
        }
        _comInitAttempted = true;
        try {
            Type? managerType = Type.GetTypeFromCLSID(VirtualDesktopManagerClsid, throwOnError: false);
            if (managerType != null) {
                _manager = (IVirtualDesktopManager?)Activator.CreateInstance(managerType);
            }
        } catch (Exception ex) {
            Log.Write("paste: IVirtualDesktopManager unavailable: " + ex.Message);
        }

        try {
            Type? shellType = Type.GetTypeFromCLSID(ImmersiveShellClsid, throwOnError: false);
            if (shellType == null) {
                return;
            }
            object? shellObj = Activator.CreateInstance(shellType);
            if (shellObj is not IServiceProvider10 shell) {
                return;
            }

            Guid service = VirtualDesktopManagerInternalClsid;
            foreach (Guid candidate in InternalIidsByPreference()) {
                Guid iid = candidate;
                if (shell.QueryService(ref service, ref iid, out object intern) == 0 && intern != null) {
                    _internal = intern;
                    _internalIid = candidate;
                    break;
                }
            }

            Guid viewsIid = typeof(IApplicationViewCollection).GUID;
            if (shell.QueryService(ref viewsIid, ref viewsIid, out object views) == 0) {
                _views = (IApplicationViewCollection)views;
            }
            Log.Write("paste: virtual-desktop COM "
                + (_internal != null ? "internal=yes" : "internal=no")
                + (_views != null ? " views=yes" : " views=no")
                + (_manager != null ? " manager=yes" : " manager=no"));
        } catch (Exception ex) {
            Log.Write("paste: ImmersiveShell virtual-desktop services unavailable: " + ex.Message);
        }
    }

    private static Guid[] InternalIidsByPreference() {
        Guid v24 = typeof(IVirtualDesktopManagerInternal24H2).GUID;
        Guid v11 = typeof(IVirtualDesktopManagerInternal11).GUID;
        Guid v10 = typeof(IVirtualDesktopManagerInternal10).GUID;
        int build = Environment.OSVersion.Version.Build;
        if (build >= 26100) {
            return [v24, v11, v10];
        }
        if (build >= 22000) {
            return [v11, v24, v10];
        }
        return [v10, v11];
    }

    private sealed class SwitcherForm : Form {
        public SwitcherForm() {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            Size = new Size(1, 1);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Opacity = 0.01;
        }

        protected override CreateParams CreateParams {
            get {
                var cp = base.CreateParams;
                cp.ExStyle |= unchecked((int)NativeMethods.WS_EX_TOOLWINDOW);
                return cp;
            }
        }
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
    private interface IVirtualDesktopManager {
        [PreserveSig]
        int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);

        [PreserveSig]
        int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);

        [PreserveSig]
        int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    private interface IServiceProvider10 {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    [Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513")]
    private interface IApplicationView {
        int SetFocus();
        int SwitchTo();
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5")]
    private interface IApplicationViewCollection {
        int GetViews(out object array);
        int GetViewsByZOrder(out object array);
        int GetViewsByAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string id, out object array);
        [PreserveSig]
        int GetViewForHwnd(IntPtr hwnd, out IApplicationView view);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")]
    private interface IVirtualDesktop24H2 {
        bool IsViewVisible(object view);
        Guid GetId();
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("53F5CA0B-158F-4124-900C-057158060B27")]
    private interface IVirtualDesktopManagerInternal24H2 {
        int GetCount();
        void MoveViewToDesktop(object view, IVirtualDesktop24H2 desktop);
        bool CanViewMoveDesktops(object view);
        IVirtualDesktop24H2 GetCurrentDesktop();
        void GetDesktops(out object desktops);
        [PreserveSig]
        int GetAdjacentDesktop(IVirtualDesktop24H2 from, int direction, out IVirtualDesktop24H2 desktop);
        void SwitchDesktop(IVirtualDesktop24H2 desktop);
        void SwitchDesktopAndMoveForegroundView(IVirtualDesktop24H2 desktop);
        IVirtualDesktop24H2 CreateDesktop();
        void MoveDesktop(IVirtualDesktop24H2 desktop, int nIndex);
        void RemoveDesktop(IVirtualDesktop24H2 desktop, IVirtualDesktop24H2 fallback);
        IVirtualDesktop24H2 FindDesktop(ref Guid desktopId);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("536D3495-B208-4CC9-AE26-DE8111275BF8")]
    private interface IVirtualDesktop11 {
        bool IsViewVisible(object view);
        Guid GetId();
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("B2F925B9-5A0F-4D2E-9F4D-2B1507593C10")]
    private interface IVirtualDesktopManagerInternal11 {
        int GetCount(IntPtr hWndOrMon);
        void MoveViewToDesktop(object view, IVirtualDesktop11 desktop);
        bool CanViewMoveDesktops(object view);
        IVirtualDesktop11 GetCurrentDesktop(IntPtr hWndOrMon);
        void GetDesktops(IntPtr hWndOrMon, out object desktops);
        [PreserveSig]
        int GetAdjacentDesktop(IVirtualDesktop11 from, int direction, out IVirtualDesktop11 desktop);
        void SwitchDesktop(IntPtr hWndOrMon, IVirtualDesktop11 desktop);
        IVirtualDesktop11 CreateDesktop(IntPtr hWndOrMon);
        void MoveDesktop(IVirtualDesktop11 desktop, IntPtr hWndOrMon, int nIndex);
        void RemoveDesktop(IVirtualDesktop11 desktop, IVirtualDesktop11 fallback);
        IVirtualDesktop11 FindDesktop(ref Guid desktopId);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("FF72FFDD-BE7E-43FC-9C03-AD81681E88E4")]
    private interface IVirtualDesktop10 {
        bool IsViewVisible(object view);
        Guid GetId();
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("F31574D6-B682-4CDC-BD56-1827860ABEC6")]
    private interface IVirtualDesktopManagerInternal10 {
        int GetCount();
        void MoveViewToDesktop(object view, IVirtualDesktop10 desktop);
        bool CanViewMoveDesktops(object view);
        IVirtualDesktop10 GetCurrentDesktop();
        void GetDesktops(out object desktops);
        [PreserveSig]
        int GetAdjacentDesktop(IVirtualDesktop10 from, int direction, out IVirtualDesktop10 desktop);
        void SwitchDesktop(IVirtualDesktop10 desktop);
        IVirtualDesktop10 CreateDesktop();
        void RemoveDesktop(IVirtualDesktop10 desktop, IVirtualDesktop10 fallback);
        IVirtualDesktop10 FindDesktop(ref Guid desktopId);
    }
}
