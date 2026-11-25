using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace XC2.Core;

public sealed record HardwareInfo(
    string GpuName,
    string GpuVendor,
    bool IsNvidiaRtx,
    double GpuVramGb,
    int ScreenWidth,
    int ScreenHeight,
    int MonitorRefreshHz,
    double TotalRamGb,
    double FreeCDriveGb,
    double FreeInstallDriveGb,
    string InstallDriveLetter,
    bool DiskBudgetOk
);

public static class SystemProbe
{
    public static HardwareInfo Probe(string installDir)
    {
        var (gpuName, gpuVendor, vramGb) = DetectPrimaryGpu();
        bool isRtx = gpuName.Contains("RTX", StringComparison.OrdinalIgnoreCase);
        var (sw, sh, hz) = DetectPrimaryDisplay();
        double ramGb = DetectRamGb();

        double freeC = DriveFreeGb("C:\\");
        var root = Path.GetPathRoot(Path.GetFullPath(installDir)) ?? "F:\\";
        double freeInst = DriveFreeGb(root);
        bool budgetOk = freeC >= 20.0 && freeInst >= 15.0; // STORAGE_POLICY.md floors

        return new HardwareInfo(
            GpuName: gpuName,
            GpuVendor: gpuVendor,
            IsNvidiaRtx: isRtx,
            GpuVramGb: Math.Round(vramGb, 1),
            ScreenWidth: sw,
            ScreenHeight: sh,
            MonitorRefreshHz: hz,
            TotalRamGb: Math.Round(ramGb, 1),
            FreeCDriveGb: Math.Round(freeC, 2),
            FreeInstallDriveGb: Math.Round(freeInst, 2),
            InstallDriveLetter: root.TrimEnd('\\'),
            DiskBudgetOk: budgetOk
        );
    }

    static double DriveFreeGb(string root)
    {
        try { return new DriveInfo(root).AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0); }
        catch { return 0; }
    }

    static (string Name, string Vendor, double VramGb) DetectPrimaryGpu()
    {
        var gpus = new List<(string Name, string Vendor, double VramGb, int Score)>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}", false);
            if (cls != null)
            {
                foreach (var sub in cls.GetSubKeyNames())
                {
                    if (sub.Length != 4 || !int.TryParse(sub, out _)) continue;
                    using var k = cls.OpenSubKey(sub, false);
                    var name = k?.GetValue("DriverDesc") as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Remote", StringComparison.OrdinalIgnoreCase)) continue;

                    string vendor = "Outro";
                    int score = 1;
                    if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase))
                    { vendor = "NVIDIA"; score = 3; }
                    else if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                    { vendor = "AMD"; score = 2; }
                    else if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                    { vendor = "Intel"; score = 1; }

                    double vramGb = 0;
                    var qw = k?.GetValue("HardwareInformation.qwMemorySize");
                    if (qw is long l && l > 0) vramGb = l / (1024.0 * 1024.0 * 1024.0);
                    else if (qw is byte[] arr && arr.Length >= 8)
                        vramGb = BitConverter.ToUInt64(arr, 0) / (1024.0 * 1024.0 * 1024.0);
                    else if (k?.GetValue("HardwareInformation.MemorySize") is int i32 && i32 != 0)
                        vramGb = unchecked((uint)i32) / (1024.0 * 1024.0 * 1024.0);

                    gpus.Add((name, vendor, vramGb, score));
                }
            }
        }
        catch { }

        var best = gpus.OrderByDescending(g => g.Score).ThenByDescending(g => g.VramGb).FirstOrDefault();
        return best.Name != null ? (best.Name, best.Vendor, best.VramGb) : ("GPU Vulkan", "Desconhecido", 0);
    }

    static (int W, int H, int Hz) DetectPrimaryDisplay()
    {
        try
        {
            var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettingsW(null, -1 /*ENUM_CURRENT_SETTINGS*/, ref dm))
                return ((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, Math.Max(60, (int)dm.dmDisplayFrequency));
        }
        catch { }
        return (1920, 1080, 60);
    }

    static double DetectRamGb()
    {
        try
        {
            var st = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref st))
                return st.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
        }
        catch { }
        return 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("kernel32.dll")]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
