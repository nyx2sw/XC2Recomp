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

        // If relaunched right after self-update, wait for the previous launcher instance to release the mutex
        int prevPid = ParseIntArg(args, "--updated-from");
        string? updatedVersion = ParseStringArg(args, "--updated-version");
        if (prevPid > 0)
            LauncherUpdater.WaitForPreviousProcess(prevPid);
        LauncherUpdater.CleanupPreviousBinary(Environment.ProcessPath);

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
        var backend = new Backend(host, content, projectRoot, config, updatedVersion);

        host.Message += backend.OnMessage;
        LauncherLog.Write($"startup: v{LauncherUpdater.CurrentVersion}, root={projectRoot}, webview2={WinHost.RuntimeVersion()}");

        host.Run();
        return 0;
    }

    static int ParseIntArg(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out var v))
                return v;
        return 0;
    }

    static string? ParseStringArg(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(args[i + 1]))
                return args[i + 1];
        return null;
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
