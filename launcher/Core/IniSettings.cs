using System.Text;

namespace XC2.Core;

/// <summary>Comment-preserving, atomic reader/writer for ScarletNX's xenoblade2.ini.</summary>
public sealed class IniSettings
{
    readonly List<Line> lines = new();
    public string FilePath { get; }

    sealed class Line
    {
        public string Raw;
        public string? Section;
        public string? Key;
        public string? Value;
        public Line(string raw, string? section = null, string? key = null, string? value = null)
        {
            Raw = raw; Section = section; Key = key; Value = value;
        }
    }

    IniSettings(string filePath) => FilePath = filePath;

    public static IniSettings Load(string filePath)
    {
        var ini = new IniSettings(filePath);
        if (!File.Exists(filePath)) return ini;

        string currentSection = "";
        foreach (var raw in File.ReadAllLines(filePath, Encoding.UTF8))
        {
            var trimmed = raw.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']') && trimmed.Length >= 2)
            {
                currentSection = trimmed[1..^1].Trim();
                ini.lines.Add(new Line(raw, currentSection));
                continue;
            }
            if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#'))
            {
                ini.lines.Add(new Line(raw, currentSection));
                continue;
            }
            var eq = raw.IndexOf('=');
            if (eq > 0)
            {
                var k = raw[..eq].Trim();
                var v = raw[(eq + 1)..].Trim();
                ini.lines.Add(new Line(raw, currentSection, k, v));
            }
            else
            {
                ini.lines.Add(new Line(raw, currentSection));
            }
        }
        return ini;
    }

    public string Get(string section, string key, string fallback = "")
    {
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var l = lines[i];
            if (l.Key != null &&
                string.Equals(l.Section, section, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return l.Value ?? fallback;
            }
        }
        return fallback;
    }

    public void Set(string section, string key, string value)
    {
        // Update existing key in place
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.Key != null &&
                string.Equals(l.Section, section, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                l.Value = value;
                l.Raw = $"{l.Key}={value}";
                return;
            }
        }

        // Insert at the end of matching section if section exists
        int secIdx = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Key == null && lines[i].Raw.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase))
                secIdx = i;
        }
        if (secIdx >= 0)
        {
            int insertAt = secIdx + 1;
            while (insertAt < lines.Count)
            {
                var t = lines[insertAt].Raw.Trim();
                if (t.StartsWith('[') && t.EndsWith(']')) break;
                insertAt++;
            }
            // Insert before trailing blank lines of the section
            while (insertAt > secIdx + 1 && string.IsNullOrWhiteSpace(lines[insertAt - 1].Raw))
                insertAt--;
            lines.Insert(insertAt, new Line($"{key}={value}", section, key, value));
            return;
        }

        // Append new section
        if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1].Raw))
            lines.Add(new Line(""));
        lines.Add(new Line($"[{section}]", section));
        lines.Add(new Line($"{key}={value}", section, key, value));
    }

    public Dictionary<string, string> ToFlatMap()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines)
        {
            if (l.Key != null)
                d[$"{l.Section}.{l.Key}"] = l.Value ?? "";
        }
        return d;
    }

    public void SaveAtomic()
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        var tmp = FilePath + $".tmp.{Environment.ProcessId}";
        var content = string.Join("\r\n", lines.Select(l => l.Raw)) + "\r\n";
        File.WriteAllText(tmp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
