namespace Vellichor.Dat;

/// <summary>
/// Resolves a numeric file id to its on-disk <c>.DAT</c> path via the client's
/// VTABLE/FTABLE index, and reads file bytes. Pure C#, no Godot dependency, so it
/// can be exercised headless against a real install (see tools/probe).
///
/// Layout verified against a real install (see docs/dat-format.md §1–2):
///   * Base ROM set: tables live at the install ROOT (VTABLE.DAT / FTABLE.DAT);
///     files at ROM/&lt;dir&gt;/&lt;file&gt;.DAT.
///   * ROM2..ROMn: tables live INSIDE the folder (ROM2/VTABLE2.DAT ...);
///     files at ROM2/&lt;dir&gt;/&lt;file&gt;.DAT.
/// (The commonly-cited reference resolver puts the base tables inside ROM/, which is
///  wrong for the base set — hence this class special-cases the table directory.)
///
/// Resolution per set: VTABLE[fileId] (1 byte) == set index → this set owns the id.
/// FTABLE[fileId] (uint16 LE) packs dir/file: dir = value &gt;&gt; 7, file = value &amp; 0x7F.
/// </summary>
public sealed class DatArchive
{
    private sealed record RomSet(int Index, string Folder, byte[] VTable, byte[] FTable);

    private readonly List<RomSet> _sets = new();

    /// <summary>ROM set indices that were actually present and loaded.</summary>
    public IReadOnlyList<int> LoadedSets => _sets.Select(s => s.Index).ToList();

    /// <summary>
    /// Mod overlay roots, checked before the base install (XIPivot-compatible). Each overlay
    /// mirrors the install's <c>ROM[n]/&lt;dir&gt;/&lt;file&gt;.DAT</c> layout; a matching file there
    /// wins. This gives native support for the same texture/model mod packs XIPivot loads,
    /// without running XIPivot. Highest priority first.
    /// </summary>
    private readonly string[] _overlays;

    public DatArchive(string installRoot, params string[] overlayRoots)
    {
        _overlays = overlayRoots ?? Array.Empty<string>();
        // Tables are small (~100–215 KB each) — load once so resolution is in-memory.
        // This matters over a network/SMB mount, where reopening files per lookup is slow.
        for (int i = 1; i < 20; i++)
        {
            string suffix = i == 1 ? "" : i.ToString();
            string folder = Path.Combine(installRoot, "ROM" + suffix);
            string tableDir = i == 1 ? installRoot : folder; // base tables at root
            string vPath = Path.Combine(tableDir, $"VTABLE{suffix}.DAT");
            string fPath = Path.Combine(tableDir, $"FTABLE{suffix}.DAT");
            if (!File.Exists(vPath) || !File.Exists(fPath) || !Directory.Exists(folder))
                continue;
            _sets.Add(new RomSet(i, folder, File.ReadAllBytes(vPath), File.ReadAllBytes(fPath)));
        }
    }

    /// <summary>Absolute path to the .DAT for <paramref name="fileId"/>, or null if unmapped.</summary>
    public string? ResolveFileId(int fileId)
    {
        if (fileId < 0) return null;
        foreach (var s in _sets)
        {
            if (fileId >= s.VTable.Length || s.VTable[fileId] != s.Index) continue;
            int off = 2 * fileId;
            if (off + 1 >= s.FTable.Length) continue;
            ushort fileDir = (ushort)(s.FTable[off] | (s.FTable[off + 1] << 8));
            int dir = fileDir >> 7;
            int file = fileDir & 0x7F;

            // XIPivot-style overlay: same ROM-relative path under any overlay root wins.
            string rel = Path.Combine(Path.GetFileName(s.Folder), dir.ToString(), $"{file}.DAT");
            foreach (var ov in _overlays)
            {
                string op = Path.Combine(ov, rel);
                if (File.Exists(op)) return op;
            }
            return Path.Combine(s.Folder, dir.ToString(), $"{file}.DAT");
        }
        return null;
    }

    /// <summary>Highest file id any loaded VTABLE could describe (for a full scan).</summary>
    public int MaxFileId => _sets.Count == 0 ? -1 : _sets.Max(s => s.VTable.Length) - 1;

    /// <summary>
    /// Every mapped (file id → absolute .DAT path) pair across all ROM sets. Lets a tool build the
    /// reverse map (path → id) so a browsed file can show its numeric id, and vice-versa. Skips the
    /// overlay check (returns base-install paths) so the map is stable.
    /// </summary>
    public IEnumerable<(int id, string path)> EnumerateAll()
    {
        foreach (var s in _sets)
        {
            int max = s.VTable.Length;
            for (int id = 0; id < max; id++)
            {
                if (s.VTable[id] != s.Index) continue;
                int off = 2 * id;
                if (off + 1 >= s.FTable.Length) continue;
                ushort fileDir = (ushort)(s.FTable[off] | (s.FTable[off + 1] << 8));
                yield return (id, Path.Combine(s.Folder, (fileDir >> 7).ToString(), $"{fileDir & 0x7F}.DAT"));
            }
        }
    }

    /// <summary>Reads the raw bytes for a file id. Throws if the id is unmapped or missing.</summary>
    public byte[] ReadFileId(int fileId)
    {
        string path = ResolveFileId(fileId)
            ?? throw new FileNotFoundException($"file id {fileId} is not mapped by any VTABLE.");
        return File.ReadAllBytes(path);
    }
}
