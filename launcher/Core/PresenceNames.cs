using System.Reflection;

namespace XC2.Core;

public sealed class PresenceNames
{
    static readonly Lazy<PresenceNames> Instance = new(Load);
    public static PresenceNames Current => Instance.Value;

    readonly Dictionary<(char Kind, uint Id), (string Pt, string En, string PtJp)> map = new();

    static PresenceNames Load()
    {
        var table = new PresenceNames();
        var asm = typeof(PresenceNames).Assembly;
        using var stream = asm.GetManifestResourceStream("XC2.Core.Data.presence_names.tsv");
        if (stream == null) return table;
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length < 4) continue;
            var parts = line.Split('\t');
            if (parts.Length < 4 || parts[0].Length != 1) continue;
            if (!uint.TryParse(parts[1], out var id)) continue;
            var pt = parts[2].Trim();
            var en = parts[3].Trim();
            var jp = parts.Length >= 5 && !string.IsNullOrWhiteSpace(parts[4]) ? parts[4].Trim() : pt;
            table.map[(parts[0][0], id)] = (pt, en, jp);
        }
        return table;
    }

    public string? Get(char kind, uint id, bool portuguese = true, bool jpNames = false)
    {
        if (!map.TryGetValue((kind, id), out var entry)) return null;
        var preferred = portuguese ? (jpNames ? entry.PtJp : entry.Pt) : entry.En;
        if (!string.IsNullOrWhiteSpace(preferred)) return preferred;
        return portuguese ? entry.En : entry.Pt;
    }

    public string Region(uint mapJumpId, bool pt = true, bool jpNames = false) =>
        Get('R', mapJumpId, pt, jpNames) ?? Get('M', mapJumpId, pt, jpNames) ?? (jpNames ? $"Alst (#{mapJumpId})" : $"Alrest (#{mapJumpId})");

    public string Landmark(uint mapJumpId, bool pt = true, bool jpNames = false) =>
        Get('L', mapJumpId, pt, jpNames) ?? Get('N', mapJumpId, pt, jpNames) ?? Region(mapJumpId, pt, jpNames);

    public string Driver(uint driverId, bool pt = true, bool jpNames = false) =>
        Get('D', driverId, pt, jpNames) ?? $"Driver #{driverId}";

    public string Blade(uint bladeId, bool pt = true, bool jpNames = false)
    {
        if (bladeId == 0) return "";
        if (bladeId == 1113) return "MOMO";
        if (bladeId == 1114) return "MOMO (i)";
        var found = Get('B', bladeId, pt, jpNames);
        if (found != null) return found;
        return bladeId >= 1200 ? (pt ? "Blade Comum" : "Common Blade") : $"Blade #{bladeId}";
    }
}
