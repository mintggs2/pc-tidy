using System.Runtime.InteropServices;

namespace PcTidy.Platform;

/// <summary>
/// Shell COM helpers. Shell.Application and Explorer's own automation objects must be used from an STA thread, so each
/// call gets a short-lived STA thread of its own.
/// </summary>
internal static class ShellInterop
{
    public static T OnSta<T>(Func<T> action)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Windows' shell didn't answer in time.");
        if (error is not null) throw error is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : error;
        return result;
    }

    /// <summary>
    /// Starts a program through Explorer (IShellDispatch2.ShellExecute on the desktop's folder view), so it runs with
    /// Explorer's normal user token even when PC Tidy itself is elevated. Admin rights are never passed on.
    /// </summary>
    public static void ShellExecuteAsExplorer(string file, string arguments, string directory) => OnSta(() =>
    {
        var shellWindows = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!)!;
        try
        {
            object location = 0, root = 0; // CSIDL_DESKTOP
            dynamic windows = shellWindows;
            object desktop = windows.FindWindowSW(ref location, ref root, 8 /* SWC_DESKTOP */, out int _, 1 /* SWFO_NEEDDISPATCH */);
            var provider = (IServiceProvider)desktop;
            var sidTopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
            var iidShellBrowser = typeof(IShellBrowser).GUID;
            Marshal.ThrowExceptionForHR(provider.QueryService(ref sidTopLevelBrowser, ref iidShellBrowser, out var browserPtr));
            var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(browserPtr);
            Marshal.Release(browserPtr);
            Marshal.ThrowExceptionForHR(browser.QueryActiveShellView(out var view));
            var iidDispatch = new Guid("00020400-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(view.GetItemObject(0 /* SVGIO_BACKGROUND */, ref iidDispatch, out var folderViewPtr));
            dynamic folderView = Marshal.GetObjectForIUnknown(folderViewPtr);
            Marshal.Release(folderViewPtr);
            dynamic shell = folderView.Application;
            shell.ShellExecute(file, arguments, directory, "open", 1);
            return 0;
        }
        finally { Marshal.FinalReleaseComObject(shellWindows); }
    });

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid riid, out IntPtr obj);
    }

    /// <summary>IShellBrowser (shobjidl_core.h). Only QueryActiveShellView is called; the rest keeps the vtable order.</summary>
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out IntPtr hwnd);
        [PreserveSig] int ContextSensitiveHelp(int enterMode);
        [PreserveSig] int InsertMenusSB(IntPtr menu, IntPtr widths);
        [PreserveSig] int SetMenuSB(IntPtr menu, IntPtr olemenu, IntPtr activeObject);
        [PreserveSig] int RemoveMenusSB(IntPtr menu);
        [PreserveSig] int SetStatusTextSB(IntPtr text);
        [PreserveSig] int EnableModelessSB(int enable);
        [PreserveSig] int TranslateAcceleratorSB(IntPtr msg, ushort id);
        [PreserveSig] int BrowseObject(IntPtr pidl, uint flags);
        [PreserveSig] int GetViewStateStream(uint mode, out IntPtr stream);
        [PreserveSig] int GetControlWindow(uint id, out IntPtr hwnd);
        [PreserveSig] int SendControlMsg(uint id, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
        [PreserveSig] int QueryActiveShellView(out IShellView view);
    }

    /// <summary>IShellView. Only GetItemObject is called; the rest keeps the vtable order.</summary>
    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        [PreserveSig] int GetWindow(out IntPtr hwnd);
        [PreserveSig] int ContextSensitiveHelp(int enterMode);
        [PreserveSig] int TranslateAccelerator(IntPtr msg);
        [PreserveSig] int EnableModeless(int enable);
        [PreserveSig] int UIActivate(uint state);
        [PreserveSig] int Refresh();
        [PreserveSig] int CreateViewWindow(IntPtr previous, IntPtr settings, IntPtr browser, IntPtr rect, out IntPtr hwnd);
        [PreserveSig] int DestroyViewWindow();
        [PreserveSig] int GetCurrentInfo(IntPtr settings);
        [PreserveSig] int AddPropertySheetPages(uint reserved, IntPtr callback, IntPtr lParam);
        [PreserveSig] int SaveViewState();
        [PreserveSig] int SelectItem(IntPtr pidl, uint flags);
        [PreserveSig] int GetItemObject(uint item, ref Guid riid, out IntPtr obj);
    }
}
