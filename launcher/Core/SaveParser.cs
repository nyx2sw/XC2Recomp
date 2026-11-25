using System.Buffers.Binary;
using System.Security.Cryptography;

namespace XC2.Core;

public sealed record ParsedSaveInfo(
    string Path,
    string FileName,
    long SizeBytes,
    string Sha256,
    DateTime LastWriteLocal,
    bool IsValid,
    string Kind, // "bf2" | "torna" | "unknown"
    uint MapJumpId,
    string RegionName,
    string LandmarkName,
    string LeaderName,
    int LeaderLevel,
    string ActiveBladeName,
    bool IsMythraActive,
    string[] EngagedBlades,
    int PlayHours,
    int PlayMinutes,
    int PlaySeconds,
    uint Gold,
    string PlayTimeFormatted,
    string SummaryLine
);

public static class SaveParser
{
    public const uint Bf2Magic = 0x0003A15B;
    public const int Bf2MinSize = 0x10AE90;

    const int OffMoney = 0x10;
    const int OffMapJump = 0x14;
    const int OffDrivers = 0x3C;
    const int DriverSize = 0x5A0;
    const int DriverOffId = 0x22;
    const int DriverOffSetBlade = 0x24;
    const int DriverOffBlades = 0x26;
    const int DriverOffLevel = 0x94;
    const int OffPartyLeader = 0xE9894 + 0x40;
    const int OffPlayTime = 0x1098D4;
    const int OffHikari = 0x10AE8D;
    const ushort BladePyra = 1001;
    const ushort BladeMythra = 1002;

    public static ParsedSaveInfo ParseFile(string filePath, bool portuguese = true, bool jpNames = false)
    {
        var fi = new FileInfo(filePath);
        if (!fi.Exists)
            return Invalid(filePath, "Arquivo não encontrado");

        byte[] data;
        try { data = File.ReadAllBytes(filePath); }
        catch (Exception ex) { return Invalid(filePath, ex.Message); }

        var sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var fileName = fi.Name;
        var isTornaName = fileName.Contains("ira", StringComparison.OrdinalIgnoreCase);

        if (data.Length < Bf2MinSize || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4)) != Bf2Magic)
        {
            var kind = isTornaName ? "torna" : "unknown";
            return new ParsedSaveInfo(
                filePath, fileName, data.Length, sha, fi.LastWriteTime,
                IsValid: isTornaName && data.Length > 1024,
                Kind: kind,
                MapJumpId: 0,
                RegionName: isTornaName ? (jpNames ? "Ira — O País Dourado" : "Torna — The Golden Country") : "Save desconhecido",
                LandmarkName: isTornaName ? (jpNames ? "Progresso de Ira" : "Progresso de Torna") : fileName,
                LeaderName: isTornaName ? "Lora" : "",
                LeaderLevel: 0,
                ActiveBladeName: isTornaName ? (jpNames ? "Shin" : "Jin") : "",
                IsMythraActive: false,
                EngagedBlades: Array.Empty<string>(),
                PlayHours: 0, PlayMinutes: 0, PlaySeconds: 0, Gold: 0,
                PlayTimeFormatted: "--:--",
                SummaryLine: $"{data.Length / 1024:N0} KiB"
            );
        }

        var names = PresenceNames.Current;
        var gold = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(OffMoney, 4));
        var mapJump = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(OffMapJump, 4));
        var playPacked = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(OffPlayTime, 4));
        var hours = (int)(playPacked >> 12);
        var minutes = (int)((playPacked >> 6) & 0x3F);
        var seconds = (int)(playPacked & 0x3F);
        var hikari = data[OffHikari] != 0;

        var leaderDriverId = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(OffPartyLeader, 2));
        if (leaderDriverId == 0 || leaderDriverId > 16) leaderDriverId = 1;

        int leaderLevel = 0;
        ushort activeBladeId = 0;
        var engaged = new List<string>();

        for (int i = 0; i < 16; i++)
        {
            int drvBase = OffDrivers + i * DriverSize;
            if (drvBase + DriverSize > data.Length) break;
            var id = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(drvBase + DriverOffId, 2));
            if (id != leaderDriverId) continue;

            leaderLevel = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(drvBase + DriverOffLevel, 2));
            var setBlade = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(drvBase + DriverOffSetBlade, 2));
            var b0 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(drvBase + DriverOffBlades, 2));
            var b1 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(drvBase + DriverOffBlades + 2, 2));
            var b2 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(drvBase + DriverOffBlades + 4, 2));
            ushort[] slots = [b0, b1, b2];

            foreach (var bid in slots)
            {
                if (bid == 0) continue;
                var resolved = (bid == BladePyra && hikari) ? BladeMythra : bid;
                var bname = names.Blade(resolved, portuguese, jpNames);
                if (!string.IsNullOrEmpty(bname)) engaged.Add(bname);
            }

            if (setBlade < 3 && slots[setBlade] != 0)
                activeBladeId = slots[setBlade];
            else
                activeBladeId = slots.FirstOrDefault(x => x != 0);

            if (activeBladeId == BladePyra && hikari)
                activeBladeId = BladeMythra;
            break;
        }

        var region = names.Region(mapJump, portuguese, jpNames);
        var landmark = names.Landmark(mapJump, portuguese, jpNames);
        var leaderName = names.Driver(leaderDriverId, portuguese, jpNames);
        var bladeName = activeBladeId != 0 ? names.Blade(activeBladeId, portuguese, jpNames) : "";
        var playFmt = $"{hours}h {minutes:D2}m";
        var summary = string.IsNullOrEmpty(bladeName)
            ? $"{leaderName} · Nv. {leaderLevel} · {playFmt}"
            : $"{leaderName} & {bladeName} · Nv. {leaderLevel} · {playFmt}";

        return new ParsedSaveInfo(
            Path: filePath,
            FileName: fileName,
            SizeBytes: data.Length,
            Sha256: sha,
            LastWriteLocal: fi.LastWriteTime,
            IsValid: true,
            Kind: "bf2",
            MapJumpId: mapJump,
            RegionName: region,
            LandmarkName: landmark,
            LeaderName: leaderName,
            LeaderLevel: leaderLevel,
            ActiveBladeName: bladeName,
            IsMythraActive: hikari && (activeBladeId == BladeMythra),
            EngagedBlades: engaged.ToArray(),
            PlayHours: hours,
            PlayMinutes: minutes,
            PlaySeconds: seconds,
            Gold: gold,
            PlayTimeFormatted: playFmt,
            SummaryLine: summary
        );
    }

    static ParsedSaveInfo Invalid(string path, string reason) => new(
        path, System.IO.Path.GetFileName(path), 0, "", DateTime.MinValue,
        false, "unknown", 0, "", reason, "", 0, "", false, Array.Empty<string>(),
        0, 0, 0, 0, "--:--", reason
    );
}
