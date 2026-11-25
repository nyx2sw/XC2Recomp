using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace XC2.Core;

public sealed record ShaderCatalogProgress(
    string Stage,
    int StageIndex,
    int StageCount,
    double Percent,
    int ShadersFound,
    string Detail,
    bool IsDone = false,
    bool IsError = false
);

public sealed record ShaderCatalogResult(
    string OutputPath,
    int UniqueShaders,
    long SizeBytes,
    ulong CatalogHash,
    double ElapsedSeconds
);

/// <summary>
/// Pure managed C# read-only shader catalog builder for Xenoblade Chronicles 2 (Switch 2 Edition 3.0.1).
/// Scans romfs/bf2.arh + romfs/bf2.ard and addons/*.romfs (including Torna) strictly read-only,
/// extracts unique Switch 2 Ampere v4 NVN shaders from Monolith Soft SPCH (HCPS/SLCT/NVSD) containers,
/// and writes shader_cache/xc2_shaders.snxs (SNXS v1) atomically for native startup pre-compilation.
/// Zero external tools or Python required.
/// </summary>
public static class ShaderCatalogBuilder
{
    const uint NvnCtrlMagic = 0x98761234u;
    static readonly byte[] MagicHcps = [(byte)'H', (byte)'C', (byte)'P', (byte)'S'];
    static readonly byte[] MagicXbc1 = [(byte)'x', (byte)'b', (byte)'c', (byte)'1'];

    readonly record struct ShaderKey(
        byte Stage, byte EarlyZ, ushort Lx, ushort Ly, ushort Lz, uint Shm, ulong HashHi, ulong HashLo);

    readonly record struct ShaderEntry(
        byte Stage, byte EarlyZ, ushort Lx, ushort Ly, ushort Lz, uint Shm, byte[] Code);

    readonly record struct ArhFileEntry(string Path, ulong Offset, uint CompSize, uint ExpSize, uint Flags);

    public static Task<ShaderCatalogResult> BuildAsync(
        string dataDir,
        string outPath,
        Action<ShaderCatalogProgress>? onProgress,
        CancellationToken ct)
        => Task.Run(() => Build(dataDir, outPath, onProgress, ct), ct);

    public static ShaderCatalogResult Build(
        string dataDir,
        string outPath,
        Action<ShaderCatalogProgress>? onProgress,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string romfsDir = Path.Combine(dataDir, "romfs");
        string arhPath = Path.Combine(romfsDir, "bf2.arh");
        string ardPath = Path.Combine(romfsDir, "bf2.ard");
        if (!File.Exists(arhPath) || !File.Exists(ardPath))
            throw new FileNotFoundException("Arquivos base do jogo (bf2.arh / bf2.ard) não encontrados.", romfsDir);

        var seen = new HashSet<ShaderKey>(16384);
        var ordered = new List<ShaderEntry>(12000);
        long lastTick = Environment.TickCount64;

        void Emit(string stage, int idx, double pct, string detail, bool force = false)
        {
            long now = Environment.TickCount64;
            if (!force && now - lastTick < 120) return;
            lastTick = now;
            onProgress?.Invoke(new ShaderCatalogProgress(stage, idx, 4, Math.Clamp(pct, 0, 100), ordered.Count, detail));
        }

        int AddShader(byte st, byte ez, ushort lx, ushort ly, ushort lz, uint shm, ReadOnlySpan<byte> codeSpan)
        {
            if (st != 5)
            {
                if (codeSpan.Length < 0x80) return 0;
                uint w0 = BinaryPrimitives.ReadUInt32LittleEndian(codeSpan);
                if (((w0 >> 5) & 0x1Fu) != 4u)
                {
                    if (codeSpan.Length > 0xB0 && ((BinaryPrimitives.ReadUInt32LittleEndian(codeSpan.Slice(0x30, 4)) >> 5) & 0x1Fu) == 4u)
                        codeSpan = codeSpan.Slice(0x30);
                    else
                        return 0;
                }
            }

            Span<byte> sha = stackalloc byte[32];
            SHA256.HashData(codeSpan, sha);
            ulong hHi = BinaryPrimitives.ReadUInt64LittleEndian(sha.Slice(0, 8));
            ulong hLo = BinaryPrimitives.ReadUInt64LittleEndian(sha.Slice(8, 8));
            var key = new ShaderKey(st, ez, lx, ly, lz, shm, hHi, hLo);
            if (!seen.Add(key)) return 0;

            ordered.Add(new ShaderEntry(st, ez, lx, ly, lz, shm, codeSpan.ToArray()));
            return 1;
        }

        Emit("Lendo índice bf2.arh", 1, 1, "Carregando tabela de arquivos do jogo base...", force: true);
        byte[] arhBytes = File.ReadAllBytes(arhPath);
        var entries = EnumerateArhEntries(arhBytes);

        var wishps = new List<ArhFileEntry>();
        var wismdas = new List<ArhFileEntry>();
        var wismts = new List<ArhFileEntry>();
        var wimdos = new List<ArhFileEntry>();

        foreach (var e in entries)
        {
            if (e.Path.EndsWith(".wishp", StringComparison.OrdinalIgnoreCase)) wishps.Add(e);
            else if (e.Path.EndsWith(".wismda", StringComparison.OrdinalIgnoreCase)) wismdas.Add(e);
            else if (e.Path.EndsWith(".wismt", StringComparison.OrdinalIgnoreCase)) wismts.Add(e);
            else if (e.Path.EndsWith(".wimdo", StringComparison.OrdinalIgnoreCase)) wimdos.Add(e);
        }
        wishps.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        wismdas.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        wismts.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        wimdos.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        using (var ardStream = new FileStream(ardPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 131072))
        {
            // Stage 1: Global .wishp
            Emit("Shaders globais (.wishp)", 1, 3, $"Analisando {wishps.Count} pacotes globais...", force: true);
            for (int i = 0; i < wishps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var raw = ReadArhPayload(ardStream, 0, wishps[i]);
                if (raw != null) ExtractSpchShaders(raw, AddShader);
                Emit("Shaders globais (.wishp)", 1, 3 + 12.0 * (i + 1) / Math.Max(1, wishps.Count),
                    $"Globais ({i + 1}/{wishps.Count}) · {ordered.Count:N0} shaders");
            }

            // Stage 2: Map .wismda
            Emit("Shaders de mapas (.wismda)", 2, 16, $"Analisando {wismdas.Count} áreas de Alrest...", force: true);
            for (int i = 0; i < wismdas.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var raw = ReadArhPayload(ardStream, 0, wismdas[i]);
                if (raw != null) ExtractFromXbcStream(raw, AddShader);
                Emit("Shaders de mapas (.wismda)", 2, 16 + 34.0 * (i + 1) / Math.Max(1, wismdas.Count),
                    $"Mapas ({i + 1}/{wismdas.Count}) · {ordered.Count:N0} shaders");
            }

            // Stage 3: Models .wismt + .wimdo
            int totalModels = wismts.Count + wimdos.Count;
            Emit("Shaders de modelos (.wismt/.wimdo)", 3, 50, $"Analisando {totalModels:N0} modelos...", force: true);
            for (int i = 0; i < wismts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var raw = ReadArhPayload(ardStream, 0, wismts[i]);
                if (raw != null) ExtractFromXbcStream(raw, AddShader);
                Emit("Shaders de modelos (.wismt/.wimdo)", 3, 50 + 28.0 * (i + 1) / Math.Max(1, totalModels),
                    $"Modelos ({i + 1}/{totalModels}) · {ordered.Count:N0} shaders");
            }
            for (int i = 0; i < wimdos.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var raw = ReadArhPayload(ardStream, 0, wimdos[i]);
                if (raw != null && raw.AsSpan().IndexOf(MagicHcps) >= 0)
                    ExtractSpchShaders(raw, AddShader);
                Emit("Shaders de modelos (.wismt/.wimdo)", 3, 50 + 28.0 * (wismts.Count + i + 1) / Math.Max(1, totalModels),
                    $"Modelos ({wismts.Count + i + 1}/{totalModels}) · {ordered.Count:N0} shaders");
            }
        }

        // Stage 4: Expansions / Torna (.romfs)
        Emit("Expansões e Torna (.romfs)", 4, 80, "Verificando expansões instaladas...", force: true);
        string addonsDir = Path.Combine(dataDir, "addons");
        if (Directory.Exists(addonsDir))
        {
            var romfsFiles = Directory.EnumerateFiles(addonsDir, "*.romfs")
                .Where(f => new FileInfo(f).Length > 1_000_000)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            for (int i = 0; i < romfsFiles.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                ScanRomfsImage(romfsFiles[i], AddShader, ct);
                Emit("Expansões e Torna (.romfs)", 4, 80 + 14.0 * (i + 1) / Math.Max(1, romfsFiles.Length),
                    $"Expansão {i + 1}/{romfsFiles.Length} ({Path.GetFileName(romfsFiles[i])}) · {ordered.Count:N0} shaders");
            }
        }

        ct.ThrowIfCancellationRequested();
        Emit("Gravando catálogo SNXS", 4, 96, $"Salvando {ordered.Count:N0} shaders únicos...", force: true);

        // Serialize SNXS v1 atomically
        int bodySize = 0;
        for (int i = 0; i < ordered.Count; i++)
            bodySize += 16 + ordered[i].Code.Length;

        var body = new byte[bodySize];
        int p = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            var e = ordered[i];
            body[p + 0] = e.Stage;
            body[p + 1] = e.EarlyZ;
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p + 2, 2), e.Lx);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p + 4, 2), e.Ly);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p + 6, 2), e.Lz);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p + 8, 4), e.Shm);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p + 12, 4), (uint)e.Code.Length);
            Buffer.BlockCopy(e.Code, 0, body, p + 16, e.Code.Length);
            p += 16 + e.Code.Length;
        }

        Span<byte> bodySha = stackalloc byte[32];
        SHA256.HashData(body, bodySha);
        ulong catHash = BinaryPrimitives.ReadUInt64LittleEndian(bodySha.Slice(0, 8));

        var header = new byte[24];
        header[0] = (byte)'S'; header[1] = (byte)'N'; header[2] = (byte)'X'; header[3] = (byte)'S';
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), (uint)ordered.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), 0u);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16, 8), catHash);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        string tmp = outPath + $".{Environment.ProcessId}.tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 131072))
            {
                fs.Write(header);
                fs.Write(body);
            }
            File.Move(tmp, outPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }

        // Remove stale stamp so the next launch pre-compiles against the newly generated catalog
        try
        {
            string stamp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, "vk_precompile.stamp");
            if (File.Exists(stamp)) File.Delete(stamp);
        }
        catch { }

        sw.Stop();
        long totalBytes = header.Length + body.LongLength;
        string doneMsg = $"Catálogo pronto: {ordered.Count:N0} shaders ({totalBytes / 1048576.0:F1} MiB) em {sw.Elapsed.TotalSeconds:F1}s.";
        LauncherLog.Write($"shaders: {doneMsg} hash=0x{catHash:X16}");
        onProgress?.Invoke(new ShaderCatalogProgress("Concluído", 4, 4, 100, ordered.Count, doneMsg, IsDone: true));
        return new ShaderCatalogResult(outPath, ordered.Count, totalBytes, catHash, sw.Elapsed.TotalSeconds);
    }

    // ==================== SPCH (HCPS / SLCT / NVSD) Parser ====================

    delegate int AddShaderFn(byte stage, byte earlyZ, ushort lx, ushort ly, ushort lz, uint shm, ReadOnlySpan<byte> code);

    static void ExtractSpchShaders(ReadOnlySpan<byte> buf, AddShaderFn add)
    {
        int blen = buf.Length;
        int pos = 0;

        while (pos < blen)
        {
            int relFind = buf.Slice(pos).IndexOf(MagicHcps);
            if (relFind < 0) break;
            int b = pos + relFind;
            if (b + 0x38 > blen) break;

            uint slctTblOff = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(b + 8, 4));
            uint slctCnt = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(b + 12, 4));
            uint slctBase = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(b + 0x18, 4));
            uint xvbOff = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(b + 0x20, 4));
            uint xvbSz = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(b + 0x24, 4));
            uint nvnOff = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(b + 0x28, 4));
            uint nvnSz = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(b + 0x2C, 4));

            if (slctCnt == 0 || slctCnt > 20000 || xvbSz == 0 || nvnSz == 0 ||
                (long)b + xvbOff + xvbSz > blen ||
                (long)b + nvnOff + nvnSz > blen ||
                (long)b + slctTblOff + (long)slctCnt * 8 > blen)
            {
                pos = b + 4;
                continue;
            }

            for (int si = 0; si < slctCnt; si++)
            {
                uint rel = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(b + (int)slctTblOff + si * 8, 4));
                long soL = (long)b + slctBase + rel;
                if (soL < 0 || soL + 0x40 > blen) continue;
                int so = (int)soL;
                if (buf[so] != 'S' || buf[so + 1] != 'L' || buf[so + 2] != 'C' || buf[so + 3] != 'T')
                    continue;

                uint progCnt = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(so + 0x10, 4));
                uint progOff = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(so + 0x14, 4));
                uint nvnRel = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(so + 0x28, 4));
                uint xvbRel = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(so + 0x30, 4));

                if (progCnt > 50000 || (long)so + progOff + (long)progCnt * 8 > blen)
                    continue;

                long curXvb = (long)b + xvbOff + xvbRel;
                long curNvn = (long)b + nvnOff + nvnRel;

                for (int pi = 0; pi < progCnt; pi++)
                {
                    uint poff = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(so + (int)progOff + pi * 8, 4));
                    long pL = (long)so + poff;
                    if (pL < 0 || pL + 0x78 > blen) continue;
                    int p = (int)pL;

                    uint nvsdOff = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(p + 0x18, 4));
                    uint nstages = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(p + 0x1C, 4));
                    uint progXvbSz = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(p + 0x68, 4));
                    uint progNvnSz = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(p + 0x6C, 4));

                    long nvsdL = (long)p + nvsdOff;
                    if (nstages > 0 && nstages <= 6 && nvsdL >= 8 && nvsdL + nstages * 20 <= blen)
                    {
                        int nvsd = (int)nvsdL;
                        if (buf[nvsd - 8] == 'N' && buf[nvsd - 7] == 'V' && buf[nvsd - 6] == 'S' && buf[nvsd - 5] == 'D')
                        {
                            for (int s = 0; s < nstages; s++)
                            {
                                int sOff = nvsd + s * 20;
                                uint doff = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(sOff + 4, 4));
                                uint dsz = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(sOff + 8, 4));
                                uint coff = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(sOff + 12, 4));

                                long cabsL = curNvn + coff;
                                long dabsL = curXvb + doff;
                                if (cabsL >= 0 && dabsL >= 0 && cabsL + 0x68 <= blen && dabsL + dsz <= blen)
                                {
                                    int cabs = (int)cabsL;
                                    int dabs = (int)dabsL;
                                    if (BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(cabs, 4)) == NvnCtrlMagic)
                                    {
                                        uint codeOff = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(cabs + 0x34, 4));
                                        uint ctrlSt = (BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(cabs + 0x54, 4)) >> 16) & 0xFFu;
                                        if (ctrlSt >= 6 || codeOff >= dsz) continue;

                                        byte ez = 0;
                                        ushort lx = 0, ly = 0, lz = 0;
                                        uint shm = 0;
                                        if (ctrlSt == 1)
                                        {
                                            ez = ((BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(cabs + 0x58, 4)) >> 24) & 0xFFu) != 0
                                                ? (byte)1 : (byte)0;
                                        }
                                        else if (ctrlSt == 5)
                                        {
                                            lx = (ushort)Math.Clamp(BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(cabs + 0x58, 4)), 1u, 1024u);
                                            ly = (ushort)Math.Clamp(BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(cabs + 0x5C, 4)), 1u, 1024u);
                                            lz = (ushort)Math.Clamp(BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(cabs + 0x60, 4)), 1u, 1024u);
                                            shm = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(cabs + 0x64, 4)) & 0x3FFFFu;
                                            if (shm > 49152u) shm = 0;
                                        }

                                        int codeLen = (int)(dsz - codeOff);
                                        if (codeLen >= 0x80 && codeLen <= 0x10000)
                                            add((byte)ctrlSt, ez, lx, ly, lz, shm, buf.Slice(dabs + (int)codeOff, codeLen));
                                    }
                                }
                            }
                        }
                    }

                    curXvb += progXvbSz;
                    curNvn += progNvnSz;
                }
            }

            pos = (int)Math.Min((long)blen, (long)b + xvbOff + xvbSz);
        }
    }

    static void ExtractFromXbcStream(byte[] raw, AddShaderFn add)
    {
        var span = raw.AsSpan();
        if (span.IndexOf(MagicHcps) >= 0)
            ExtractSpchShaders(span, add);

        int pos = 0;
        int rlen = raw.Length;
        while (pos + 48 <= rlen)
        {
            int rel = span.Slice(pos).IndexOf(MagicXbc1);
            if (rel < 0) break;
            int idx = pos + rel;
            if (idx + 48 > rlen) break;

            uint ver = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(idx + 4, 4));
            uint usz = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(idx + 8, 4));
            uint csz = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(idx + 12, 4));

            if (ver == 1 && csz > 0 && csz < 64_000_000 && idx + 48L + csz <= rlen)
            {
                try
                {
                    var dec = DecompressZlib(raw, idx + 48, (int)csz, (int)Math.Min(usz, 64_000_000));
                    if (dec.AsSpan().IndexOf(MagicHcps) >= 0)
                        ExtractSpchShaders(dec, add);
                }
                catch { }
                pos = idx + 48 + (int)csz;
            }
            else
            {
                pos = idx + 4;
            }
        }
    }

    // ==================== ARH1 / Switch RomFS Read-Only Scanners ====================

    static List<ArhFileEntry> EnumerateArhEntries(byte[] rawArh)
    {
        var result = new List<ArhFileEntry>(16384);
        if (rawArh.Length < 40 || rawArh[0] != 'a' || rawArh[1] != 'r' || rawArh[2] != 'h' || rawArh[3] != '1')
            return result;

        int nodeCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(rawArh.AsSpan(8, 4));
        int strOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(rawArh.AsSpan(12, 4));
        int strLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(rawArh.AsSpan(16, 4));
        int nodeOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(rawArh.AsSpan(20, 4));
        int nodeLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(rawArh.AsSpan(24, 4));
        int fileTableOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(rawArh.AsSpan(28, 4));
        uint fileCount = BinaryPrimitives.ReadUInt32LittleEndian(rawArh.AsSpan(32, 4));
        uint keyVal = BinaryPrimitives.ReadUInt32LittleEndian(rawArh.AsSpan(36, 4)) ^ 0xF3F35353u;

        Span<byte> key = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(key, keyVal);
        byte[] strings = DecodeXor(rawArh, strOff, strLen, key);
        byte[] nodes = DecodeXor(rawArh, nodeOff, nodeLen, key);

        var chars = new byte[2048];
        for (int i = 0; i < nodeCount; i++)
        {
            int nxt = BinaryPrimitives.ReadInt32LittleEndian(nodes.AsSpan(i * 8, 4));
            int prev = BinaryPrimitives.ReadInt32LittleEndian(nodes.AsSpan(i * 8 + 4, 4));
            if (nxt >= 0 || prev < 0 || prev >= nodeCount) continue;

            int charCount = 0;
            int cur = i;
            bool ok = true;
            for (int step = 0; step < 2048; step++)
            {
                int parent = BinaryPrimitives.ReadInt32LittleEndian(nodes.AsSpan(cur * 8 + 4, 4));
                if (parent < 0) break;
                if (parent >= nodeCount) { ok = false; break; }
                int b = BinaryPrimitives.ReadInt32LittleEndian(nodes.AsSpan(parent * 8, 4));
                int c = b ^ cur;
                if (c <= 0 || c >= 128) { ok = false; break; }
                chars[charCount++] = (byte)c;
                cur = parent;
            }
            int strStart = -nxt;
            if (!ok || charCount == 0 || strStart <= 0 || strStart >= strings.Length) continue;

            int end = Array.IndexOf(strings, (byte)0, strStart);
            if (end < 0 || end + 5 > strings.Length) continue;

            uint fid = BinaryPrimitives.ReadUInt32LittleEndian(strings.AsSpan(end + 1, 4));
            if (fid >= fileCount) continue;

            int tailLen = end - strStart;
            var nameBuf = new byte[charCount + tailLen];
            for (int k = 0; k < charCount; k++)
                nameBuf[k] = chars[charCount - 1 - k];
            Buffer.BlockCopy(strings, strStart, nameBuf, charCount, tailLen);
            string path = Encoding.ASCII.GetString(nameBuf);

            int metaOff = fileTableOff + (int)fid * 24;
            if (metaOff + 24 > rawArh.Length) continue;
            ulong ardOff = BinaryPrimitives.ReadUInt64LittleEndian(rawArh.AsSpan(metaOff, 8));
            uint compSz = BinaryPrimitives.ReadUInt32LittleEndian(rawArh.AsSpan(metaOff + 8, 4));
            uint expSz = BinaryPrimitives.ReadUInt32LittleEndian(rawArh.AsSpan(metaOff + 12, 4));
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(rawArh.AsSpan(metaOff + 16, 4));

            result.Add(new ArhFileEntry(path, ardOff, compSz, expSz, flags));
        }

        return result;
    }

    static byte[] DecodeXor(byte[] src, int off, int len, ReadOnlySpan<byte> key)
    {
        var dst = new byte[len];
        Buffer.BlockCopy(src, off, dst, 0, len);
        int aligned = (len / 4) * 4;
        for (int i = 0; i < aligned; i++)
            dst[i] ^= key[i & 3];
        return dst;
    }

    static byte[]? ReadArhPayload(FileStream ardStream, long baseOffset, ArhFileEntry entry)
    {
        bool compressed = (entry.Flags & 2u) != 0;
        long toReadL = (long)entry.CompSize + (compressed ? 48 : 0);
        if (toReadL <= 0 || toReadL > 1_073_741_824L) return null;
        int toRead = (int)toReadL;

        var raw = new byte[toRead];
        ardStream.Seek(baseOffset + (long)entry.Offset, SeekOrigin.Begin);
        int got = ReadExact(ardStream, raw, 0, toRead);
        if (got < (compressed ? 48 : (int)entry.CompSize)) return null;

        if (!compressed)
            return raw;

        if (raw[0] != 'x' || raw[1] != 'b' || raw[2] != 'c' || raw[3] != '1')
            return null;
        int xExp = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8, 4));
        int xComp = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12, 4));
        if (xComp <= 0 || 48 + xComp > raw.Length) return null;
        return DecompressZlib(raw, 48, xComp, xExp > 0 ? xExp : (int)entry.ExpSize);
    }

    static void ScanRomfsImage(string romfsPath, AddShaderFn add, CancellationToken ct)
    {
        using var fs = new FileStream(romfsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 131072);
        long size = fs.Length;
        if (size < 1024) return;

        Span<byte> hdr = stackalloc byte[0x50];
        if (ReadExact(fs, hdr) < 0x50) return;

        ulong hdrSz = BinaryPrimitives.ReadUInt64LittleEndian(hdr.Slice(0, 8));
        ulong fmOff = BinaryPrimitives.ReadUInt64LittleEndian(hdr.Slice(56, 8));
        ulong fmSz = BinaryPrimitives.ReadUInt64LittleEndian(hdr.Slice(64, 8));
        ulong dataOff = BinaryPrimitives.ReadUInt64LittleEndian(hdr.Slice(72, 8));
        if (hdrSz != 0x50 || fmOff + fmSz > (ulong)size || dataOff > (ulong)size || fmSz > 64_000_000)
            return;

        var fmeta = new byte[(int)fmSz];
        fs.Seek((long)fmOff, SeekOrigin.Begin);
        if (ReadExact(fs, fmeta, 0, fmeta.Length) < fmeta.Length) return;

        var arhs = new Dictionary<string, (long Off, int Size)>(StringComparer.OrdinalIgnoreCase);
        var ards = new Dictionary<string, (long Off, long Size)>(StringComparer.OrdinalIgnoreCase);

        int off = 0;
        while (off + 0x20 <= fmeta.Length)
        {
            ct.ThrowIfCancellationRequested();
            ulong foff = BinaryPrimitives.ReadUInt64LittleEndian(fmeta.AsSpan(off + 8, 8));
            ulong fsz = BinaryPrimitives.ReadUInt64LittleEndian(fmeta.AsSpan(off + 16, 8));
            int nlen = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmeta.AsSpan(off + 28, 4));
            if (nlen < 0 || nlen > 256 || off + 0x20 + nlen > fmeta.Length) break;

            string name = Encoding.UTF8.GetString(fmeta, off + 0x20, nlen);
            if (name.EndsWith(".arh", StringComparison.OrdinalIgnoreCase) && fsz > 0 && fsz < 64_000_000)
            {
                arhs[name[..^4]] = ((long)(dataOff + foff), (int)fsz);
            }
            else if (name.EndsWith(".ard", StringComparison.OrdinalIgnoreCase) && fsz > 0)
            {
                ards[name[..^4]] = ((long)(dataOff + foff), (long)fsz);
            }
            else if (fsz > 0 && fsz < 1_073_741_824UL && IsShaderContainer(name))
            {
                var raw = new byte[(int)fsz];
                fs.Seek((long)(dataOff + foff), SeekOrigin.Begin);
                if (ReadExact(fs, raw, 0, raw.Length) == raw.Length)
                {
                    if (name.EndsWith(".wishp", StringComparison.OrdinalIgnoreCase))
                        ExtractSpchShaders(raw, add);
                    else
                        ExtractFromXbcStream(raw, add);
                }
            }

            off += (0x20 + nlen + 3) & ~3;
        }

        foreach (var (stem, (arhOff, arhSz)) in arhs)
        {
            if (!ards.TryGetValue(stem, out var ardLoc)) continue;
            var arhBytes = new byte[arhSz];
            fs.Seek(arhOff, SeekOrigin.Begin);
            if (ReadExact(fs, arhBytes, 0, arhSz) < arhSz) continue;

            var subEntries = EnumerateArhEntries(arhBytes);
            foreach (var e in subEntries)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsShaderContainer(e.Path)) continue;
                var raw = ReadArhPayload(fs, ardLoc.Off, e);
                if (raw == null) continue;
                if (e.Path.EndsWith(".wishp", StringComparison.OrdinalIgnoreCase))
                    ExtractSpchShaders(raw, add);
                else
                    ExtractFromXbcStream(raw, add);
            }
        }
    }

    static bool IsShaderContainer(string path) =>
        path.EndsWith(".wishp", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".wismt", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".wismda", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".wimdo", StringComparison.OrdinalIgnoreCase);

    static byte[] DecompressZlib(byte[] src, int offset, int count, int expectedSize)
    {
        using var srcMs = new MemoryStream(src, offset, count, writable: false);
        using var zlib = new ZLibStream(srcMs, CompressionMode.Decompress);
        using var dstMs = new MemoryStream(expectedSize > 0 ? expectedSize : count * 2);
        zlib.CopyTo(dstMs);
        return dstMs.ToArray();
    }

    static int ReadExact(Stream s, byte[] buf, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buf, offset + total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    static int ReadExact(Stream s, Span<byte> buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = s.Read(buf.Slice(total));
            if (n <= 0) break;
            total += n;
        }
        return total;
    }
}
