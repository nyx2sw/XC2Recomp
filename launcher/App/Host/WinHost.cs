using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using XC2.Core;

namespace XC2.Launcher.Host;

/// <summary>
/// Frameless native Win32 window hosting CoreWebView2 directly (zero Electron/WPF/WinForms overhead).
/// Features Windows 11 DWM shadow &amp; rounded corners, PerMonitorV2 DPI scaling, native drag-and-drop file paths,
/// native IFileOpenDialog pickers, and built-in visual capture (XC2_LAUNCHER_SHOT) for automated UI verification.
/// </summary>
public sealed class WinHost
{
    public const string VirtualHost = "app.xc2";
    public string Origin => "https://" + VirtualHost + "/";
    public event Action<string, string[]>? Message;
    public event Action? Closing;

    readonly Content content;
    readonly string webViewDataDir;
    readonly bool devTools;
    readonly int baseWidth, baseHeight;
    IntPtr hwnd;
    WndProc? proc;
    CoreWebView2Environment? env;
    CoreWebView2Controller? ctl;
    CoreWebView2? web;
    readonly ConcurrentQueue<Action> queue = new();
    readonly List<string> earlyMessages = new();
    bool pageReady;

    public WinHost(Content content, string webViewDataDir, bool devTools, int width = 1280, int height = 800)
    {
        this.content = content;
        this.webViewDataDir = webViewDataDir;
        this.devTools = devTools;
        baseWidth = width;
        baseHeight = height;
    }

    public static string? RuntimeVersion()
    {
        try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch { return null; }
    }

    public void Run()
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4)); // PerMonitorV2
        var hinst = GetModuleHandle(null);
        proc = WindowProc;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            style = 0x0003,
            lpfnWndProc = proc,
            hInstance = hinst,
            hCursor = LoadCursor(IntPtr.Zero, 32512),
            hbrBackground = CreateSolidBrush(0x002E1409), // #09142E in BGR
            lpszClassName = "XC2PCLauncherWindow",
            hIcon = LoadIcon(hinst, new IntPtr(32512)),
            hIconSm = LoadIcon(hinst, new IntPtr(32512)),
        };
        RegisterClassEx(ref wc);

        var dpi = GetDpiForSystem();
        var work = new RECT();
        SystemParametersInfo(0x0030, 0, ref work, 0);
        double k = dpi / 96.0;
        double ww = work.right - work.left;
        double wh = work.bottom - work.top;
        k = Math.Min(k, Math.Min(ww * 0.92 / baseWidth, wh * 0.92 / baseHeight));
        int w = (int)(baseWidth * k);
        int h = (int)(baseHeight * k);
        int x = work.left + (int)(ww - w) / 2;
        int y = work.top + (int)(wh - h) / 2;

        const uint style = 0x00CF0000u & ~0x00040000u; // WS_OVERLAPPEDWINDOW without WS_THICKFRAME (maximize supported)
        hwnd = CreateWindowEx(0, "XC2PCLauncherWindow", "Xenoblade Chronicles 2 · PC Edition", style,
            x, y, w, h, IntPtr.Zero, IntPtr.Zero, hinst, IntPtr.Zero);

        var margins = new MARGINS { cyBottomHeight = 1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, 4);
        int cornerPref = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, ref cornerPref, 4);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0027);

        SynchronizationContext.SetSynchronizationContext(new WinSyncContext(this));
        _ = StartWebViewAsync();

        ShowWindow(hwnd, 5);
        UpdateWindow(hwnd);

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        try { ctl?.Close(); } catch { }
    }

    async Task StartWebViewAsync()
    {
        try
        {
            var opts = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required --disable-features=msSmartScreenProtection");
            env = await CoreWebView2Environment.CreateAsync(null, webViewDataDir, opts);
            ctl = await env.CreateCoreWebView2ControllerAsync(hwnd);
            ctl.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 9, 20, 46);
            Resize();

            web = ctl.CoreWebView2;
            var s = web.Settings;
            s.AreDevToolsEnabled = devTools;
            s.AreDefaultContextMenusEnabled = devTools;
            s.AreBrowserAcceleratorKeysEnabled = devTools;
            s.IsZoomControlEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsPinchZoomEnabled = false;
            s.IsSwipeNavigationEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsNonClientRegionSupportEnabled = true;

            web.AddWebResourceRequestedFilter("https://" + VirtualHost + "/*", CoreWebView2WebResourceContext.All);
            web.WebResourceRequested += OnWebResourceRequested;
            web.WebMessageReceived += OnWebMessageReceived;
            web.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternalUrl(e.Uri); };
            web.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    OpenExternalUrl(e.Uri);
                }
            };
            web.ProcessFailed += (_, e) => LauncherLog.Write("webview failed: " + e.ProcessFailedKind);

            var startSuffix = Environment.GetEnvironmentVariable("XC2_LAUNCHER_START") ?? "";
            web.Navigate(Origin + "ui/index.html" + startSuffix);
            ctl.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        }
        catch (Exception ex)
        {
            LauncherLog.Write("webview startup error: " + ex);
            MessageBoxW(hwnd, "Não foi possível iniciar a interface do Launcher (Microsoft Edge WebView2).\n\n" + ex.Message,
                "Xenoblade Chronicles 2 PC Edition", 0x10);
            Environment.Exit(3);
        }
    }

    async void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var path = new Uri(e.Request.Uri).AbsolutePath;
        if (!path.StartsWith("/media/", StringComparison.OrdinalIgnoreCase))
        {
            var got = content.Get(path);
            e.Response = got is { } g
                ? env!.CreateWebResourceResponse(new MemoryStream(g.Data), 200, "OK",
                    $"Content-Type: {g.Mime}\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: *")
                : env!.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store");
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var got = await Task.Run(() => content.Get(path));
            e.Response = got is { } g
                ? env!.CreateWebResourceResponse(new MemoryStream(g.Data), 200, "OK",
                    $"Content-Type: {g.Mime}\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: *")
                : env!.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store");
        }
        catch (Exception ex)
        {
            LauncherLog.Write($"resource {path}: {ex.Message}");
            try { e.Response = env!.CreateWebResourceResponse(null, 500, "Error", "Cache-Control: no-store"); } catch { }
        }
        finally
        {
            deferral.Complete();
        }
    }

    void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try { json = e.TryGetWebMessageAsString(); }
        catch { json = e.WebMessageAsJson; }

        var files = new List<string>();
        try
        {
            if (e.AdditionalObjects != null)
            {
                foreach (var obj in e.AdditionalObjects)
                    if (obj is CoreWebView2File f) files.Add(f.Path);
            }
        }
        catch { }

        if (json.Contains("\"t\":\"ready\""))
        {
            pageReady = true;
            foreach (var pending in earlyMessages) web!.PostWebMessageAsString(pending);
            earlyMessages.Clear();

            if (Environment.GetEnvironmentVariable("XC2_LAUNCHER_SHOT") is { Length: > 0 } spec)
                _ = CaptureShotsAsync(spec);
        }

        Message?.Invoke(json, files.ToArray());
    }

    /// <summary>Automated visual QA capture: XC2_LAUNCHER_SHOT="path1.png@1500;path2.png@2500:tab=saves"</summary>
    async Task CaptureShotsAsync(string spec)
    {
        try
        {
            foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var segs = part.Split('|');
                var file = segs[0].Trim();
                int delayMs = segs.Length > 1 && int.TryParse(segs[1], out var d) ? d : 1400;
                string? js = segs.Length > 2 ? segs[2] : null;

                if (!string.IsNullOrWhiteSpace(js))
                    await web!.ExecuteScriptAsync(js);

                await Task.Delay(delayMs);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
                using var fs = File.Create(file);
                await web!.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
                LauncherLog.Write("shot captured: " + file);
            }
        }
        catch (Exception ex) { LauncherLog.Write("shot error: " + ex); }
        Close();
    }

    public void Post(string json) => Invoke(() =>
    {
        if (web == null || !pageReady) { earlyMessages.Add(json); return; }
        web.PostWebMessageAsString(json);
    });

    public void Invoke(Action action)
    {
        queue.Enqueue(action);
        if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_RUN, IntPtr.Zero, IntPtr.Zero);
    }

    public void Minimize() => Invoke(() => ShowWindow(hwnd, 6));
    public void ToggleMaximize() => Invoke(() => ShowWindow(hwnd, IsZoomed(hwnd) ? 9 : 3));
    public void Close() => Invoke(() => PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero));
    public void Activate() => Invoke(() => { if (IsIconic(hwnd)) ShowWindow(hwnd, 9); SetForegroundWindow(hwnd); });

    void Resize()
    {
        if (ctl == null) return;
        GetClientRect(hwnd, out var r);
        ctl.Bounds = new System.Drawing.Rectangle(0, 0, r.right - r.left, r.bottom - r.top);
    }

    const uint WM_RUN = 0x8001;
    IntPtr WindowProc(IntPtr h, uint msg, IntPtr wp, IntPtr lp)
    {
        switch (msg)
        {
            case WM_RUN:
                while (queue.TryDequeue(out var a))
                {
                    try { a(); } catch (Exception ex) { LauncherLog.Write("ui dispatch: " + ex); }
                }
                return IntPtr.Zero;
            case 0x0083: // WM_NCCALCSIZE
                if (wp != IntPtr.Zero)
                {
                    if (IsZoomed(h))
                    {
                        var p = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(lp);
                        var mon = MonitorFromWindow(h, 2);
                        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                        GetMonitorInfo(mon, ref mi);
                        p.rgrc0 = mi.rcWork;
                        Marshal.StructureToPtr(p, lp, false);
                    }
                    return IntPtr.Zero;
                }
                break;
            case 0x0005: // WM_SIZE
                Resize();
                Post("{\"t\":\"max\",\"on\":" + (IsZoomed(h) ? "true" : "false") + "}");
                return IntPtr.Zero;
            case 0x0003: // WM_MOVE
                ctl?.NotifyParentWindowPositionChanged();
                break;
            case 0x0006: // WM_ACTIVATE
                bool active = (wp.ToInt64() & 0xFFFF) != 0;
                if (active) ctl?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
                Post("{\"t\":\"focus\",\"on\":" + (active ? "true" : "false") + "}");
                break;
            case 0x02E0: // WM_DPICHANGED
                var rc = Marshal.PtrToStructure<RECT>(lp);
                SetWindowPos(h, IntPtr.Zero, rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top, 0x0014);
                return IntPtr.Zero;
            case 0x0010: // WM_CLOSE
                Closing?.Invoke();
                DestroyWindow(h);
                return IntPtr.Zero;
            case 0x0002: // WM_DESTROY
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(h, msg, wp, lp);
    }

    public string? PickFile(string title, string filterName, string pattern) => PickOnUiThread(title, false, filterName, pattern);
    public string? PickFolder(string title) => PickOnUiThread(title, true, null, null);

    string? PickOnUiThread(string title, bool folder, string? filterName, string? pattern)
    {
        var tcs = new TaskCompletionSource<string?>();
        Invoke(() =>
        {
            try { tcs.SetResult(ShowOpenDialog(title, folder, filterName, pattern)); }
            catch (Exception ex) { LauncherLog.Write("picker: " + ex.Message); tcs.SetResult(null); }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    string? ShowOpenDialog(string title, bool folder, string? filterName, string? pattern)
    {
        var dlg = (IFileOpenDialog)new FileOpenDialogRCW();
        try
        {
            dlg.GetOptions(out var o);
            dlg.SetOptions(o | 0x40u | (folder ? 0x20u : 0u) | 0x1000u);
            dlg.SetTitle(title);
            if (!folder && filterName != null && pattern != null)
            {
                dlg.SetFileTypes(2,
                [
                    new COMDLG_FILTERSPEC { pszName = filterName, pszSpec = pattern },
                    new COMDLG_FILTERSPEC { pszName = "Todos os arquivos", pszSpec = "*.*" }
                ]);
            }
            if (dlg.Show(hwnd) != 0) return null;
            dlg.GetResult(out var item);
            item.GetDisplayName(0x80058000u, out var path);
            return path;
        }
        finally { Marshal.ReleaseComObject(dlg); }
    }

    static void OpenExternalUrl(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    sealed class WinSyncContext(WinHost host) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => host.Invoke(() => d(state));
    }

    // ==================== Win32 Interop ====================
    delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX { public int cbSize; public uint style; public WndProc lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName, lpszClassName; public IntPtr hIconSm; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] struct NCCALCSIZE_PARAMS { public RECT rgrc0, rgrc1, rgrc2; public IntPtr lppos; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct COMDLG_FILTERSPEC { public string pszName; public string pszSpec; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool UpdateWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint act, uint p, ref RECT r, uint winini);
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr inst, int id);
    [DllImport("user32.dll")] static extern IntPtr LoadIcon(IntPtr inst, IntPtr id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(IntPtr h, string text, string caption, uint type);
    [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(uint bgr);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int sz);

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")] class FileOpenDialogRCW { }
    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem { void BindToHandler(); void GetParent(); void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name); void GetAttributes(); void Compare(); }
    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint n, [MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] specs);
        void SetFileTypeIndex(uint i); void GetFileTypeIndex(out uint i);
        void Advise(); void Unadvise();
        void SetOptions(uint o); void GetOptions(out uint o);
        void SetDefaultFolder(IShellItem i); void SetFolder(IShellItem i); void GetFolder(out IShellItem i); void GetCurrentSelection(out IShellItem i);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string n); void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string n);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string t);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string t); void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string t);
        void GetResult(out IShellItem i);
        void AddPlace(IShellItem i, int fdap); void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string e);
        void Close(int hr); void SetClientGuid(ref Guid g); void ClearClientData(); void SetFilter(IntPtr f);
        void GetResults(out IntPtr e); void GetSelectedItems(out IntPtr e);
    }
}
