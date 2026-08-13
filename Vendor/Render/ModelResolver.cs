using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using XiHeadless.Game;

namespace Vellichor.Render;

/// <summary>
/// Turns an entity's network "look" into ROM DAT paths for its model, using the community-harvested
/// FFXI model tables (Vanalytics/AltanaView, MIT data under data/models/): per-(race,slot) equipment
/// model files, per-race face files, and a name-keyed monster/NPC model list. There is no clean
/// arithmetic formula for the general case (expansion gear/monsters are scattered), so these tables are
/// the source of truth; file-id→path is otherwise handled by DatArchive/FTABLE.
///
/// - Monster / simple NPC (look type 0): one self-contained DAT (embedded skeleton+anim).
/// - PC / humanoid NPC (look type 1): a race SKELETON DAT + per-slot equipment/face mesh DATs that bind
///   to the shared skeleton (see <see cref="PcRecipe"/>).
/// Paths validated against this corpus (they matched the shipped tables exactly).
/// </summary>
public sealed class ModelResolver
{
    private readonly string _corpus;
    private Dictionary<string, Dictionary<int, string>>? _model;    // "race:slot" -> modelId -> ROM path
    private Dictionary<int, List<string>>? _face;                   // race -> face index -> ROM path
    private Dictionary<string, string>? _npcByName;                 // monster/NPC name -> ROM path

    // Per-race skeleton DATs (Vanalytics SKELETON_PATHS; race 6 Taru♀ reuses race 5).
    private static readonly Dictionary<int, string> Skeleton = new()
    {
        [1] = "ROM/27/82.dat", [2] = "ROM/32/58.dat", [3] = "ROM/37/31.dat", [4] = "ROM/42/4.dat",
        [5] = "ROM/46/93.dat", [6] = "ROM/46/93.dat", [7] = "ROM/51/89.dat", [8] = "ROM/56/59.dat",
    };

    // Wire equipment slots -> Vanalytics slotId (0=Race,1=Face reserved). Visible armor = head..feet.
    private const int SlotHead = 2, SlotBody = 3, SlotHands = 4, SlotLegs = 5, SlotFeet = 6, SlotMain = 7, SlotSub = 8, SlotRanged = 9;

    public ModelResolver(string corpusDir, string dataDir)
    {
        _corpus = corpusDir;
        try
        {
            _model = LoadModelPaths(Path.Combine(dataDir, "model-dat-paths.json"));
            _face = LoadFacePaths(Path.Combine(dataDir, "face-paths.json"));
            _npcByName = LoadNpcPaths(Path.Combine(dataDir, "npc-model-paths.json"));
        }
        catch { /* tables missing -> resolver yields nothing, renderer uses capsules */ }
    }

    public bool Ready => _model is not null;

    /// Absolute corpus path for a ROM-relative "ROM/dir/file.dat" (normalizing case/separators), or null
    /// if the file isn't present.
    private string? Abs(string? romPath)
    {
        if (string.IsNullOrEmpty(romPath)) return null;
        var rel = romPath.Replace('/', Path.DirectorySeparatorChar);
        if (rel.EndsWith(".dat")) rel = rel[..^4] + ".DAT";
        var full = Path.Combine(_corpus, rel);
        return File.Exists(full) ? full : null;
    }

    /// Monster / simple NPC (look type 0): resolve by entity name via the harvested NPC list. Returns the
    /// self-contained model DAT path, or null.
    public string? MonsterPath(string name)
    {
        if (_npcByName is null || string.IsNullOrEmpty(name)) return null;
        if (_npcByName.TryGetValue(Norm(name), out var p)) return Abs(p);
        return null;
    }

    /// PC / humanoid NPC (look type 1): the race skeleton DAT + the ordered list of part DATs (face +
    /// visible armor slots + weapons), each resolved to an absolute path. Unequipped visible armor uses
    /// model 0 (the naked body part). Returns null if the race/skeleton is unknown.
    public (string skeleton, List<string> parts)? PcRecipe(in EntityLook look)
    {
        if (_model is null || !Skeleton.TryGetValue(look.Race, out var skelRel)) return null;
        var skel = Abs(skelRel);
        if (skel is null) return null;

        int race = look.Race == 6 ? 6 : look.Race; // race 6 uses race-5 model data but its own key exists
        var parts = new List<string>();
        void Add(string? p) { if (p is not null) parts.Add(p); }

        // Face (index by the Face byte; table is 1-based F1A.. so clamp into range).
        if (_face is not null && _face.TryGetValue(look.Race, out var faces) && faces.Count > 0)
            Add(Abs(faces[System.Math.Clamp(look.Face, 0, faces.Count - 1)]));

        // The wire equipment ids carry a slot tag in the high nibble (head+0x1000 … ranged+0x8000, from the
        // server's GrapIDTbl); mask it off (& 0x0FFF) to get the actual model id the tables are keyed by.
        Add(EquipPath(race, SlotHead, look.Head & 0x0FFF));
        Add(EquipPath(race, SlotBody, look.Body & 0x0FFF));
        Add(EquipPath(race, SlotHands, look.Hands & 0x0FFF));
        Add(EquipPath(race, SlotLegs, look.Legs & 0x0FFF));
        Add(EquipPath(race, SlotFeet, look.Feet & 0x0FFF));
        Add(EquipPath(race, SlotMain, look.Main & 0x0FFF));
        Add(EquipPath(race, SlotSub, look.Sub & 0x0FFF));
        Add(EquipPath(race, SlotRanged, look.Ranged & 0x0FFF));

        return parts.Count > 0 ? (skel, parts) : null;
    }

    /// The NAKED base body of a race as labeled (slot → DAT path) parts + the skeleton DAT — so a tool
    /// (the DAT viewer) can swap ONE slot for an arbitrary equipment DAT and preview it worn on a real
    /// body. Slots in draw order: face, head, body, hands, legs, feet. Null if the race is unknown.
    public (string skeleton, List<(string slot, string path)> parts)? PcBaseParts(int race, int face)
    {
        if (_model is null || !Skeleton.TryGetValue(race, out var skelRel)) return null;
        var skel = Abs(skelRel);
        if (skel is null) return null;
        var parts = new List<(string, string)>();
        void Add(string slot, string? p) { if (p is not null) parts.Add((slot, p)); }
        if (_face is not null && _face.TryGetValue(race, out var faces) && faces.Count > 0)
            Add("face", Abs(faces[System.Math.Clamp(face, 0, faces.Count - 1)]));
        Add("head", EquipPath(race, SlotHead, 0));
        Add("body", EquipPath(race, SlotBody, 0));
        Add("hands", EquipPath(race, SlotHands, 0));
        Add("legs", EquipPath(race, SlotLegs, 0));
        Add("feet", EquipPath(race, SlotFeet, 0));
        return parts.Count > 0 ? (skel, parts) : null;
    }

    /// Resolve one (race, slot, modelId) equipment mesh; visible armor falls back to model 0.
    private string? EquipPath(int race, int slot, int modelId)
    {
        if (_model is null) return null;
        string key = $"{race}:{slot}";
        Dictionary<int, string>? table = _model.GetValueOrDefault(key);
        if (table is null && slot == SlotSub) table = _model.GetValueOrDefault($"{race}:{SlotMain}"); // sub reuses main
        if (table is null) return null;
        if (table.TryGetValue(modelId, out var p)) return Abs(p);
        // visible armor: default to the naked model 0 when the specific id is missing
        bool visibleArmor = slot is SlotHead or SlotBody or SlotHands or SlotLegs or SlotFeet;
        if (visibleArmor && modelId != 0 && table.TryGetValue(0, out var p0)) return Abs(p0);
        return null;
    }

    private static string Norm(string s) => s.Trim().ToLowerInvariant();

    // ---- table loaders -----------------------------------------------------
    private static Dictionary<string, Dictionary<int, string>> LoadModelPaths(string path)
    {
        var result = new Dictionary<string, Dictionary<int, string>>();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var raceSlot in doc.RootElement.EnumerateObject())
        {
            var inner = new Dictionary<int, string>();
            foreach (var kv in raceSlot.Value.EnumerateObject())
                if (int.TryParse(kv.Name, out var id) && kv.Value.GetString() is { } s) inner[id] = s;
            result[raceSlot.Name] = inner;
        }
        return result;
    }

    private static Dictionary<int, List<string>> LoadFacePaths(string path)
    {
        var result = new Dictionary<int, List<string>>();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var race in doc.RootElement.EnumerateObject())
        {
            if (!int.TryParse(race.Name, out var r)) continue;
            var list = new List<string>();
            foreach (var e in race.Value.EnumerateArray())
                if (e.TryGetProperty("path", out var p) && p.GetString() is { } s) list.Add(s);
            result[r] = list;
        }
        return result;
    }

    private static Dictionary<string, string> LoadNpcPaths(string path)
    {
        var result = new Dictionary<string, string>();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var e in doc.RootElement.EnumerateArray())
            if (e.TryGetProperty("name", out var n) && e.TryGetProperty("path", out var p)
                && n.GetString() is { } name && p.GetString() is { } pth)
                result[Norm(name)] = pth;
        return result;
    }
}
