using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace XC2.Core;

public sealed record NupPackageInfo(
    int Index,
    ulong TitleId,
    string TitleIdHex,
    uint RootDirIndex,
    int FileCount,
    long UncompressedBytes,
    long StoredBytes,
    bool IsBaseGame
);

public sealed record NupControlInfo(
    byte[] RawNacp,
    string DisplayVersion,
    string GameTitle,
    string VerificationNote,
    string Sha256Hex
);

public sealed record NupInspection(
    string NupPath,
    long NupFileBytes,
    ulong BaseTitleId,
    string BaseTitleIdHex,
    string DisplayVersion,
    string GameTitle,
    bool IsXenoblade2Switch2,
    int PackageCount,
    int TotalFileCount,
    long TotalUncompressedBytes,
    long EstimatedOutputBytes,
    NupPackageInfo[] Packages,
    bool ZstdAvailable,
    string? ZstdPath
);

public sealed record RomfsBuildResult(
    string OutputPath,
    string TitleIdHex,
    int DirCount,
    int FileCount,
    long SizeBytes,
    string Sha256Hex
);

/// <summary>
/// Clean-room native C# NUP v1 container reader, SHA-256 verifier, Zstd streaming decompressor,
/// control.nacp extractor, and deterministic Nintendo Switch Horizon RomFS (.romfs) builder.
/// Bit-exact equivalent of work/tools/nup_extract.py and work/tools/nup_control.py.
/// </summary>
public sealed class NupArchive : IDisposable
{
    public const ulong Xc2Switch2TitleId = 0x0400E95004038001UL;
    public const int NacpSize = 0x4000;
    const int StreamChunkBytes = 4 * 1024 * 1024;

    readonly FileStream stream;
    readonly long fileLength;
    readonly byte[] toc;
    readonly byte[] strtab;
    readonly int pkgOff = 0x30;
    readonly int dirOff;
    readonly int fileOff;
    readonly int childOff;
    readonly uint[] children;
    readonly DirRec[] dirs;
    readonly ZstdNative? zstd;
    IntPtr dstream;
    bool disposed;

    public string FilePath { get; }
    public uint PackageCount { get; }
    public ulong DirCount { get; }
    public ulong FileCount { get; }
    public bool ZstdAvailable => zstd != null;
    public string? ZstdPath => zstd?.LibraryPath;

    readonly record struct DirRec(uint NameOff, uint NameLen, uint FirstChild, uint ChildCount);
    public readonly record struct FileRec(string Name, ulong RawSize, ulong BlobOffset, ulong StoredSize, uint Compression, byte[] Sha256);

    NupArchive(string path, FileStream fs, byte[] tocBytes, uint nPkg, ulong nDir, ulong nFile, int strOff, int strLen, ZstdNative? zstdLib)
    {
        FilePath = path;
        stream = fs;
        fileLength = fs.Length;
        toc = tocBytes;
        PackageCount = nPkg;
        DirCount = nDir;
        FileCount = nFile;
        zstd = zstdLib;

        strtab = new byte[strLen];
        Buffer.BlockCopy(toc, strOff, strtab, 0, strLen);

        dirOff = pkgOff + checked((int)nPkg * 0x40);
        fileOff = dirOff + checked((int)nDir * 24);
        childOff = fileOff + checked((int)nFile * 80);

        dirs = new DirRec[checked((int)nDir)];
        for (int i = 0; i < dirs.Length; i++)
        {
            int o = dirOff + i * 24;
            dirs[i] = new DirRec(
                BinaryPrimitives.ReadUInt32LittleEndian(toc.AsSpan(o, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(toc.AsSpan(o + 4, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(toc.AsSpan(o + 8, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(toc.AsSpan(o + 12, 4))
            );
        }

        int childCount = (strOff - childOff) / 4;
        children = new uint[childCount];
        for (int i = 0; i < childCount; i++)
            children[i] = BinaryPrimitives.ReadUInt32LittleEndian(toc.AsSpan(childOff + i * 4, 4));
    }

    public static NupArchive Open(string nupPath, IEnumerable<string>? extraZstdSearchDirs = null)
    {
        if (!File.Exists(nupPath))
            throw new FileNotFoundException("Arquivo .nup não encontrado.", nupPath);

        var fs = new FileStream(nupPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
        try
        {
            Span<byte> hdr = stackalloc byte[0x80];
            ReadExact(fs, hdr);
            if (hdr[0] != (byte)'N' || hdr[1] != (byte)'U' || hdr[2] != (byte)'P' || hdr[3] != 1)
                throw new InvalidDataException("Assinatura NUP v1 inválida (esperado 'NUP\\x01').");

            ulong tocOff = BinaryPrimitives.ReadUInt64LittleEndian(hdr.Slice(0x08, 8));
            ulong tocSz = BinaryPrimitives.ReadUInt64LittleEndian(hdr.Slice(0x10, 8));
            ReadOnlySpan<byte> expectedTocSha = hdr.Slice(0x18, 32);

            if (tocSz < 0x30 || tocSz > 256UL * 1024 * 1024 || tocOff + tocSz > (ulong)fs.Length)
                throw new InvalidDataException($"Extensão do TOC inválida (offset=0x{tocOff:X}, size={tocSz}).");

            fs.Position = checked((long)tocOff);
            byte[] tocBytes = new byte[checked((int)tocSz)];
            ReadExact(fs, tocBytes);

            byte[] actualTocSha = SHA256.HashData(tocBytes);
            if (!actualTocSha.AsSpan().SequenceEqual(expectedTocSha))
                throw new InvalidDataException("Falha na verificação SHA-256 do índice TOC do arquivo .nup (arquivo corrompido).");

            if (tocBytes[0] != (byte)'T' || tocBytes[1] != (byte)'O' || tocBytes[2] != (byte)'C' || tocBytes[3] != 1)
                throw new InvalidDataException("Assinatura TOC v1 inválida (esperado 'TOC\\x01').");

            uint nPkg = BinaryPrimitives.ReadUInt32LittleEndian(tocBytes.AsSpan(4, 4));
            ulong nDir = BinaryPrimitives.ReadUInt64LittleEndian(tocBytes.AsSpan(8, 8));
            ulong nFile = BinaryPrimitives.ReadUInt64LittleEndian(tocBytes.AsSpan(16, 8));
            ulong strOff = BinaryPrimitives.ReadUInt64LittleEndian(tocBytes.AsSpan(24, 8));
            ulong strLen = BinaryPrimitives.ReadUInt64LittleEndian(tocBytes.AsSpan(32, 8));

            if (strOff + strLen > tocSz)
                throw new InvalidDataException("Tabela de strings do TOC fora dos limites.");

            var zstdLib = ZstdNative.TryLoad(extraZstdSearchDirs);
            return new NupArchive(nupPath, fs, tocBytes, nPkg, nDir, nFile, checked((int)strOff), checked((int)strLen), zstdLib);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public ulong GetPackageTitleId(int pkgIndex)
    {
        if (pkgIndex < 0 || pkgIndex >= PackageCount)
            throw new ArgumentOutOfRangeException(nameof(pkgIndex));
        return BinaryPrimitives.ReadUInt64LittleEndian(toc.AsSpan(pkgOff + pkgIndex * 0x40, 8));
    }

    public uint GetPackageRootDir(int pkgIndex)
    {
        if (pkgIndex < 0 || pkgIndex >= PackageCount)
            throw new ArgumentOutOfRangeException(nameof(pkgIndex));
        return BinaryPrimitives.ReadUInt32LittleEndian(toc.AsSpan(pkgOff + pkgIndex * 0x40 + 0x08, 4));
    }

    public (ulong Offset, uint Size) GetPackageMetadataExtent(int pkgIndex)
    {
        if (pkgIndex < 0 || pkgIndex >= PackageCount)
            throw new ArgumentOutOfRangeException(nameof(pkgIndex));
        int o = pkgOff + pkgIndex * 0x40 + 0x20;
        return (
            BinaryPrimitives.ReadUInt64LittleEndian(toc.AsSpan(o, 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(toc.AsSpan(o + 8, 4))
        );
    }

    public string ReadString(uint off, uint len) => Encoding.UTF8.GetString(strtab, checked((int)off), checked((int)len));

    public FileRec GetFileRecord(int fileIndex)
    {
        int o = fileOff + fileIndex * 80;
        uint nOff = BinaryPrimitives.ReadUInt32LittleEndian(toc.AsSpan(o, 4));
        uint nLen = BinaryPrimitives.ReadUInt32LittleEndian(toc.AsSpan(o + 4, 4));
        ulong rawSz = BinaryPrimitives.ReadUInt64LittleEndian(toc.AsSpan(o + 8, 8));
        ulong blobOff = BinaryPrimitives.ReadUInt64LittleEndian(toc.AsSpan(o + 16, 8));
        ulong storedSz = BinaryPrimitives.ReadUInt64LittleEndian(toc.AsSpan(o + 24, 8));
        byte[] sha = new byte[32];
        Buffer.BlockCopy(toc, o + 32, sha, 0, 32);
        uint comp = BinaryPrimitives.ReadUInt32LittleEndian(toc.AsSpan(o + 64, 4));
        return new FileRec(ReadString(nOff, nLen), rawSz, blobOff, storedSz, comp, sha);
    }

    public IEnumerable<(string Name, int Index, bool IsDir)> EnumerateChildren(uint dirIndex)
    {
        var d = dirs[dirIndex];
        for (uint ci = d.FirstChild; ci < d.FirstChild + d.ChildCount; ci++)
        {
            uint entry = children[ci];
            if (entry < DirCount)
            {
                var sub = dirs[entry];
                yield return (ReadString(sub.NameOff, sub.NameLen), checked((int)entry), true);
            }
            else
            {
                int fi = checked((int)(entry - DirCount));
                yield return (GetFileRecord(fi).Name, fi, false);
            }
        }
    }

    public NupInspection Inspect()
    {
        var pkgs = new List<NupPackageInfo>((int)PackageCount);
        int totalFiles = 0;
        long totalRaw = 0;
        long estOut = 0;

        for (int pi = 0; pi < PackageCount; pi++)
        {
            ulong tid = GetPackageTitleId(pi);
            uint rootDi = GetPackageRootDir(pi);
            int pFiles = 0;
            long pRaw = 0;
            long pStored = 0;
            CountSubtree(rootDi, ref pFiles, ref pRaw, ref pStored);
            pkgs.Add(new NupPackageInfo(
                Index: pi,
                TitleId: tid,
                TitleIdHex: $"{tid:x16}",
                RootDirIndex: rootDi,
                FileCount: pFiles,
                UncompressedBytes: pRaw,
                StoredBytes: pStored,
                IsBaseGame: pi == 0
            ));
            totalFiles += pFiles;
            totalRaw += pRaw;
            // RomFS images add ~1.5% overhead for tables/alignment; base adds ~120 MiB AOT DLL
            estOut += pi == 0 ? pRaw + 160L * 1024 * 1024 : pRaw + Math.Max(4096, pRaw / 64);
        }

        ulong baseTid = PackageCount > 0 ? GetPackageTitleId(0) : 0;
        string displayVer = "";
        string gameTitle = "Xenoblade Chronicles 2";
        try
        {
            var ctrl = ReadControlNacp();
            displayVer = ctrl.DisplayVersion;
            if (!string.IsNullOrWhiteSpace(ctrl.GameTitle))
                gameTitle = ctrl.GameTitle;
        }
        catch { }

        return new NupInspection(
            NupPath: FilePath,
            NupFileBytes: fileLength,
            BaseTitleId: baseTid,
            BaseTitleIdHex: $"{baseTid:x16}",
            DisplayVersion: displayVer,
            GameTitle: gameTitle,
            IsXenoblade2Switch2: baseTid == Xc2Switch2TitleId,
            PackageCount: (int)PackageCount,
            TotalFileCount: totalFiles,
            TotalUncompressedBytes: totalRaw,
            EstimatedOutputBytes: estOut,
            Packages: pkgs.ToArray(),
            ZstdAvailable: ZstdAvailable,
            ZstdPath: ZstdPath
        );
    }

    void CountSubtree(uint dirIndex, ref int files, ref long rawBytes, ref long storedBytes)
    {
        var d = dirs[dirIndex];
        for (uint ci = d.FirstChild; ci < d.FirstChild + d.ChildCount; ci++)
        {
            uint entry = children[ci];
            if (entry < DirCount)
            {
                CountSubtree(entry, ref files, ref rawBytes, ref storedBytes);
            }
            else
            {
                var rec = GetFileRecord(checked((int)(entry - DirCount)));
                files++;
                rawBytes += checked((long)rec.RawSize);
                storedBytes += checked((long)rec.StoredSize);
            }
        }
    }

    public NupControlInfo ReadControlNacp()
    {
        if (PackageCount == 0)
            throw new InvalidDataException("O arquivo .nup não contém nenhum pacote.");

        uint rootDir = GetPackageRootDir(0);
        int? nacpFileIdx = null;
        foreach (var (name, idx, isDir) in EnumerateChildren(rootDir))
        {
            if (!isDir && name.Equals("control.nacp", StringComparison.OrdinalIgnoreCase))
            {
                nacpFileIdx = idx;
                break;
            }
            if (isDir && name.Equals("control", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var (cname, cidx, cIsDir) in EnumerateChildren((uint)idx))
                {
                    if (!cIsDir && cname.Equals("control.nacp", StringComparison.OrdinalIgnoreCase))
                    {
                        nacpFileIdx = cidx;
                        break;
                    }
                }
            }
        }

        if (nacpFileIdx.HasValue)
        {
            var rec = GetFileRecord(nacpFileIdx.Value);
            if (rec.RawSize != NacpSize)
                throw new InvalidDataException($"Tamanho inesperado de control.nacp: {rec.RawSize} bytes.");
            using var ms = new MemoryStream(NacpSize);
            var (_, sha) = ExtractFileToStream(nacpFileIdx.Value, ms);
            byte[] data = ms.ToArray();
            return ParseNacpInfo(data, "Entrada de arquivo SHA-256 verificada", sha);
        }

        // Fall back to Package 0 TLV metadata extent (tag 2 = NACP, tag 4 = display version)
        var (metaOff, metaSize) = GetPackageMetadataExtent(0);
        if (metaSize == 0 || metaSize > 16 * 1024 * 1024 || metaOff + metaSize > (ulong)fileLength)
            throw new InvalidDataException("Extensão de metadados TLV do Pacote 0 ausente ou inválida.");

        stream.Position = checked((long)metaOff);
        byte[] meta = new byte[metaSize];
        ReadExact(stream, meta);

        byte[]? nacpData = null;
        int pos = 0;
        while (pos < meta.Length)
        {
            if (meta.Length - pos < 8)
                throw new InvalidDataException("Registro TLV truncado nos metadados do Pacote 0.");
            uint tag = BinaryPrimitives.ReadUInt32LittleEndian(meta.AsSpan(pos, 4));
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(meta.AsSpan(pos + 4, 4));
            int start = pos + 8;
            pos = checked((int)(start + len));
            if (pos > meta.Length)
                throw new InvalidDataException($"Registro TLV 0x{tag:X} excede os limites de metadados.");
            if (tag == 2)
                nacpData = meta.AsSpan(start, checked((int)len)).ToArray();
        }

        if (nacpData == null || nacpData.Length != NacpSize)
            throw new InvalidDataException("control.nacp (16 KiB) não encontrado nos metadados do Pacote 0.");

        string shaHex = Convert.ToHexString(SHA256.HashData(nacpData)).ToLowerInvariant();
        return ParseNacpInfo(nacpData, "TOC SHA-256 verificado (TLV tag 2)", shaHex);
    }

    static NupControlInfo ParseNacpInfo(byte[] data, string note, string shaHex)
    {
        string ReadZString(int off, int maxLen)
        {
            int end = off;
            while (end < off + maxLen && end < data.Length && data[end] != 0) end++;
            return Encoding.UTF8.GetString(data, off, end - off).Trim();
        }

        // Title entry 0 (American English) is at 0x0000 (0x200 bytes); display version is at 0x3060 (0x10 bytes)
        string title = ReadZString(0x0000, 0x200);
        if (string.IsNullOrEmpty(title)) title = "Xenoblade Chronicles 2";
        string version = ReadZString(0x3060, 0x10);
        return new NupControlInfo(data, version, title, note, shaHex);
    }

    public (long BytesWritten, string Sha256Hex) ExtractFileToStream(
        int fileIndex,
        Stream output,
        Action<long>? onBytesDelta = null,
        CancellationToken ct = default)
    {
        var rec = GetFileRecord(fileIndex);
        if (rec.BlobOffset + rec.StoredSize > (ulong)fileLength)
            throw new InvalidDataException($"Arquivo '{rec.Name}' fora dos limites do .nup.");

        stream.Position = checked((long)rec.BlobOffset);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long written = 0;

        if (rec.Compression == 0)
        {
            byte[] buf = new byte[Math.Min(StreamChunkBytes, Math.Max(4096, (int)Math.Min(rec.StoredSize, (ulong)StreamChunkBytes)))];
            ulong rem = rec.StoredSize;
            while (rem > 0)
            {
                ct.ThrowIfCancellationRequested();
                int toRead = (int)Math.Min((ulong)buf.Length, rem);
                int got = stream.Read(buf, 0, toRead);
                if (got <= 0) throw new EndOfStreamException($"EOF inesperado ao ler '{rec.Name}'.");
                output.Write(buf, 0, got);
                hasher.AppendData(buf, 0, got);
                written += got;
                rem -= (ulong)got;
                onBytesDelta?.Invoke(got);
            }
        }
        else if (rec.Compression == 1)
        {
            if (zstd == null)
                throw new InvalidOperationException("Biblioteca nativa libzstd.dll não encontrada para descompressão Zstd.");

            if (rec.RawSize <= 16UL * 1024 * 1024)
            {
                ct.ThrowIfCancellationRequested();
                byte[] src = new byte[checked((int)rec.StoredSize)];
                ReadExact(stream, src);
                byte[] dst = new byte[checked((int)rec.RawSize)];
                nuint ret = zstd.Decompress(dst, src);
                if (zstd.IsError(ret) || ret != (nuint)rec.RawSize)
                    throw new InvalidDataException($"Erro Zstd ao descomprimir '{rec.Name}' (ret={ret}, esperado={rec.RawSize}).");
                output.Write(dst, 0, dst.Length);
                hasher.AppendData(dst, 0, dst.Length);
                written = dst.Length;
                onBytesDelta?.Invoke(written);
            }
            else
            {
                if (dstream == IntPtr.Zero) dstream = zstd.CreateDStream();
                zstd.InitDStream(dstream);

                byte[] inArr = new byte[StreamChunkBytes];
                byte[] outArr = new byte[StreamChunkBytes];
                ulong rem = rec.StoredSize;

                var inPin = GCHandle.Alloc(inArr, GCHandleType.Pinned);
                var outPin = GCHandle.Alloc(outArr, GCHandleType.Pinned);
                try
                {
                    var inBuf = new ZstdNative.ZstdInBuffer { Src = inPin.AddrOfPinnedObject(), Size = 0, Pos = 0 };
                    while ((ulong)written < rec.RawSize)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (inBuf.Pos == inBuf.Size)
                        {
                            int toRead = (int)Math.Min((ulong)inArr.Length, rem);
                            if (toRead <= 0)
                                throw new EndOfStreamException($"EOF inesperado no fluxo Zstd de '{rec.Name}'.");
                            ReadExact(stream, inArr.AsSpan(0, toRead));
                            rem -= (ulong)toRead;
                            inBuf.Size = (nuint)toRead;
                            inBuf.Pos = 0;
                        }
                        var outBuf = new ZstdNative.ZstdOutBuffer { Dst = outPin.AddrOfPinnedObject(), Size = (nuint)outArr.Length, Pos = 0 };
                        nuint ret = zstd.DecompressStream(dstream, ref outBuf, ref inBuf);
                        if (zstd.IsError(ret))
                            throw new InvalidDataException($"Erro Zstd streaming ao descomprimir '{rec.Name}' (code={ret}).");
                        int produced = checked((int)outBuf.Pos);
                        if (produced > 0)
                        {
                            output.Write(outArr, 0, produced);
                            hasher.AppendData(outArr, 0, produced);
                            written += produced;
                            onBytesDelta?.Invoke(produced);
                        }
                    }
                }
                finally
                {
                    inPin.Free();
                    outPin.Free();
                }
            }
        }
        else
        {
            throw new NotSupportedException($"Método de compressão NUP desconhecido: {rec.Compression} em '{rec.Name}'.");
        }

        byte[] actualSha = hasher.GetHashAndReset();
        if ((ulong)written != rec.RawSize || !actualSha.AsSpan().SequenceEqual(rec.Sha256))
            throw new InvalidDataException($"SHA-256 divergente em '{rec.Name}' (arquivo corrompido).");

        return (written, Convert.ToHexString(actualSha).ToLowerInvariant());
    }

    public (int FilesExtracted, long BytesExtracted) ExtractBasePackage(
        string dataDir,
        Action<string, long>? onFileProgress = null,
        CancellationToken ct = default)
    {
        if (PackageCount == 0)
            throw new InvalidDataException("Pacote base (Pkg[0]) ausente no arquivo .nup.");

        int files = 0;
        long bytes = 0;
        uint rootDir = GetPackageRootDir(0);

        void Walk(uint di, string relPrefix)
        {
            foreach (var (name, idx, isDir) in EnumerateChildren(di))
            {
                ct.ThrowIfCancellationRequested();
                string childRel = string.IsNullOrEmpty(relPrefix) ? name : $"{relPrefix}/{name}";
                if (isDir)
                {
                    Walk((uint)idx, childRel);
                }
                else
                {
                    var rec = GetFileRecord(idx);
                    // Route subsdk1 (dlss Ampere) & subsdk2 (cuda Ampere) to exefs_host_bypass/
                    string dstPath = relPrefix.Equals("exefs", StringComparison.OrdinalIgnoreCase) &&
                                     (name.Equals("subsdk1", StringComparison.OrdinalIgnoreCase) ||
                                      name.Equals("subsdk2", StringComparison.OrdinalIgnoreCase))
                        ? Path.Combine(dataDir, "exefs_host_bypass", name)
                        : Path.Combine(dataDir, childRel.Replace('/', Path.DirectorySeparatorChar));

                    var fi = new FileInfo(dstPath);
                    if (fi.Exists && (ulong)fi.Length == rec.RawSize)
                    {
                        files++;
                        bytes += checked((long)rec.RawSize);
                        onFileProgress?.Invoke(childRel, checked((long)rec.RawSize));
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
                    string tmpPath = dstPath + ".part";
                    try
                    {
                        using (var outFs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
                        {
                            ExtractFileToStream(idx, outFs, delta => onFileProgress?.Invoke(childRel, delta), ct);
                        }
                        File.Move(tmpPath, dstPath, overwrite: true);
                    }
                    catch
                    {
                        try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                        throw;
                    }

                    files++;
                    bytes += checked((long)rec.RawSize);
                }
            }
        }

        Walk(rootDir, "");
        return (files, bytes);
    }

    /// <summary>Builds a deterministic Nintendo Switch Horizon RomFS (.romfs) image directly from a NUP Add-On package.</summary>
    public RomfsBuildResult BuildAddonRomfs(
        int pkgIndex,
        string outRomfsPath,
        Action<long>? onBytesDelta = null,
        CancellationToken ct = default)
    {
        if (pkgIndex < 1 || pkgIndex >= PackageCount)
            throw new ArgumentOutOfRangeException(nameof(pkgIndex));

        ulong tid = GetPackageTitleId(pkgIndex);
        uint pkgRootDi = GetPackageRootDir(pkgIndex);
        uint romfsDirIdx = pkgRootDi;
        foreach (var (name, idx, isDir) in EnumerateChildren(pkgRootDi))
        {
            if (isDir && name.Equals("romfs", StringComparison.OrdinalIgnoreCase))
            {
                romfsDirIdx = (uint)idx;
                break;
            }
        }

        return BuildRomfsImage(romfsDirIdx, $"{tid:x16}", outRomfsPath, onBytesDelta, ct);
    }

    public RomfsBuildResult BuildRomfsImage(
        uint romfsDirIdx,
        string titleIdHex,
        string outRomfsPath,
        Action<long>? onBytesDelta = null,
        CancellationToken ct = default)
    {
        var dirEntries = new List<RomDirEntry>();
        var fileEntries = new List<RomFileEntry>();

        int VisitDir(uint nupDi, int parentRomDi, byte[] nameBytes)
        {
            ct.ThrowIfCancellationRequested();
            int myDi = dirEntries.Count;
            dirEntries.Add(new RomDirEntry
            {
                Name = nameBytes,
                Parent = myDi > 0 ? parentRomDi : 0,
                FirstChild = uint.MaxValue,
                NextSib = uint.MaxValue,
                FirstFile = uint.MaxValue,
                HashNext = uint.MaxValue
            });

            var subDirs = new List<(string Name, uint NupDi)>();
            var subFiles = new List<(string Name, int FileIdx, ulong RawSize)>();

            foreach (var (name, idx, isDir) in EnumerateChildren(nupDi))
            {
                if (isDir) subDirs.Add((name, (uint)idx));
                else
                {
                    var rec = GetFileRecord(idx);
                    subFiles.Add((name, idx, rec.RawSize));
                }
            }

            subDirs.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            subFiles.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

            int prevF = -1;
            foreach (var (fn, fi, rsz) in subFiles)
            {
                int fIdx = fileEntries.Count;
                fileEntries.Add(new RomFileEntry
                {
                    Name = Encoding.UTF8.GetBytes(fn),
                    Parent = myDi,
                    NextSib = uint.MaxValue,
                    FileIndex = fi,
                    Size = rsz,
                    HashNext = uint.MaxValue
                });
                if (prevF < 0) dirEntries[myDi].FirstFile = (uint)fIdx;
                else fileEntries[prevF].NextSib = (uint)fIdx;
                prevF = fIdx;
            }

            int prevD = -1;
            foreach (var (dn, subDi) in subDirs)
            {
                int childRomDi = VisitDir(subDi, myDi, Encoding.UTF8.GetBytes(dn));
                if (prevD < 0) dirEntries[myDi].FirstChild = (uint)childRomDi;
                else dirEntries[prevD].NextSib = (uint)childRomDi;
                prevD = childRomDi;
            }
            return myDi;
        }

        VisitDir(romfsDirIdx, 0, Array.Empty<byte>());

        uint[] dirOffsets = new uint[dirEntries.Count];
        uint curDoff = 0;
        for (int i = 0; i < dirEntries.Count; i++)
        {
            dirEntries[i].MetaOff = curDoff;
            dirOffsets[i] = curDoff;
            curDoff += checked((uint)(24 + ((dirEntries[i].Name.Length + 3) & ~3)));
        }
        uint dirMetaSize = curDoff;

        uint[] fileOffsets = new uint[fileEntries.Count];
        uint curFoff = 0;
        ulong curDataRel = 0;
        for (int i = 0; i < fileEntries.Count; i++)
        {
            fileEntries[i].MetaOff = curFoff;
            fileOffsets[i] = curFoff;
            curDataRel = (curDataRel + 15UL) & ~15UL;
            fileEntries[i].DataRel = curDataRel;
            curDataRel += fileEntries[i].Size;
            curFoff += checked((uint)(32 + ((fileEntries[i].Name.Length + 3) & ~3)));
        }
        uint fileMetaSize = curFoff;

        uint dirHashCnt = RomfsBucketCount(dirEntries.Count);
        uint fileHashCnt = RomfsBucketCount(fileEntries.Count);
        uint[] dirHashTab = new uint[dirHashCnt];
        uint[] fileHashTab = new uint[fileHashCnt];
        Array.Fill(dirHashTab, uint.MaxValue);
        Array.Fill(fileHashTab, uint.MaxValue);

        for (int i = 0; i < dirEntries.Count; i++)
        {
            uint pOff = dirOffsets[dirEntries[i].Parent];
            uint h = RomfsHash(pOff, dirEntries[i].Name) % dirHashCnt;
            dirEntries[i].HashNext = dirHashTab[h];
            dirHashTab[h] = dirEntries[i].MetaOff;
        }

        for (int i = 0; i < fileEntries.Count; i++)
        {
            uint pOff = dirOffsets[fileEntries[i].Parent];
            uint h = RomfsHash(pOff, fileEntries[i].Name) % fileHashCnt;
            fileEntries[i].HashNext = fileHashTab[h];
            fileHashTab[h] = fileEntries[i].MetaOff;
        }

        byte[] dirMetaBuf = new byte[dirMetaSize];
        int dp = 0;
        foreach (var d in dirEntries)
        {
            uint pOff = dirOffsets[d.Parent];
            uint nsOff = d.NextSib == uint.MaxValue ? uint.MaxValue : dirOffsets[d.NextSib];
            uint fcOff = d.FirstChild == uint.MaxValue ? uint.MaxValue : dirOffsets[d.FirstChild];
            uint ffOff = d.FirstFile == uint.MaxValue ? uint.MaxValue : fileOffsets[d.FirstFile];
            BinaryPrimitives.WriteUInt32LittleEndian(dirMetaBuf.AsSpan(dp, 4), pOff);
            BinaryPrimitives.WriteUInt32LittleEndian(dirMetaBuf.AsSpan(dp + 4, 4), nsOff);
            BinaryPrimitives.WriteUInt32LittleEndian(dirMetaBuf.AsSpan(dp + 8, 4), fcOff);
            BinaryPrimitives.WriteUInt32LittleEndian(dirMetaBuf.AsSpan(dp + 12, 4), ffOff);
            BinaryPrimitives.WriteUInt32LittleEndian(dirMetaBuf.AsSpan(dp + 16, 4), d.HashNext);
            BinaryPrimitives.WriteUInt32LittleEndian(dirMetaBuf.AsSpan(dp + 20, 4), (uint)d.Name.Length);
            dp += 24;
            Buffer.BlockCopy(d.Name, 0, dirMetaBuf, dp, d.Name.Length);
            dp += (d.Name.Length + 3) & ~3;
        }

        byte[] fileMetaBuf = new byte[fileMetaSize];
        int fp = 0;
        foreach (var fe in fileEntries)
        {
            uint pOff = dirOffsets[fe.Parent];
            uint nsOff = fe.NextSib == uint.MaxValue ? uint.MaxValue : fileOffsets[fe.NextSib];
            BinaryPrimitives.WriteUInt32LittleEndian(fileMetaBuf.AsSpan(fp, 4), pOff);
            BinaryPrimitives.WriteUInt32LittleEndian(fileMetaBuf.AsSpan(fp + 4, 4), nsOff);
            BinaryPrimitives.WriteUInt64LittleEndian(fileMetaBuf.AsSpan(fp + 8, 8), fe.DataRel);
            BinaryPrimitives.WriteUInt64LittleEndian(fileMetaBuf.AsSpan(fp + 16, 8), fe.Size);
            BinaryPrimitives.WriteUInt32LittleEndian(fileMetaBuf.AsSpan(fp + 24, 4), fe.HashNext);
            BinaryPrimitives.WriteUInt32LittleEndian(fileMetaBuf.AsSpan(fp + 28, 4), (uint)fe.Name.Length);
            fp += 32;
            Buffer.BlockCopy(fe.Name, 0, fileMetaBuf, fp, fe.Name.Length);
            fp += (fe.Name.Length + 3) & ~3;
        }

        ulong off = 0x50;
        ulong dhOff = off, dhSz = (ulong)dirHashCnt * 4; off += dhSz;
        ulong dmOff = off, dmSz = dirMetaSize; off += dmSz;
        ulong fhOff = off, fhSz = (ulong)fileHashCnt * 4; off += fhSz;
        ulong fmOff = off, fmSz = fileMetaSize; off += fmSz;
        ulong dataOff = Math.Max(0x200UL, (off + 15UL) & ~15UL);

        byte[] header = new byte[0x50];
        ulong[] hdrVals = [0x50UL, dhOff, dhSz, dmOff, dmSz, fhOff, fhSz, fmOff, fmSz, dataOff];
        for (int i = 0; i < 10; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(i * 8, 8), hdrVals[i]);

        Directory.CreateDirectory(Path.GetDirectoryName(outRomfsPath)!);
        string tmpPath = outRomfsPath + ".tmp";
        byte[] zeros = new byte[512];
        try
        {
            using (var outFs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
            {
                outFs.Write(header);
                WriteUInt32Array(outFs, dirHashTab);
                outFs.Write(dirMetaBuf);
                WriteUInt32Array(outFs, fileHashTab);
                outFs.Write(fileMetaBuf);

                long padToData = checked((long)dataOff) - outFs.Position;
                while (padToData > 0)
                {
                    int chunk = (int)Math.Min(zeros.Length, padToData);
                    outFs.Write(zeros, 0, chunk);
                    padToData -= chunk;
                }

                ulong curRel = 0;
                foreach (var fe in fileEntries)
                {
                    ct.ThrowIfCancellationRequested();
                    ulong pad = fe.DataRel - curRel;
                    if (pad > 0)
                    {
                        outFs.Write(zeros, 0, checked((int)pad));
                        curRel += pad;
                    }
                    var (written, _) = ExtractFileToStream(fe.FileIndex, outFs, onBytesDelta, ct);
                    curRel += checked((ulong)written);
                }
            }

            string shaHex = GameInstall.QuickSha256(tmpPath);
            long finalSize = new FileInfo(tmpPath).Length;
            File.Move(tmpPath, outRomfsPath, overwrite: true);
            return new RomfsBuildResult(outRomfsPath, titleIdHex, dirEntries.Count, fileEntries.Count, finalSize, shaHex);
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            throw;
        }
    }

    static void WriteUInt32Array(Stream s, uint[] arr)
    {
        byte[] buf = new byte[arr.Length * 4];
        for (int i = 0; i < arr.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(i * 4, 4), arr[i]);
        s.Write(buf);
    }

    public static uint RomfsHash(uint parentOffset, ReadOnlySpan<byte> nameBytes)
    {
        uint h = parentOffset ^ 123456789u;
        for (int i = 0; i < nameBytes.Length; i++)
            h = ((h >> 5) | (h << 27)) ^ nameBytes[i];
        return h;
    }

    public static uint RomfsBucketCount(int count)
    {
        if (count < 3) return 3;
        uint cnt = (uint)count;
        if (cnt < 19) return cnt | 1u;
        while (cnt % 2 == 0 || cnt % 3 == 0 || cnt % 5 == 0 || cnt % 7 == 0 ||
               cnt % 11 == 0 || cnt % 13 == 0 || cnt % 17 == 0)
        {
            cnt++;
        }
        return cnt;
    }

    static void ReadExact(Stream s, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer.Slice(total));
            if (n <= 0) throw new EndOfStreamException("Fim de arquivo inesperado ao ler contêiner .nup.");
            total += n;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (dstream != IntPtr.Zero && zstd != null)
        {
            zstd.FreeDStream(dstream);
            dstream = IntPtr.Zero;
        }
        stream.Dispose();
    }

    sealed class RomDirEntry
    {
        public byte[] Name = Array.Empty<byte>();
        public int Parent;
        public uint FirstChild;
        public uint NextSib;
        public uint FirstFile;
        public uint HashNext;
        public uint MetaOff;
    }

    sealed class RomFileEntry
    {
        public byte[] Name = Array.Empty<byte>();
        public int Parent;
        public uint NextSib;
        public int FileIndex;
        public ulong Size;
        public uint HashNext;
        public uint MetaOff;
        public ulong DataRel;
    }

    sealed class ZstdNative
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct ZstdInBuffer
        {
            public IntPtr Src;
            public nuint Size;
            public nuint Pos;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ZstdOutBuffer
        {
            public IntPtr Dst;
            public nuint Size;
            public nuint Pos;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate nuint DecompressFn(byte[] dst, nuint dstCap, byte[] src, nuint srcSize);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr CreateDStreamFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate nuint FreeDStreamFn(IntPtr dctx);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate nuint InitDStreamFn(IntPtr dctx);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate nuint DecompressStreamFn(IntPtr dctx, ref ZstdOutBuffer output, ref ZstdInBuffer input);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate uint IsErrorFn(nuint code);

        readonly DecompressFn fnDecompress;
        readonly CreateDStreamFn fnCreateDStream;
        readonly FreeDStreamFn fnFreeDStream;
        readonly InitDStreamFn fnInitDStream;
        readonly DecompressStreamFn fnDecompressStream;
        readonly IsErrorFn fnIsError;

        public string LibraryPath { get; }

        ZstdNative(IntPtr handle, string path)
        {
            LibraryPath = path;
            fnDecompress = Marshal.GetDelegateForFunctionPointer<DecompressFn>(NativeLibrary.GetExport(handle, "ZSTD_decompress"));
            fnCreateDStream = Marshal.GetDelegateForFunctionPointer<CreateDStreamFn>(NativeLibrary.GetExport(handle, "ZSTD_createDStream"));
            fnFreeDStream = Marshal.GetDelegateForFunctionPointer<FreeDStreamFn>(NativeLibrary.GetExport(handle, "ZSTD_freeDStream"));
            fnInitDStream = Marshal.GetDelegateForFunctionPointer<InitDStreamFn>(NativeLibrary.GetExport(handle, "ZSTD_initDStream"));
            fnDecompressStream = Marshal.GetDelegateForFunctionPointer<DecompressStreamFn>(NativeLibrary.GetExport(handle, "ZSTD_decompressStream"));
            fnIsError = Marshal.GetDelegateForFunctionPointer<IsErrorFn>(NativeLibrary.GetExport(handle, "ZSTD_isError"));
        }

        public nuint Decompress(byte[] dst, byte[] src) => fnDecompress(dst, (nuint)dst.Length, src, (nuint)src.Length);
        public IntPtr CreateDStream() => fnCreateDStream();
        public void FreeDStream(IntPtr dctx) => fnFreeDStream(dctx);
        public void InitDStream(IntPtr dctx) => fnInitDStream(dctx);
        public nuint DecompressStream(IntPtr dctx, ref ZstdOutBuffer o, ref ZstdInBuffer i) => fnDecompressStream(dctx, ref o, ref i);
        public bool IsError(nuint code) => fnIsError(code) != 0;

        public static ZstdNative? TryLoad(IEnumerable<string>? extraDirs)
        {
            var candidates = new List<string>();
            if (extraDirs != null)
            {
                foreach (var dir in extraDirs)
                {
                    if (!string.IsNullOrWhiteSpace(dir))
                        candidates.Add(Path.Combine(dir, "libzstd.dll"));
                }
            }
            var envRuntime = Environment.GetEnvironmentVariable("XC2_RUNTIME_DIR");
            if (!string.IsNullOrWhiteSpace(envRuntime))
                candidates.Add(Path.Combine(envRuntime, "libzstd.dll"));

            candidates.Add(Path.Combine(AppContext.BaseDirectory, "libzstd.dll"));
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "runtime", "libzstd.dll"));
            var root = GameInstall.FindProjectRoot();
            candidates.Add(Path.Combine(root, "runtime", "libzstd.dll"));
            candidates.Add(Path.Combine(root, "games", "libzstd.dll"));
            candidates.Add(Path.Combine(root, "work", "test", "libzstd.dll"));
            candidates.Add("libzstd.dll");

            foreach (var cand in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (cand != "libzstd.dll" && !File.Exists(cand)) continue;
                if (NativeLibrary.TryLoad(cand, out IntPtr h))
                {
                    try { return new ZstdNative(h, cand); }
                    catch { NativeLibrary.Free(h); }
                }
            }
            return null;
        }
    }
}
