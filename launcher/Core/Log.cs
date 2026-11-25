namespace XC2.Core;

public static class LauncherLog
{
    static readonly object Gate = new();
    static string? logPath;

    public static string LogPath => logPath ??= Path.Combine(LauncherPaths.DataDir, "launcher.log");

    public static void Init(string? customDir = null)
    {
        var dir = customDir ?? LauncherPaths.DataDir;
        Directory.CreateDirectory(dir);
        logPath = Path.Combine(dir, "launcher.log");
        try
        {
            var fi = new FileInfo(logPath);
            if (fi.Exists && fi.Length > 2 * 1024 * 1024)
            {
                var prev = logPath + ".old";
                if (File.Exists(prev)) File.Delete(prev);
                File.Move(logPath, prev);
            }
        }
        catch { }
        Write("=== Xenoblade Chronicles 2 PC Edition Launcher ===");
    }

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, line);
            }
            catch { }
        }
    }
}

public static class LauncherPaths
{
    public static string DataDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("XC2_LAUNCHER_DATA");
            var dir = !string.IsNullOrWhiteSpace(env)
                ? env
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Xenoblade2PC");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string MediaCacheDir => Ensure(Path.Combine(DataDir, "media"));
    public static string SaveBackupsDir => Ensure(Path.Combine(DataDir, "save-backups"));
    public static string WebViewDataDir => Ensure(Path.Combine(DataDir, "webview2"));
    public static string ConfigFile => Path.Combine(DataDir, "launcher.json");

    static string Ensure(string p)
    {
        Directory.CreateDirectory(p);
        return p;
    }
}
