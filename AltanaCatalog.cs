using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DatViewer;

/// <summary>One selectable row in a Library list (or a "@" section header).</summary>
public sealed class LibEntry
{
    public string Label = "";
    public string RefToken = "";
    public List<string> RomPaths = new(); // ROM-relative, e.g. "ROM/52/13.DAT"; may be empty if unparseable
    public bool IsHeader;
}

/// <summary>Second-level selector under a category — a race (PC), family (NPC), school (Effect)…</summary>
public sealed class LibGroup
{
    public string Name = "";       // display
    public string CsvPath = "";    // non-PC: the list CSV backing this group
    public string FolderPath = ""; // PC: the race folder holding slot CSVs
    public string BaseRef = "";    // PC: base body ref (from index.csv)
    public bool IsPc;
}

/// <summary>A top-level browsable category (PC, NPC, Effect, Zones, Image, Music).</summary>
public sealed class LibCategory
{
    public string Name = "";
    public string GroupLabel = "Group"; // what the second selector is called
    public bool HasSlots;               // PC: each group has slot lists
    public List<LibGroup> Groups = new();
}

/// <summary>
/// Reads AltanaViewer's vendored List/ CSV catalog into a navigable model, and resolves its
/// reference tokens to ROM-relative DAT paths. This is the "menu" data — it turns raw DAT addresses
/// into named, categorised lists (PC → race → slot → item, NPC → family → creature, …).
///
/// Reference-token grammar (decoded from the AltanaViewer lists):
///   "52/13"        → ROM/52/13.DAT            (2-part = base ROM: dir/file)
///   "1/3/108"      → ROM/3/108.DAT            (3-part = romIndex/dir/file; index 1 = base ROM)
///   "9/1/63"       → ROM9/1/63.DAT            (index N>1 = ROMN)
///   "1/5/15-16"    → ROM/5/15.DAT, ROM/5/16.DAT   (range on the last number)
///   "a;b;c"        → each part resolved, concatenated (composite model — several DATs)
/// </summary>
public sealed class AltanaCatalog
{
    public readonly List<LibCategory> Categories = new();

    // Slot files present in a PC race folder, in a sensible display order (Action/Motion are
    // animation tables, not model slots, so they are excluded).
    private static readonly string[] SlotOrder =
        { "Face", "Head", "Body", "Hands", "Legs", "Feet", "Main", "Sub", "Range" };

    public AltanaCatalog(string listRoot)
    {
        if (!Directory.Exists(listRoot)) return;
        AddPc(Path.Combine(listRoot, "PC"));
        AddFileGroups(Path.Combine(listRoot, "NPC"), "NPC", "Family");
        AddFileGroups(Path.Combine(listRoot, "Effect"), "Effect", "School");
        AddFileGroups(Path.Combine(listRoot, "Image"), "Image", "Maps");
        AddFileGroups(Path.Combine(listRoot, "Music"), "Music", "Set");
        AddSingle(Path.Combine(listRoot, "Zones", "zones.csv"), "Zones");
    }

    // ---- structure -----------------------------------------------------------------------------

    private void AddPc(string dir)
    {
        string index = FindFile(dir, "index.csv");
        if (index is null) return;
        var cat = new LibCategory { Name = "PC", GroupLabel = "Race", HasSlots = true };
        foreach (var line in ReadLines(index))
        {
            // folder,label,ref   (label may be empty; ref is always the last field)
            var parts = line.Split(',');
            if (parts.Length < 1) continue;
            string folder = parts[0].Trim();
            string folderPath = Path.Combine(dir, folder);
            if (!Directory.Exists(folderPath)) continue;
            string reff = parts.Length >= 2 ? parts[^1].Trim() : "";
            string label = parts.Length >= 3 ? string.Join(",", parts.Skip(1).Take(parts.Length - 2)).Trim() : "";
            string name = string.IsNullOrEmpty(label) ? folder : label;
            // Tarutaru male (race 5) and female (race 6) share one body model/equipment folder in FFXI, so the
            // catalog ships a single "Tarutaru". Expose both so the female is selectable like the other races.
            if (folder.Equals("Tarutaru", StringComparison.OrdinalIgnoreCase))
            {
                cat.Groups.Add(new LibGroup { Name = "Tarutaru Male",   FolderPath = folderPath, BaseRef = reff, IsPc = true });
                cat.Groups.Add(new LibGroup { Name = "Tarutaru Female", FolderPath = folderPath, BaseRef = reff, IsPc = true });
                continue;
            }
            cat.Groups.Add(new LibGroup { Name = name, FolderPath = folderPath, BaseRef = reff, IsPc = true });
        }
        if (cat.Groups.Count > 0) Categories.Add(cat);
    }

    // A category whose groups are the CSV files named by index.csv (key[,label]).
    private void AddFileGroups(string dir, string categoryName, string groupLabel)
    {
        if (!Directory.Exists(dir)) return;
        var cat = new LibCategory { Name = categoryName, GroupLabel = groupLabel };
        string index = FindFile(dir, "index.csv");
        if (index is not null)
        {
            foreach (var line in ReadLines(index))
            {
                var (key, label) = SplitFirst(line);
                string csv = FindFile(dir, key + ".csv");
                if (csv is null) continue; // index may reference lists that don't ship
                cat.Groups.Add(new LibGroup { Name = string.IsNullOrEmpty(label) ? key : label, CsvPath = csv });
            }
        }
        else
        {
            // No index: just list the CSVs alphabetically.
            foreach (var f in Directory.EnumerateFiles(dir, "*.csv").OrderBy(Path.GetFileName))
                cat.Groups.Add(new LibGroup { Name = Path.GetFileNameWithoutExtension(f), CsvPath = f });
        }
        if (cat.Groups.Count > 0) Categories.Add(cat);
    }

    // A single-file category (Zones) — one group holding the whole (sectioned) list.
    private void AddSingle(string csv, string categoryName)
    {
        if (!File.Exists(csv)) return;
        Categories.Add(new LibCategory
        {
            Name = categoryName, GroupLabel = "Set",
            Groups = { new LibGroup { Name = "All " + categoryName, CsvPath = csv } },
        });
    }

    /// PC slot CSVs present in a race folder, ordered; excludes the Action/Motion animation tables.
    public List<string> SlotsFor(LibGroup g)
    {
        var have = Directory.EnumerateFiles(g.FolderPath, "*.csv")
            .Select(f => Path.GetFileNameWithoutExtension(f) ?? "")
            .Where(n => n.Length > 0 && !n.Equals("Action", StringComparison.OrdinalIgnoreCase)
                                     && !n.Equals("Motion", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ordered = SlotOrder.Where(have.Contains).ToList();
        ordered.AddRange(have.Where(n => !SlotOrder.Contains(n, StringComparer.OrdinalIgnoreCase)).OrderBy(n => n));
        return ordered;
    }

    public List<LibEntry> EntriesForGroup(LibGroup g) => ParseList(g.CsvPath);
    public List<LibEntry> EntriesForPcSlot(LibGroup g, string slot) =>
        ParseList(FindFile(g.FolderPath, slot + ".csv") ?? Path.Combine(g.FolderPath, slot + ".csv"));

    // ---- CSV parsing ---------------------------------------------------------------------------

    /// Parse a list CSV: "ref,label" rows, "@Section" headers, blank lines skipped.
    public static List<LibEntry> ParseList(string absPath)
    {
        var list = new List<LibEntry>();
        if (string.IsNullOrEmpty(absPath) || !File.Exists(absPath)) return list;
        foreach (var raw in File.ReadLines(absPath))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('@'))
            {
                list.Add(new LibEntry { IsHeader = true, Label = PrettyHeader(line[1..]) });
                continue;
            }
            var (reff, label) = SplitFirst(line);
            if (reff.Length == 0) continue;
            list.Add(new LibEntry
            {
                RefToken = reff,
                Label = string.IsNullOrEmpty(label) ? reff : label,
                RomPaths = ResolveRefPaths(reff),
            });
        }
        return list;
    }

    /// Resolve a reference token to the list of ROM-relative paths it names (see grammar above).
    public static List<string> ResolveRefPaths(string token)
    {
        var paths = new List<string>();
        foreach (var part in token.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var segs = part.Split('/');
            if (segs.Length is < 2 or > 3) continue;

            int romIdx = 1, dir; string fileSpec;
            if (segs.Length == 3)
            {
                if (!int.TryParse(segs[0], out romIdx)) continue;
                if (!int.TryParse(segs[1], out dir)) continue;
                fileSpec = segs[2];
            }
            else
            {
                if (!int.TryParse(segs[0], out dir)) continue;
                fileSpec = segs[1];
            }

            string romDir = romIdx <= 1 ? "ROM" : "ROM" + romIdx;
            foreach (int file in ExpandRange(fileSpec))
                paths.Add($"{romDir}/{dir}/{file}.DAT");
        }
        return paths;
    }

    // "15-16" → 15,16 ; "13" → 13
    private static IEnumerable<int> ExpandRange(string spec)
    {
        int dash = spec.IndexOf('-');
        if (dash < 0)
        {
            if (int.TryParse(spec, out var one)) yield return one;
            yield break;
        }
        if (int.TryParse(spec[..dash], out var lo) && int.TryParse(spec[(dash + 1)..], out var hi) && hi >= lo)
            for (int i = lo; i <= hi; i++) yield return i;
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static (string key, string rest) SplitFirst(string line)
    {
        int c = line.IndexOf(',');
        return c < 0 ? (line.Trim(), "") : (line[..c].Trim(), line[(c + 1)..].Trim());
    }

    private static IEnumerable<string> ReadLines(string path) =>
        File.ReadLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('@'));

    // Case-insensitive file lookup (the lists mix "Automaton.csv" with lower-case index keys).
    private static string? FindFile(string dir, string name)
    {
        if (!Directory.Exists(dir)) return null;
        string exact = Path.Combine(dir, name);
        if (File.Exists(exact)) return exact;
        return Directory.EnumerateFiles(dir)
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
    }

    // "zones_base_game (XIData Recovered)" → "Base Game"
    private static string PrettyHeader(string h)
    {
        int paren = h.IndexOf('(');
        if (paren >= 0) h = h[..paren];
        h = h.Trim();
        if (h.StartsWith("zones_", StringComparison.OrdinalIgnoreCase)) h = h[6..];
        h = h.Replace('_', ' ').Trim();
        if (h.Length == 0) return "—";
        return string.Join(' ', h.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + (w.Length > 1 ? w[1..] : "")));
    }
}
