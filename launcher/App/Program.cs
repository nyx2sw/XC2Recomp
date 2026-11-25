using System.Runtime.InteropServices;
using XC2.Core;
using XC2.Launcher.Host;

namespace XC2.Launcher;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        LauncherLog.Init();
        bool devTools = args.Any(a => a.Equals("--dev", StringComparison.OrdinalIgnoreCase));
        bool shotMode = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XC2_LAUNCHER_SHOT"));

        // Single-instance guard (bypassed only during automated screenshot QA if needed)
        using var mutex = new Mutex(true, @"Local\XC2PCEditionLauncherSingleInstance", out bool isFirst);
        if (!isFirst && !shotMode)
        {
            FocusExistingLauncher();
            return 0;
        }

        if (WinHost.RuntimeVersion() == null)
        {
            MessageBoxW(IntPtr.Zero,
                "Microsoft Edge WebView2 Runtime não foi encontrado neste PC.\nInstale o WebView2 Runtime da Microsoft para abrir o Launcher.",
                "Xenoblade Chronicles 2 · PC Edition", 0x10);
            return 3;
        }

        var projectRoot = GameInstall.FindProjectRoot();
        var config = LauncherConfig.Load();
        var content = new Content();
        var host = new WinHost(content, LauncherPaths.WebViewDataDir, devTools);
        var backend = new Backend(host, content, projectRoot, config);

        host.Message += backend.OnMessage;
        LauncherLog.Write($"startup: root={projectRoot}, webview2={WinHost.RuntimeVersion()}");

        host.Run();
        return 0;
    }

    static void FocusExistingLauncher()
    {
        var h = FindWindowW("XC2PCLauncherWindow", null);
        if (h != IntPtr.Zero)
        {
            if (IsIconic(h)) ShowWindow(h, 9);
            SetForegroundWindow(h);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string? title);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(IntPtr h, string text, string caption, uint type);
}
