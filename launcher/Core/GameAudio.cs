using System.Buffers.Binary;
using System.Text;

namespace XC2.Core;

public sealed record BgmTrackInfo(
    string Id,
    string FileName,
    string Title,
    string Subtitle,
    double DurationSec,
    double LoopStartSec,
    double LoopEndSec,
    bool Available
);

/// <summary>
/// Pure managed C# media extractor reading directly from the player's own Xenoblade Chronicles 2 romfs:
/// - stream/bgm/*.nop (Nintendo Switch sadf/opus) -> standard OggOpus (.ogg) with exact loop metadata
/// - sound/system.pck (Wwise AKPK -> BKHD/DIDX/DATA/HIRC -> PTADPCM 0x8311) -> 16-bit PCM WAV
/// Zero external tools, zero copyrighted game audio baked into the launcher executable.
/// </summary>
public sealed class GameAudio
{
    public static readonly (string Id, string File, string Title, string Subtitle)[] BgmCatalog =
    [
        ("m01",  "m01.nop",  "Where We Used To Be",       "Tema Principal · Piano"),
        ("m90",  "m90.nop",  "Tela Título (Loop)",        "Tema de Abertura · Loop Contínuo"),
        ("m02",  "m02.nop",  "Elysium, in the Blue Sky",  "Abertura Orquestral"),
        ("m103", "m103.nop", "Torna — The Golden Country","Tema da Expansão"),
    ];

    public static readonly Dictionary<string, string> SfxEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cursor"] = "common022_play", // 0.100s crisp UI tick
        ["decide"] = "common002_play", // 0.486s crisp UI confirm
        ["open"]   = "common001_play", // 1.200s glassy chime
        ["cancel"] = "common027_play", // 0.304s clean back/cancel
        ["play"]   = "common003_play", // 1.434s resonant start chime
        ["error"]  = "common006_play", // 0.526s denied/error tone
    };

    readonly string romfsDir;
    readonly string cacheDir;
    readonly object gate = new();

    public GameAudio(string romfsDir, string cacheDir)
    {
        this.romfsDir = romfsDir;
        this.cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
    }

    public BgmTrackInfo[] ListBgmTracks()
    {
        var list = new List<BgmTrackInfo>();
        foreach (var t in BgmCatalog)
        {
            var src = Path.Combine(romfsDir, "stream", "bgm", t.File);
            if (!File.Exists(src))
            {
                list.Add(new BgmTrackInfo(t.Id, t.File, t.Title, t.Subtitle, 0, 0, 0, false));
                continue;
            }
            var (dur, ls, le) = ReadNopLoopInfo(src);
            list.Add(new BgmTrackInfo(t.Id, t.File, t.Title, t.Subtitle, dur, ls, le, dur > 0));
        }
        return list.ToArray();
    }

    public (byte[] Data, string Mime)? GetBgmOgg(string id)
    {
        var entry = BgmCatalog.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(entry.Id)) return null;
        var src = Path.Combine(romfsDir, "stream", "bgm", entry.File);
        if (!File.Exists(src)) return null;

        var cached = Path.Combine(cacheDir, $"bgm_{entry.Id}_v1.ogg");
        lock (gate)
        {
            if (!File.Exists(cached) || new FileInfo(cached).Length < 1024)
            {
                try
                {
                    var ogg = TransmuxNopToOggOpus(File.ReadAllBytes(src));
                    var tmp = cached + $".tmp.{Environment.ProcessId}";
                    File.WriteAllBytes(tmp, ogg);
                    File.Move(tmp, cached, overwrite: true);
                    LauncherLog.Write($"audio: cached BGM {entry.Id} ({ogg.Length / 1024:N0} KiB)");
                }
                catch (Exception ex)
                {
                    LauncherLog.Write($"audio: bgm {entry.Id} error: {ex.Message}");
                    return null;
                }
            }
        }
        return (File.ReadAllBytes(cached), "audio/ogg");
    }

    public (byte[] Data, string Mime)? GetSfxWav(string id)
    {
        if (!SfxEvents.TryGetValue(id, out var eventName)) return null;
        var pck = Path.Combine(romfsDir, "sound", "system.pck");
        if (!File.Exists(pck)) return null;

        var cached = Path.Combine(cacheDir, $"sfx_{id}_v1.wav");
        lock (gate)
        {
            if (!File.Exists(cached) || new FileInfo(cached).Length < 128)
            {
                try
                {
                    var wav = ExtractSystemSfxWav(pck, eventName);
                    if (wav == null) return null;
                    var tmp = cached + $".tmp.{Environment.ProcessId}";
                    File.WriteAllBytes(tmp, wav);
                    File.Move(tmp, cached, overwrite: true);
                    LauncherLog.Write($"audio: cached SFX {id} ({eventName}, {wav.Length:N0} B)");
                }
                catch (Exception ex)
                {
                    LauncherLog.Write($"audio: sfx {id} error: {ex.Message}");
                    return null;
                }
            }
        }
        return (File.ReadAllBytes(cached), "audio/wav");
    }

    public void WarmupAsync(string preferredBgmId)
    {
        _ = Task.Run(() =>
        {
            try
            {
                GetBgmOgg(preferredBgmId);
            }
            catch (Exception ex) { LauncherLog.Write("audio warmup: " + ex.Message); }
        });
    }

    // ==================== 1. Nintendo Switch sadf/opus (.nop) -> OggOpus ====================

    public static (double DurationSec, double LoopStartSec, double LoopEndSec) ReadNopLoopInfo(string path)
    {
        try
        {
            Span<byte> h = stackalloc byte[0x48];
            using var fs = File.OpenRead(path);
            if (fs.Read(h) < 0x48) return (0, 0, 0);
            if (h[0] != (byte)'s' || h[1] != (byte)'a' || h[2] != (byte)'d' || h[3] != (byte)'f') return (0, 0, 0);
            uint sr = BinaryPrimitives.ReadUInt32LittleEndian(h[0x24..]);
            uint nsamp = BinaryPrimitives.ReadUInt32LittleEndian(h[0x28..]);
            uint lstart = BinaryPrimitives.ReadUInt32LittleEndian(h[0x2C..]);
            uint lend = BinaryPrimitives.ReadUInt32LittleEndian(h[0x30..]);
            if (sr == 0) sr = 48000;
            return ((double)nsamp / sr, (double)lstart / sr, (double)lend / sr);
        }
        catch { return (0, 0, 0); }
    }

    public static byte[] TransmuxNopToOggOpus(byte[] nop)
    {
        if (nop.Length < 0x80 || Encoding.ASCII.GetString(nop, 0, 4) != "sadf")
            throw new InvalidDataException("Not a Nintendo Switch sadf/opus .nop file.");

        int dataOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(nop.AsSpan(0x1C, 4));
        uint nsamp = BinaryPrimitives.ReadUInt32LittleEndian(nop.AsSpan(0x28, 4));

        // Nintendo Switch Standard Opus header (0x80000001) at dataOff
        uint tag = BinaryPrimitives.ReadUInt32LittleEndian(nop.AsSpan(dataOff, 4));
        if (tag != 0x80000001u)
            throw new InvalidDataException($"Unexpected Switch Opus tag 0x{tag:X8}");

        int hsz = (int)BinaryPrimitives.ReadUInt32LittleEndian(nop.AsSpan(dataOff + 4, 4));
        byte channels = nop[dataOff + 9];
        uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(nop.AsSpan(dataOff + 12, 4));
        ushort preSkip = BinaryPrimitives.ReadUInt16LittleEndian(nop.AsSpan(dataOff + 28, 2));

        int dpos = dataOff + 8 + hsz;
        uint dtag = BinaryPrimitives.ReadUInt32LittleEndian(nop.AsSpan(dpos, 4));
        if (dtag != 0x80000004u)
            throw new InvalidDataException($"Unexpected Switch Opus data tag 0x{dtag:X8}");
        int dsz = (int)BinaryPrimitives.ReadUInt32LittleEndian(nop.AsSpan(dpos + 4, 4));

        using var ms = new MemoryStream(nop.Length);
        const uint serial = 0x58433201u; // "XC2\x01"

        // Page 0: OpusHead (BOS = 0x02)
        var opusHead = new byte[19];
        Encoding.ASCII.GetBytes("OpusHead", 0, 8, opusHead, 0);
        opusHead[8] = 1; // version
        opusHead[9] = channels;
        BinaryPrimitives.WriteUInt16LittleEndian(opusHead.AsSpan(10, 2), preSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(opusHead.AsSpan(12, 4), sampleRate);
        BinaryPrimitives.WriteInt16LittleEndian(opusHead.AsSpan(16, 2), 0); // gain
        opusHead[18] = 0; // channel mapping family
        WriteOggPage(ms, [opusHead], 0, serial, 0, 0x02);

        // Page 1: OpusTags
        var vendor = Encoding.UTF8.GetBytes("XC2PCLauncher");
        var opusTags = new byte[8 + 4 + vendor.Length + 4];
        Encoding.ASCII.GetBytes("OpusTags", 0, 8, opusTags, 0);
        BinaryPrimitives.WriteInt32LittleEndian(opusTags.AsSpan(8, 4), vendor.Length);
        Buffer.BlockCopy(vendor, 0, opusTags, 12, vendor.Length);
        BinaryPrimitives.WriteInt32LittleEndian(opusTags.AsSpan(12 + vendor.Length, 4), 0);
        WriteOggPage(ms, [opusTags], 0, serial, 1, 0x00);

        // Read all packets
        var packets = new List<ArraySegment<byte>>(8192);
        int pos = dpos + 8;
        int end = Math.Min(nop.Length, pos + dsz);
        while (pos + 8 <= end)
        {
            int psz = (int)BinaryPrimitives.ReadUInt32BigEndian(nop.AsSpan(pos, 4));
            pos += 8; // skip size + finalRange
            if (psz <= 0 || pos + psz > end) break;
            packets.Add(new ArraySegment<byte>(nop, pos, psz));
            pos += psz;
        }

        uint seq = 2;
        long granule = 0;
        int idx = 0;
        var batch = new List<ArraySegment<byte>>(32);
        while (idx < packets.Count)
        {
            batch.Clear();
            int segCount = 0;
            while (idx < packets.Count && batch.Count < 25)
            {
                int need = packets[idx].Count / 255 + 1;
                if (segCount + need > 255) break;
                batch.Add(packets[idx]);
                segCount += need;
                granule += OpusPacketSamples(packets[idx]);
                idx++;
            }
            bool isLast = idx == packets.Count;
            long pageGranule = isLast ? (long)nsamp + preSkip : granule;
            WriteOggPage(ms, batch, pageGranule, serial, seq++, isLast ? (byte)0x04 : (byte)0x00);
        }

        return ms.ToArray();
    }

    static int OpusPacketSamples(ArraySegment<byte> pkt)
    {
        if (pkt.Count == 0) return 960;
        byte toc = pkt.Array![pkt.Offset];
        int cfg = toc >> 3;
        int frameSamples = cfg switch
        {
            >= 16 and <= 31 => (cfg & 3) switch { 0 => 120, 1 => 240, 2 => 480, _ => 960 }, // CELT
            >= 12 and <= 15 => (cfg & 1) == 0 ? 480 : 960,                                  // Hybrid
            _ => (cfg & 3) switch { 0 => 480, 1 => 960, 2 => 1920, _ => 2880 }              // SILK
        };
        int code = toc & 3;
        int frameCount = code switch
        {
            0 => 1,
            1 or 2 => 2,
            _ => pkt.Count > 1 ? (pkt.Array[pkt.Offset + 1] & 0x3F) : 1
        };
        return frameSamples * Math.Max(1, frameCount);
    }

    static readonly uint[] OggCrcTable = BuildOggCrcTable();
    static uint[] BuildOggCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint r = i << 24;
            for (int j = 0; j < 8; j++)
                r = (r & 0x80000000u) != 0 ? (r << 1) ^ 0x04C11DB7u : r << 1;
            t[i] = r;
        }
        return t;
    }

    static void WriteOggPage(MemoryStream ms, IReadOnlyList<ArraySegment<byte>> packets, long granule, uint serial, uint seq, byte flags)
    {
        var segs = new List<byte>(64);
        int payloadLen = 0;
        for (int i = 0; i < packets.Count; i++)
        {
            int rem = packets[i].Count;
            payloadLen += rem;
            while (rem >= 255) { segs.Add(255); rem -= 255; }
            segs.Add((byte)rem);
        }

        var page = new byte[27 + segs.Count + payloadLen];
        page[0] = (byte)'O'; page[1] = (byte)'g'; page[2] = (byte)'g'; page[3] = (byte)'S';
        page[4] = 0;
        page[5] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6, 8), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14, 4), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18, 4), seq);
        // CRC at 22..26 stays 0 during calculation
        page[26] = (byte)segs.Count;
        for (int i = 0; i < segs.Count; i++) page[27 + i] = segs[i];

        int p = 27 + segs.Count;
        for (int i = 0; i < packets.Count; i++)
        {
            var seg = packets[i];
            Buffer.BlockCopy(seg.Array!, seg.Offset, page, p, seg.Count);
            p += seg.Count;
        }

        uint crc = 0;
        for (int i = 0; i < page.Length; i++)
            crc = (crc << 8) ^ OggCrcTable[((crc >> 24) ^ page[i]) & 0xFF];
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22, 4), crc);
        ms.Write(page, 0, page.Length);
    }

    // ==================== 2. Wwise system.pck (AKPK -> BKHD -> PTADPCM 0x8311 -> PCM WAV) ====================

    static uint Fnv1Lower(string s)
    {
        uint h = 2166136261u;
        foreach (char c in s.ToLowerInvariant())
            h = (h * 16777619u) ^ (byte)c;
        return h;
    }

    public static byte[]? ExtractSystemSfxWav(string systemPckPath, string eventName)
    {
        var data = File.ReadAllBytes(systemPckPath);
        if (data.Length < 64 || Encoding.ASCII.GetString(data, 0, 4) != "AKPK") return null;

        int langSz = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12, 4));
        int offBnk = 28 + langSz;
        int bnkCnt = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offBnk, 4));
        if (bnkCnt < 2) return null;

        uint commonBankId = Fnv1Lower("common");
        int bnkOff = -1, bnkSz = 0;
        for (int i = 0; i < bnkCnt; i++)
        {
            int pos = offBnk + 4 + i * 20;
            uint bid = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
            uint mult = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4, 4));
            uint sz = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 8, 4));
            uint offM = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 12, 4));
            if (bid == commonBankId || i == 1)
            {
                bnkOff = (int)(offM * mult);
                bnkSz = (int)sz;
                if (bid == commonBankId) break;
            }
        }
        if (bnkOff < 0 || bnkOff + bnkSz > data.Length) return null;

        // Parse BKHD chunks: DIDX, DATA, HIRC
        var didx = new Dictionary<uint, (int Off, int Size)>();
        int dataStart = -1;
        int hircOff = -1, hircSz = 0;
        int cur = bnkOff;
        int bnkEnd = bnkOff + bnkSz;
        while (cur + 8 <= bnkEnd)
        {
            var tag = Encoding.ASCII.GetString(data, cur, 4);
            int csz = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cur + 4, 4));
            int body = cur + 8;
            if (body + csz > bnkEnd) break;
            if (tag == "DIDX")
            {
                for (int j = 0; j + 12 <= csz; j += 12)
                {
                    uint wid = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(body + j, 4));
                    int woff = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(body + j + 4, 4));
                    int wsz = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(body + j + 8, 4));
                    didx[wid] = (woff, wsz);
                }
            }
            else if (tag == "DATA") dataStart = body;
            else if (tag == "HIRC") { hircOff = body; hircSz = csz; }
            cur = body + csz;
        }
        if (dataStart < 0 || hircOff < 0) return null;

        // Parse HIRC to resolve eventName -> Action -> Sound -> WemID
        int hcnt = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(hircOff, 4));
        var objs = new Dictionary<uint, (byte Type, int Off, int Len)>(hcnt);
        int hp = hircOff + 4;
        int hend = hircOff + hircSz;
        for (int i = 0; i < hcnt && hp + 9 <= hend; i++)
        {
            byte ot = data[hp];
            int osz = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(hp + 1, 4));
            uint oid = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(hp + 5, 4));
            objs[oid] = (ot, hp + 9, osz - 4);
            hp += 5 + osz;
        }

        uint targetEv = Fnv1Lower(eventName);
        if (!objs.TryGetValue(targetEv, out var evObj) || evObj.Type != 4) return null;
        uint actId = data[evObj.Off] == 1
            ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(evObj.Off + 1, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(evObj.Off + 4, 4));
        if (!objs.TryGetValue(actId, out var actObj)) return null;
        uint sndId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(actObj.Off + 2, 4));
        if (!objs.TryGetValue(sndId, out var sndObj)) return null;
        uint wemId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(sndObj.Off + 5, 4));
        if (!didx.TryGetValue(wemId, out var wemLoc)) return null;

        var wem = data.AsSpan(dataStart + wemLoc.Off, wemLoc.Size);
        return DecodePtAdpcmWemToWav(wem);
    }

    static readonly int[,] PtStep =
    {
        { -14,-10,-7,-5,-3,-2,-1,0,0,1,2,3,5,7,10,14 },
        { -28,-20,-14,-10,-7,-5,-3,-1,1,3,5,7,10,14,20,28 },
        { -56,-40,-28,-20,-14,-10,-6,-2,2,6,10,14,20,28,40,56 },
        { -112,-80,-56,-40,-28,-20,-12,-4,4,12,20,28,40,56,80,112 },
        { -224,-160,-112,-80,-56,-40,-24,-8,8,24,40,56,80,112,160,224 },
        { -448,-320,-224,-160,-112,-80,-48,-16,16,48,80,112,160,224,320,448 },
        { -896,-640,-448,-320,-224,-160,-96,-32,32,96,160,224,320,448,640,896 },
        { -1792,-1280,-896,-640,-448,-320,-192,-64,64,192,320,448,640,896,1280,1792 },
        { -3584,-2560,-1792,-1280,-896,-640,-384,-128,128,384,640,896,1280,1792,2560,3584 },
        { -7168,-5120,-3584,-2560,-1792,-1280,-768,-256,256,768,1280,1792,2560,3584,5120,7168 },
        { -14336,-10240,-7168,-5120,-3584,-2560,-1536,-512,512,1536,2560,3584,5120,7168,10240,14336 },
        { -28672,-20480,-14336,-10240,-7168,-5120,-3072,-1024,1024,3072,5120,7168,10240,14336,20480,28672 },
        { 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0 },
    };

    static readonly int[,] PtNextIdx =
    {
        { 2,2,1,1,0,0,0,0,0,0,0,0,1,1,2,2 },
        { 3,3,2,2,1,1,1,0,0,1,1,1,2,2,3,3 },
        { 4,4,3,3,2,2,2,1,1,2,2,2,3,3,4,4 },
        { 5,5,4,4,3,3,3,2,2,3,3,3,4,4,5,5 },
        { 6,6,5,5,4,4,4,3,3,4,4,4,5,5,6,6 },
        { 7,7,6,6,5,5,5,4,4,5,5,5,6,6,7,7 },
        { 8,8,7,7,6,6,6,5,5,6,6,6,7,7,8,8 },
        { 9,9,8,8,7,7,7,6,6,7,7,7,8,8,9,9 },
        { 10,10,9,9,8,8,8,7,7,8,8,8,9,9,10,10 },
        { 11,11,10,10,9,9,9,8,8,9,9,9,10,10,11,11 },
        { 11,11,11,11,10,10,10,9,9,10,10,10,11,11,11,11 },
        { 11,11,11,11,11,11,11,10,10,11,11,11,11,11,11,11 },
        { 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0 },
    };

    public static byte[]? DecodePtAdpcmWemToWav(ReadOnlySpan<byte> wem)
    {
        if (wem.Length < 44 || Encoding.ASCII.GetString(wem[..4]) != "RIFF") return null;
        ushort fmt = 0, ch = 1, blockAlign = 36;
        uint sr = 32000;
        ReadOnlySpan<byte> raw = default;

        int q = 12;
        while (q + 8 <= wem.Length)
        {
            var tag = Encoding.ASCII.GetString(wem.Slice(q, 4));
            int sz = (int)BinaryPrimitives.ReadUInt32LittleEndian(wem.Slice(q + 4, 4));
            int body = q + 8;
            if (body + sz > wem.Length) break;
            if (tag == "fmt " && sz >= 16)
            {
                fmt = BinaryPrimitives.ReadUInt16LittleEndian(wem.Slice(body, 2));
                ch = BinaryPrimitives.ReadUInt16LittleEndian(wem.Slice(body + 2, 2));
                sr = BinaryPrimitives.ReadUInt32LittleEndian(wem.Slice(body + 4, 4));
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(wem.Slice(body + 12, 2));
            }
            else if (tag == "data")
            {
                raw = wem.Slice(body, sz);
            }
            q = body + sz;
        }
        if (fmt != 0x8311 || raw.Length < blockAlign || ch == 0) return null;

        int framePerCh = blockAlign / ch;
        if (framePerCh < 6) return null;
        int samplesPerFrame = 2 + (framePerCh - 5) * 2;
        int numBlocks = raw.Length / blockAlign;
        int totalSamplesPerCh = numBlocks * samplesPerFrame;
        int pcmBytes = totalSamplesPerCh * ch * 2;

        var wav = new byte[44 + pcmBytes];
        Encoding.ASCII.GetBytes("RIFF", 0, 4, wav, 0);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4, 4), 36 + pcmBytes);
        Encoding.ASCII.GetBytes("WAVEfmt ", 0, 8, wav, 8);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20, 2), 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22, 2), ch);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24, 4), sr);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28, 4), sr * ch * 2u);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32, 2), (ushort)(ch * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34, 2), 16);
        Encoding.ASCII.GetBytes("data", 0, 4, wav, 36);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40, 4), pcmBytes);

        var chBuf = new short[ch, samplesPerFrame];
        int outPos = 44;
        for (int b = 0; b < numBlocks; b++)
        {
            int blkBase = b * blockAlign;
            for (int c = 0; c < ch; c++)
            {
                int off = blkBase + c * framePerCh;
                int h2 = BinaryPrimitives.ReadInt16LittleEndian(raw.Slice(off, 2));
                int h1 = BinaryPrimitives.ReadInt16LittleEndian(raw.Slice(off + 2, 2));
                int idx = Math.Min((int)raw[off + 4], 12);
                chBuf[c, 0] = (short)h2;
                chBuf[c, 1] = (short)h1;
                int s = 2;
                for (int k = 5; k < framePerCh; k++)
                {
                    byte nibs = raw[off + k];
                    int n0 = nibs & 0xF;
                    int v0 = Math.Clamp(PtStep[idx, n0] + 2 * h1 - h2, -32768, 32767);
                    idx = PtNextIdx[idx, n0];
                    chBuf[c, s++] = (short)v0;
                    h2 = h1; h1 = v0;

                    int n1 = (nibs >> 4) & 0xF;
                    int v1 = Math.Clamp(PtStep[idx, n1] + 2 * h1 - h2, -32768, 32767);
                    idx = PtNextIdx[idx, n1];
                    chBuf[c, s++] = (short)v1;
                    h2 = h1; h1 = v1;
                }
            }
            for (int i = 0; i < samplesPerFrame; i++)
            {
                for (int c = 0; c < ch; c++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(outPos, 2), chBuf[c, i]);
                    outPos += 2;
                }
            }
        }
        return wav;
    }
}
