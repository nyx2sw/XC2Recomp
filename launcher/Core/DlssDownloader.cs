using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace XC2.Core;

public sealed record DlssProgress(
    string Stage,
    double Percent,
    string Detail,
    bool IsDone = false,
    bool IsError = false
);

public sealed record DlssInstallResult(
    bool Success,
    string DllPath,
    string Version,
    string SignerSubject,
    long SizeBytes
);

/// <summary>
/// On-demand downloader and Authenticode verifier for NVIDIA's official nvngx_dlss.dll.
/// Downloads directly from NVIDIA's official public GitHub repository (NVIDIA/DLSS) only when
/// explicitly requested by the user — never redistributes NVIDIA binaries with the launcher.
/// Verifies PE structure, WinVerifyTrust Authenticode chain, and NVIDIA Corporation signer.
/// </summary>
public static class DlssDownloader
{
    public const string OfficialDownloadUrl =
        "https://raw.githubusercontent.com/NVIDIA/DLSS/main/lib/Windows_x86_64/rel/nvngx_dlss.dll";

    public const string OfficialRepoUrl = "https://github.com/NVIDIA/DLSS";

    static readonly HttpClient Http = CreateHttpClient();

    static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XC2Recomp-Launcher/1.0");
        return client;
    }

    public static string? GetInstalledVersion(string installDir)
    {
        try
        {
            string dllPath = Path.Combine(installDir, "nvngx_dlss.dll");
            if (!File.Exists(dllPath)) return null;
            var fvi = FileVersionInfo.GetVersionInfo(dllPath);
            string raw = (fvi.FileVersion ?? fvi.ProductVersion ?? "").Trim();
            if (string.IsNullOrEmpty(raw)) return "instalado";
            // Normalize "310,9,16,0" -> "310.9.16"
            var parts = raw.Split([',', '.'], StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim()).ToList();
            while (parts.Count > 3 && parts[^1] == "0") parts.RemoveAt(parts.Count - 1);
            return string.Join('.', parts);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<DlssInstallResult> DownloadOfficialAsync(
        string installDir,
        Action<DlssProgress>? onProgress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(installDir);
        string destPath = Path.Combine(installDir, "nvngx_dlss.dll");
        string tmpPath = destPath + $".dl.{Environment.ProcessId}.tmp";

        try
        {
            onProgress?.Invoke(new DlssProgress("Conectando à NVIDIA", 2,
                "Conectando ao repositório oficial github.com/NVIDIA/DLSS..."));

            using var req = new HttpRequestMessage(HttpMethod.Get, OfficialDownloadUrl);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            long total = resp.Content.Headers.ContentLength ?? 0;
            if (total > 0 && total < 5_000_000)
                throw new InvalidDataException($"Tamanho inesperado do binário NVIDIA DLSS ({total:N0} B).");

            await using (var net = await resp.Content.ReadAsStreamAsync(ct))
            await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, useAsync: true))
            {
                var buffer = new byte[131072];
                long received = 0;
                long lastTick = Environment.TickCount64;
                int read;
                while ((read = await net.ReadAsync(buffer, ct)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;
                    long now = Environment.TickCount64;
                    if (now - lastTick >= 120)
                    {
                        lastTick = now;
                        double pct = total > 0 ? 5.0 + (double)received / total * 85.0 : 45.0;
                        string detail = total > 0
                            ? $"Baixando nvngx_dlss.dll oficial… {received / 1048576.0:F1} / {total / 1048576.0:F1} MiB"
                            : $"Baixando nvngx_dlss.dll oficial… {received / 1048576.0:F1} MiB";
                        onProgress?.Invoke(new DlssProgress("Baixando NVIDIA DLSS", pct, detail));
                    }
                }
            }

            onProgress?.Invoke(new DlssProgress("Verificando assinatura NVIDIA", 93,
                "Validando assinatura digital Authenticode (NVIDIA Corporation)..."));

            var (version, signer, size) = await Task.Run(() => VerifyNvidiaDll(tmpPath), ct);

            File.Move(tmpPath, destPath, overwrite: true);
            LauncherLog.Write($"dlss: installed official nvngx_dlss.dll v{version} ({size:N0} B) signed by '{signer}'");

            var doneDetail = $"NVIDIA DLSS v{version} instalado e verificado ({size / 1048576.0:F1} MiB).";
            onProgress?.Invoke(new DlssProgress("Concluído", 100, doneDetail, IsDone: true));
            return new DlssInstallResult(true, destPath, version, signer, size);
        }
        catch
        {
            TryDelete(tmpPath);
            throw;
        }
    }

    public static DlssInstallResult InstallFromLocalFile(string sourcePath, string installDir)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("Arquivo nvngx_dlss.dll não encontrado.", sourcePath);

        var (version, signer, size) = VerifyNvidiaDll(sourcePath);

        Directory.CreateDirectory(installDir);
        string destPath = Path.Combine(installDir, "nvngx_dlss.dll");
        if (!Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
        {
            string tmpPath = destPath + $".copy.{Environment.ProcessId}.tmp";
            try
            {
                File.Copy(sourcePath, tmpPath, overwrite: true);
                File.Move(tmpPath, destPath, overwrite: true);
            }
            catch
            {
                TryDelete(tmpPath);
                throw;
            }
        }

        LauncherLog.Write($"dlss: imported local nvngx_dlss.dll v{version} ({size:N0} B) signed by '{signer}'");
        return new DlssInstallResult(true, destPath, version, signer, size);
    }

    public static (string Version, string SignerSubject, long SizeBytes) VerifyNvidiaDll(string filePath)
    {
        var fi = new FileInfo(filePath);
        if (!fi.Exists || fi.Length < 5_000_000)
            throw new InvalidDataException("O arquivo não parece ser uma biblioteca nvngx_dlss.dll válida.");

        // 1. Verify x64 PE header
        using (var fs = File.OpenRead(filePath))
        {
            Span<byte> dos = stackalloc byte[64];
            if (fs.Read(dos) < 64 || dos[0] != (byte)'M' || dos[1] != (byte)'Z')
                throw new InvalidDataException("Cabeçalho PE (MZ) inválido.");
            int peOff = BitConverter.ToInt32(dos.Slice(60, 4));
            if (peOff < 64 || peOff + 6 > fi.Length)
                throw new InvalidDataException("Offset PE inválido.");
            fs.Seek(peOff, SeekOrigin.Begin);
            Span<byte> pe = stackalloc byte[6];
            if (fs.Read(pe) < 6 || pe[0] != 'P' || pe[1] != 'E' || pe[2] != 0 || pe[3] != 0)
                throw new InvalidDataException("Assinatura PE inválida.");
            ushort machine = BitConverter.ToUInt16(pe.Slice(4, 2));
            if (machine != 0x8664)
                throw new InvalidDataException("A biblioteca nvngx_dlss.dll deve ser 64-bit (x86-64).");
        }

        // 2. Verify Windows Authenticode trust chain via WinVerifyTrust
        int trustStatus = VerifyAuthenticodeTrust(filePath);
        if (trustStatus != 0)
            throw new InvalidDataException($"Falha na validação da assinatura digital Authenticode (código 0x{trustStatus:X8}).");

        // 3. Verify signer certificate belongs to NVIDIA Corporation
        string subject;
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
#pragma warning restore SYSLIB0057
            subject = cert.Subject ?? "";
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Não foi possível ler o certificado digital do arquivo: {ex.Message}");
        }

        if (!subject.Contains("NVIDIA Corporation", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Assinatura digital não pertence à NVIDIA Corporation ({subject}).");

        // 4. Read version info
        var fvi = FileVersionInfo.GetVersionInfo(filePath);
        string rawVer = (fvi.FileVersion ?? fvi.ProductVersion ?? "").Trim();
        var parts = rawVer.Split([',', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim()).ToList();
        while (parts.Count > 3 && parts[^1] == "0") parts.RemoveAt(parts.Count - 1);
        string ver = parts.Count > 0 ? string.Join('.', parts) : "DLSS";

        return (ver, subject, fi.Length);
    }

    // ==================== Win32 WinVerifyTrust P/Invoke ====================

    static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    static int VerifyAuthenticodeTrust(string filePath)
    {
        IntPtr filePathPtr = Marshal.StringToCoTaskMemUni(filePath);
        IntPtr fileInfoPtr = IntPtr.Zero;
        try
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = filePathPtr,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero
            };
            fileInfoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2, // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE (fast offline-safe chain check)
                dwUnionChoice = 1, // WTD_CHOICE_FILE
                pFile = fileInfoPtr,
                dwStateAction = 1, // WTD_STATEACTION_VERIFY
                dwProvFlags = 0x00000010 | 0x00001000 // WTD_SAFER_FLAG | WTD_CACHE_ONLY_URL_RETRIEVAL
            };

            Guid action = WinTrustActionGenericVerifyV2;
            int status = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            data.dwStateAction = 2; // WTD_STATEACTION_CLOSE
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return status;
        }
        finally
        {
            if (fileInfoPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(fileInfoPtr);
            if (filePathPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(filePathPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { }
    }
}
