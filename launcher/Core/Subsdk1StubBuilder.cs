using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace XC2.Core;

public sealed record Subsdk1StubResult(
    string OutputPath,
    int SizeBytes,
    int ExportedSymbolCount,
    string BuildIdHex,
    string Sha256Hex
);

/// <summary>
/// Clean-room minimal NSO0 generator for exefs/subsdk1 (dlss.nss).
/// Reads ONLY the 32-byte build_id and exported symbol names from exefs_host_bypass/subsdk1,
/// and emits a tiny (16 KiB) self-contained NSO0 with:
///   - Valid nnSdk 21.4.4 MOD0 + NT_GNU_BUILD_ID headers
///   - Valid ELF64 .dynsym, .dynstr, SysV .hash, and .gnu.hash tables
///   - Distinct 8-byte AArch64 entry stubs in .text (0x0080..) and reserved .text padding
///     (0x0200..0x0ffc) used by ScarletNX's Ounce SDK for dynamic NVN2 thunk dispatch.
/// Byte-exact managed C# equivalent of work/tools/build_subsdk1_stub.py.
/// </summary>
public static class Subsdk1StubBuilder
{
    const uint Arm64Ret = 0xD65F03C0u;

    public static Subsdk1StubResult BuildFromBypass(string origSubsdk1Path, string outStubPath)
    {
        if (!File.Exists(origSubsdk1Path))
            throw new FileNotFoundException("Original subsdk1 não encontrado em exefs_host_bypass/subsdk1.", origSubsdk1Path);

        byte[] raw = File.ReadAllBytes(origSubsdk1Path);
        var (buildId, exports) = ReadNsoBuildIdAndExports(raw, origSubsdk1Path);
        byte[] nsoBytes = BuildStubBytes(buildId, exports);

        Directory.CreateDirectory(Path.GetDirectoryName(outStubPath)!);
        string tmp = outStubPath + $".tmp.{Environment.ProcessId}";
        try
        {
            File.WriteAllBytes(tmp, nsoBytes);
            File.Move(tmp, outStubPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }

        // Round-trip sanity check on our generated stub
        var (_, verifyExports) = ReadNsoBuildIdAndExports(nsoBytes, outStubPath);
        if (verifyExports.Count != exports.Count)
            throw new InvalidDataException($"Falha na verificação do stub subsdk1: esperado {exports.Count} símbolos, lido {verifyExports.Count}.");

        string shaHex = Convert.ToHexString(SHA256.HashData(nsoBytes)).ToLowerInvariant();
        string bidHex = Convert.ToHexString(buildId.AsSpan(0, 20)).ToLowerInvariant();
        return new Subsdk1StubResult(outStubPath, nsoBytes.Length, exports.Count, bidHex, shaHex);
    }

    public static byte[] BuildStubBytes(byte[] buildId, IReadOnlyList<string> exports)
    {
        if (buildId.Length != 32)
            throw new ArgumentException("buildId deve ter 32 bytes.", nameof(buildId));
        if (exports.Count == 0)
            throw new InvalidDataException("Nenhum símbolo exportado encontrado em subsdk1.");
        if (0x0080 + exports.Count * 8 > 0x0200)
            throw new InvalidDataException($"Número de símbolos ({exports.Count}) excede a área reservada de stubs antes de 0x0200.");

        const int textOff = 0x0000, textSize = 0x1000;
        const int roOff = 0x1000, roSize = 0x2000;
        const int dataOff = 0x3000, dataSize = 0x1000;
        const int bssOff = 0x4000, bssSize = 0x1000;

        byte[] text = new byte[textSize];
        byte[] ro = new byte[roSize];
        byte[] data = new byte[dataSize];

        // ---- .text layout ----
        const uint mod0Vaddr = roOff + 0x18;
        BinaryPrimitives.WriteUInt32LittleEndian(text.AsSpan(0x0000, 4), 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(text.AsSpan(0x0004, 4), mod0Vaddr);
        BinaryPrimitives.WriteUInt32LittleEndian(text.AsSpan(0x0040, 4), Arm64Ret); // _init
        BinaryPrimitives.WriteUInt32LittleEndian(text.AsSpan(0x0048, 4), Arm64Ret); // _fini

        const int ngxStubStrVaddr = roOff + 0x0080;
        byte[] ngxStubBytes = Encoding.ASCII.GetBytes("NVSDK_NGX_HostBridge_Inactive\0");
        Buffer.BlockCopy(ngxStubBytes, 0, ro, 0x0080, ngxStubBytes.Length);

        var symVaddrs = new Dictionary<string, uint>(exports.Count, StringComparer.Ordinal);
        for (int i = 0; i < exports.Count; i++)
        {
            string sym = exports[i];
            int fnVaddr = 0x0080 + i * 8;
            symVaddrs[sym] = (uint)fnVaddr;

            uint insn0;
            if (sym == "_Z23dlssdetailUseMiddleWarev")
            {
                insn0 = 0x52800000u; // MOV W0, #0
            }
            else if (sym == "GetNGXResultAsString")
            {
                int delta = ngxStubStrVaddr - fnVaddr;
                uint immlo = (uint)(delta & 0x3);
                uint immhi = (uint)((delta >> 2) & 0x7FFFF);
                insn0 = 0x10000000u | (immlo << 29) | (immhi << 5);
            }
            else
            {
                insn0 = 0x52975A00u; // MOVZ W0, #0xBAD0, LSL #16 (NVSDK_NGX_Result_Fail)
            }

            BinaryPrimitives.WriteUInt32LittleEndian(text.AsSpan(fnVaddr, 4), insn0);
            BinaryPrimitives.WriteUInt32LittleEndian(text.AsSpan(fnVaddr + 4, 4), Arm64Ret);
        }

        // ---- .rodata layout ----
        BinaryPrimitives.WriteUInt32LittleEndian(ro.AsSpan(0x0000, 4), 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(ro.AsSpan(0x0004, 4), 0x18u);
        BinaryPrimitives.WriteUInt32LittleEndian(ro.AsSpan(0x0008, 4), 0x4Cu);
        BinaryPrimitives.WriteUInt32LittleEndian(ro.AsSpan(0x000C, 4), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(ro.AsSpan(0x0010, 4), 4u);
        Encoding.ASCII.GetBytes("dlss").CopyTo(ro, 0x0014);

        // NintendoSDK version 21.4.4 at ro+0x4C
        BinaryPrimitives.WriteUInt32LittleEndian(ro.AsSpan(0x004C, 4), 21u);
        BinaryPrimitives.WriteUInt32LittleEndian(ro.AsSpan(0x0050, 4), 4u);
        BinaryPrimitives.WriteUInt32LittleEndian(ro.AsSpan(0x0054, 4), 4u);

        // NT_GNU_BUILD_ID note (36 bytes) at ro+0x60
        const int noteRoOff = 0x0060;
        const int noteVaddr = roOff + noteRoOff;
        BinaryPrimitives.WriteUInt32LittleEndian(ro.AsSpan(noteRoOff, 4), 4u);
        BinaryPrimitives.WriteUInt32LittleEndian(ro.AsSpan(noteRoOff + 4, 4), 20u);
        BinaryPrimitives.WriteUInt32LittleEndian(ro.AsSpan(noteRoOff + 8, 4), 3u);
        Encoding.ASCII.GetBytes("GNU\0").CopyTo(ro, noteRoOff + 12);
        Buffer.BlockCopy(buildId, 0, ro, noteRoOff + 16, 20);

        // Build .dynstr
        using var dynstrMs = new MemoryStream();
        dynstrMs.WriteByte(0);
        uint sonameOff = (uint)dynstrMs.Position;
        dynstrMs.Write(Encoding.ASCII.GetBytes("dlss.nss\0"));
        var symNameOffs = new Dictionary<string, uint>(exports.Count, StringComparer.Ordinal);
        foreach (var sym in exports)
        {
            symNameOffs[sym] = (uint)dynstrMs.Position;
            dynstrMs.Write(Encoding.ASCII.GetBytes(sym));
            dynstrMs.WriteByte(0);
        }
        byte[] dynstr = dynstrMs.ToArray();

        // Stable sort by GNU hash bucket (matches Python sorted())
        const uint nbuckets = 17;
        const uint symoffset = 1;
        var exportsSorted = exports.OrderBy(s => ElfGnuHash(Encoding.ASCII.GetBytes(s)) % nbuckets).ToList();

        // Build .dynsym (24 bytes per Elf64_Sym)
        byte[] dynsym = new byte[(1 + exportsSorted.Count) * 24];
        for (int i = 0; i < exportsSorted.Count; i++)
        {
            string sym = exportsSorted[i];
            int o = (i + 1) * 24;
            BinaryPrimitives.WriteUInt32LittleEndian(dynsym.AsSpan(o, 4), symNameOffs[sym]);
            dynsym[o + 4] = (1 << 4) | 2; // STB_GLOBAL | STT_FUNC
            dynsym[o + 5] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(dynsym.AsSpan(o + 6, 2), 1); // .text
            BinaryPrimitives.WriteUInt64LittleEndian(dynsym.AsSpan(o + 8, 8), symVaddrs[sym]);
            BinaryPrimitives.WriteUInt64LittleEndian(dynsym.AsSpan(o + 16, 8), 8UL);
        }

        uint nsyms = (uint)(1 + exportsSorted.Count);

        // Build SysV .hash
        const uint sysvNbuckets = 17;
        uint[] sysvBuckets = new uint[sysvNbuckets];
        uint[] sysvChains = new uint[nsyms];
        for (int i = 0; i < exportsSorted.Count; i++)
        {
            uint idx = (uint)(i + 1);
            uint b = ElfSysvHash(Encoding.ASCII.GetBytes(exportsSorted[i])) % sysvNbuckets;
            if (sysvBuckets[b] == 0)
            {
                sysvBuckets[b] = idx;
            }
            else
            {
                uint cur = sysvBuckets[b];
                while (sysvChains[cur] != 0) cur = sysvChains[cur];
                sysvChains[cur] = idx;
            }
        }
        byte[] sysvHash = new byte[(2 + sysvBuckets.Length + sysvChains.Length) * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(sysvHash.AsSpan(0, 4), sysvNbuckets);
        BinaryPrimitives.WriteUInt32LittleEndian(sysvHash.AsSpan(4, 4), nsyms);
        int shPos = 8;
        foreach (uint b in sysvBuckets) { BinaryPrimitives.WriteUInt32LittleEndian(sysvHash.AsSpan(shPos, 4), b); shPos += 4; }
        foreach (uint c in sysvChains) { BinaryPrimitives.WriteUInt32LittleEndian(sysvHash.AsSpan(shPos, 4), c); shPos += 4; }

        // Build .gnu.hash
        const uint bloomSize = 1;
        const uint bloomShift = 6;
        ulong bloomWord = 0;
        uint[] gnuBuckets = new uint[nbuckets];
        uint[] gnuChains = new uint[exportsSorted.Count];
        for (int pos = 0; pos < exportsSorted.Count; pos++)
        {
            uint symIdx = symoffset + (uint)pos;
            uint h = ElfGnuHash(Encoding.ASCII.GetBytes(exportsSorted[pos]));
            uint b = h % nbuckets;
            bloomWord |= (1UL << (int)(h % 64)) | (1UL << (int)((h >> (int)bloomShift) % 64));
            if (gnuBuckets[b] == 0) gnuBuckets[b] = symIdx;
            bool isLast = (pos + 1 == exportsSorted.Count) ||
                          (ElfGnuHash(Encoding.ASCII.GetBytes(exportsSorted[pos + 1])) % nbuckets != b);
            gnuChains[pos] = isLast ? (h | 1u) : (h & ~1u);
        }
        byte[] gnuHash = new byte[16 + 8 + (gnuBuckets.Length + gnuChains.Length) * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(gnuHash.AsSpan(0, 4), nbuckets);
        BinaryPrimitives.WriteUInt32LittleEndian(gnuHash.AsSpan(4, 4), symoffset);
        BinaryPrimitives.WriteUInt32LittleEndian(gnuHash.AsSpan(8, 4), bloomSize);
        BinaryPrimitives.WriteUInt32LittleEndian(gnuHash.AsSpan(12, 4), bloomShift);
        BinaryPrimitives.WriteUInt64LittleEndian(gnuHash.AsSpan(16, 8), bloomWord);
        int ghPos = 24;
        foreach (uint b in gnuBuckets) { BinaryPrimitives.WriteUInt32LittleEndian(gnuHash.AsSpan(ghPos, 4), b); ghPos += 4; }
        foreach (uint c in gnuChains) { BinaryPrimitives.WriteUInt32LittleEndian(gnuHash.AsSpan(ghPos, 4), c); ghPos += 4; }

        // Place tables in .rodata starting at 0x0100
        int curRo = 0x0100;
        int dynsymVaddr = roOff + curRo;
        Buffer.BlockCopy(dynsym, 0, ro, curRo, dynsym.Length);
        curRo = AlignUp(curRo + dynsym.Length, 8);

        int dynstrVaddr = roOff + curRo;
        Buffer.BlockCopy(dynstr, 0, ro, curRo, dynstr.Length);
        curRo = AlignUp(curRo + dynstr.Length, 8);

        int sysvHashVaddr = roOff + curRo;
        Buffer.BlockCopy(sysvHash, 0, ro, curRo, sysvHash.Length);
        curRo = AlignUp(curRo + sysvHash.Length, 8);

        int gnuHashVaddr = roOff + curRo;
        Buffer.BlockCopy(gnuHash, 0, ro, curRo, gnuHash.Length);
        curRo = AlignUp(curRo + gnuHash.Length, 8);

        if (curRo > roSize)
            throw new InvalidDataException("Tabelas ELF excederam o tamanho de .rodata do stub subsdk1.");

        // ---- .data layout (.dynamic at 0x3100) ----
        const int dynVaddr = dataOff + 0x0100;
        (ulong Tag, ulong Val)[] dynEntries =
        [
            (14UL, sonameOff),               // DT_SONAME
            (30UL, 8UL),                     // DT_FLAGS (DF_BIND_NOW)
            (0x6FFFFFFBUL, 1UL),             // DT_FLAGS_1 (DF_1_NOW)
            (3UL, dataOff),                  // DT_PLTGOT
            (6UL, (ulong)dynsymVaddr),       // DT_SYMTAB
            (11UL, 24UL),                    // DT_SYMENT
            (5UL, (ulong)dynstrVaddr),       // DT_STRTAB
            (10UL, (ulong)dynstr.Length),    // DT_STRSZ
            (4UL, (ulong)sysvHashVaddr),     // DT_HASH
            (0x6FFFFEF5UL, (ulong)gnuHashVaddr), // DT_GNU_HASH
            (12UL, 0x0040UL),                // DT_INIT
            (13UL, 0x0048UL),                // DT_FINI
            (0UL, 0UL),                      // DT_NULL
        ];
        for (int i = 0; i < dynEntries.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x0100 + i * 16, 8), dynEntries[i].Tag);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x0100 + i * 16 + 8, 8), dynEntries[i].Val);
        }

        // MOD0 + nnSdk 21.4.4 extended fields at ro+0x18 (mod0Vaddr = 0x1018)
        const int ehEmptyVaddr = roOff + 0x0058;
        const int runtimeModVaddr = bssOff + 0x0010;
        Encoding.ASCII.GetBytes("MOD0").CopyTo(ro, 0x0018);
        int[] mod0Rel =
        [
            dynVaddr - (int)mod0Vaddr,
            bssOff - (int)mod0Vaddr,
            (bssOff + bssSize) - (int)mod0Vaddr,
            ehEmptyVaddr - (int)mod0Vaddr,
            ehEmptyVaddr - (int)mod0Vaddr,
            runtimeModVaddr - (int)mod0Vaddr,
            dataOff - (int)mod0Vaddr,
            bssOff - (int)mod0Vaddr,
            (roOff + 0x000C) - (int)mod0Vaddr,
            (roOff + 0x0018) - (int)mod0Vaddr,
            noteVaddr - (int)mod0Vaddr,
            (noteVaddr + 36) - (int)mod0Vaddr,
        ];
        for (int i = 0; i < mod0Rel.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(ro.AsSpan(0x001C + i * 4, 4), mod0Rel[i]);

        // ---- NSO0 Header (0x100 bytes) ----
        byte[] hdr = new byte[0x100];
        const uint flags = (1u << 3) | (1u << 4) | (1u << 5); // SHA-256 checks enabled, uncompressed
        const uint fText = 0x0100;
        const uint fRo = fText + textSize;
        const uint fData = fRo + roSize;

        Encoding.ASCII.GetBytes("NSO0").CopyTo(hdr, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x0C, 4), flags);

        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x10, 4), fText);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x14, 4), textOff);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x18, 4), textSize);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x1C, 4), 1u);

        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x20, 4), fRo);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x24, 4), roOff);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x28, 4), roSize);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x2C, 4), 1u);

        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x30, 4), fData);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x34, 4), dataOff);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x38, 4), dataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x3C, 4), bssSize);

        Buffer.BlockCopy(buildId, 0, hdr, 0x40, 32);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x60, 4), textSize);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x64, 4), roSize);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x68, 4), dataSize);

        SHA256.HashData(text).CopyTo(hdr, 0xA0);
        SHA256.HashData(ro).CopyTo(hdr, 0xC0);
        SHA256.HashData(data).CopyTo(hdr, 0xE0);

        byte[] output = new byte[hdr.Length + text.Length + ro.Length + data.Length];
        Buffer.BlockCopy(hdr, 0, output, 0, hdr.Length);
        Buffer.BlockCopy(text, 0, output, (int)fText, text.Length);
        Buffer.BlockCopy(ro, 0, output, (int)fRo, ro.Length);
        Buffer.BlockCopy(data, 0, output, (int)fData, data.Length);
        return output;
    }

    public static (byte[] BuildId, List<string> Exports) ReadNsoBuildIdAndExports(byte[] nso, string label)
    {
        if (nso.Length < 0x100 || nso[0] != (byte)'N' || nso[1] != (byte)'S' || nso[2] != (byte)'O' || nso[3] != (byte)'0')
            throw new InvalidDataException($"'{label}' não é um binário NSO0 válido.");

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(nso.AsSpan(0x0C, 4));
        byte[] buildId = new byte[32];
        Buffer.BlockCopy(nso, 0x40, buildId, 0, 32);

        var segs = new (uint FileOff, uint MemOff, uint MemSize, uint FileSize)[3];
        for (int i = 0; i < 3; i++)
        {
            segs[i] = (
                BinaryPrimitives.ReadUInt32LittleEndian(nso.AsSpan(0x10 + i * 0x10, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(nso.AsSpan(0x14 + i * 0x10, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(nso.AsSpan(0x18 + i * 0x10, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(nso.AsSpan(0x60 + i * 4, 4))
            );
        }

        ulong imageSizeU64 = (ulong)segs[2].MemOff + segs[2].MemSize;
        if (imageSizeU64 == 0 || imageSizeU64 > 256UL * 1024 * 1024)
            throw new InvalidDataException($"Tamanho de imagem NSO0 inválido em '{label}': {imageSizeU64}.");

        byte[] img = new byte[(int)imageSizeU64];
        for (int i = 0; i < 3; i++)
        {
            var s = segs[i];
            if ((ulong)s.FileOff + s.FileSize > (ulong)nso.Length || (ulong)s.MemOff + s.MemSize > (ulong)img.Length)
                throw new InvalidDataException($"Segmento {i} fora dos limites em '{label}'.");

            ReadOnlySpan<byte> src = nso.AsSpan((int)s.FileOff, (int)s.FileSize);
            byte[] decoded;
            if ((flags & (1u << i)) != 0)
            {
                decoded = Lz4BlockDecompress(src, (int)s.MemSize);
            }
            else
            {
                decoded = src.ToArray();
            }

            if (decoded.Length < (int)s.MemSize)
                throw new InvalidDataException($"Segmento {i} truncado em '{label}' ({decoded.Length} < {s.MemSize}).");

            if ((flags & (8u << i)) != 0)
            {
                byte[] actualHash = SHA256.HashData(decoded.AsSpan(0, (int)s.MemSize));
                if (!actualHash.AsSpan().SequenceEqual(nso.AsSpan(0xA0 + i * 0x20, 0x20)))
                    throw new InvalidDataException($"SHA-256 inválido no segmento {i} de '{label}'.");
            }

            Buffer.BlockCopy(decoded, 0, img, (int)s.MemOff, (int)s.MemSize);
        }

        // Parse MOD0 -> .dynamic -> exported symbols
        if (img.Length < 8)
            throw new InvalidDataException($"Imagem NSO0 muito pequena em '{label}'.");
        uint m0 = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(4, 4));
        if ((ulong)m0 + 0x1C > (ulong)img.Length ||
            img[m0] != (byte)'M' || img[m0 + 1] != (byte)'O' || img[m0 + 2] != (byte)'D' || img[m0 + 3] != (byte)'0')
            throw new InvalidDataException($"Cabeçalho MOD0 ausente em '{label}'.");

        int dynRel = BinaryPrimitives.ReadInt32LittleEndian(img.AsSpan((int)m0 + 4, 4));
        long dynOff = (long)m0 + dynRel;
        if (dynOff < 0 || dynOff >= img.Length)
            throw new InvalidDataException($"Seção .dynamic fora dos limites em '{label}'.");

        var dyn = new Dictionary<long, ulong>();
        long off = dynOff;
        while (off + 16 <= img.Length)
        {
            long tag = BinaryPrimitives.ReadInt64LittleEndian(img.AsSpan((int)off, 8));
            ulong val = BinaryPrimitives.ReadUInt64LittleEndian(img.AsSpan((int)off + 8, 8));
            off += 16;
            if (tag == 0) break;
            dyn.TryAdd(tag, val);
        }

        if (!dyn.TryGetValue(5, out ulong strtabU) || !dyn.TryGetValue(6, out ulong symtabU))
            throw new InvalidDataException($"DT_STRTAB/DT_SYMTAB ausentes em '{label}'.");

        ulong syment = dyn.TryGetValue(11, out ulong se) && se > 0 ? se : 24UL;
        ulong nsyms = 0;
        if (dyn.TryGetValue(4, out ulong hashOff) && hashOff + 8 <= (ulong)img.Length)
        {
            nsyms = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan((int)hashOff + 4, 4));
        }
        else if (strtabU > symtabU)
        {
            nsyms = (strtabU - symtabU) / syment;
        }

        var symsByAddr = new Dictionary<ulong, string>();
        for (ulong i = 0; i < nsyms; i++)
        {
            ulong o = symtabU + i * syment;
            if (o + 24 > (ulong)img.Length) break;
            int oi = (int)o;
            uint nm = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(oi, 4));
            byte info = img[oi + 4];
            ushort shn = BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(oi + 6, 2));
            ulong val = BinaryPrimitives.ReadUInt64LittleEndian(img.AsSpan(oi + 8, 8));

            if (shn != 0 && nm != 0 && (info & 15) is 0 or 1 or 2 && val != 0)
            {
                int nameStart = checked((int)(strtabU + nm));
                if (nameStart >= 0 && nameStart < img.Length)
                {
                    int nameEnd = Array.IndexOf(img, (byte)0, nameStart);
                    if (nameEnd > nameStart)
                    {
                        string name = Encoding.Latin1.GetString(img, nameStart, nameEnd - nameStart);
                        symsByAddr[val] = name;
                    }
                }
            }
        }

        var exports = symsByAddr.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        return (buildId, exports);
    }

    public static byte[] Lz4BlockDecompress(ReadOnlySpan<byte> src, int expectedSize)
    {
        byte[] dst = new byte[expectedSize];
        int si = 0, di = 0, sn = src.Length;
        while (si < sn)
        {
            byte tok = src[si++];
            int litLen = tok >> 4;
            if (litLen == 15)
            {
                byte b;
                do
                {
                    if (si >= sn) throw new InvalidDataException("Fluxo LZ4 truncado (literal length).");
                    b = src[si++];
                    litLen += b;
                } while (b == 255);
            }

            if (si + litLen > sn || di + litLen > expectedSize)
                throw new InvalidDataException("Fluxo LZ4 fora dos limites (literal copy).");
            src.Slice(si, litLen).CopyTo(dst.AsSpan(di, litLen));
            si += litLen;
            di += litLen;

            if (si >= sn) break;

            if (si + 2 > sn) throw new InvalidDataException("Fluxo LZ4 truncado (offset).");
            int offset = src[si] | (src[si + 1] << 8);
            si += 2;
            if (offset == 0 || offset > di)
                throw new InvalidDataException($"Offset LZ4 inválido ({offset}, pos={di}).");

            int matchLen = (tok & 0x0F) + 4;
            if ((tok & 0x0F) == 15)
            {
                byte b;
                do
                {
                    if (si >= sn) throw new InvalidDataException("Fluxo LZ4 truncado (match length).");
                    b = src[si++];
                    matchLen += b;
                } while (b == 255);
            }

            if (di + matchLen > expectedSize)
                throw new InvalidDataException("Fluxo LZ4 excede o tamanho descomprimido esperado.");

            int p = di - offset;
            for (int k = 0; k < matchLen; k++)
                dst[di++] = dst[p + k];
        }

        if (di != expectedSize)
            throw new InvalidDataException($"Tamanho LZ4 descomprimido divergente ({di} != {expectedSize}).");
        return dst;
    }

    static uint ElfSysvHash(ReadOnlySpan<byte> name)
    {
        uint h = 0;
        foreach (byte b in name)
        {
            h = (h << 4) + b;
            uint g = h & 0xF0000000u;
            if (g != 0) h ^= g >> 24;
            h &= ~g;
        }
        return h;
    }

    static uint ElfGnuHash(ReadOnlySpan<byte> name)
    {
        uint h = 5381;
        foreach (byte b in name)
            h = (h * 33u) + b;
        return h;
    }

    static int AlignUp(int val, int align) => (val + align - 1) & ~(align - 1);
}
