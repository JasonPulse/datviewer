using System;
using System.Collections.Generic;
using Godot;
using Vellichor.Dat;

namespace Vellichor.Render;

/// <summary>
/// A decoded FFXI character/creature model (0x29 skeleton + 0x2a skinned meshes + 0x2b animation clips)
/// held in engine-ready but INSTANCE-FREE form, so one decode can spawn many live entities. Decoding is
/// expensive and shared; nodes are not, so <see cref="BuildInstance"/> mints a fresh Skeleton3D + skinned
/// MeshInstance3D tree each call (materials/textures are Godot resources and ARE shared). Animation clips
/// convert to skeleton-independent <see cref="AnimationDriver.Track"/>s (the driver reads the rest pose
/// from whichever skeleton it drives), so they're shared too.
///
/// This is the reusable core behind ModelViewer.BuildAnimatedCharacter and the live EntityRenderer.
/// </summary>
public sealed class CharacterModel
{
    private readonly MeshData[] _meshes;
    private readonly Material[] _mat;                 // parallel to _meshes
    private readonly ModelDecoder.SkelBone[] _bones;
    private readonly Dictionary<string, byte[]> _clipPayloads;   // clip name -> raw 0x2b payload (lazy-decoded)
    private readonly Dictionary<string, AnimationDriver.Track[]> _clipCache = new();

    public int BoneCount => _bones.Length;
    public IReadOnlyCollection<string> ClipNames => _clipPayloads.Keys;

    private CharacterModel(MeshData[] meshes, Material[] mat, ModelDecoder.SkelBone[] bones, Dictionary<string, byte[]> clips)
    { _meshes = meshes; _mat = mat; _bones = bones; _clipPayloads = clips; }

    /// Decode a SELF-CONTAINED character DAT (monster/creature: 0x29 skeleton + 0x2a mesh + 0x2b anim in
    /// one file). Returns null if it has no skinned mesh + skeleton.
    public static CharacterModel? Decode(byte[] dat)
    {
        var chunks = ChunkReader.Walk(dat);
        ModelDecoder.Skeleton? sk = null;
        var clips = new Dictionary<string, byte[]>();
        foreach (var c in chunks)
        {
            if (c.Type == 0x29 && sk is null) sk = ModelDecoder.DecodeSkeleton(dat[c.PayloadOffset..(c.PayloadOffset + c.PayloadLength)]);
            else if (c.Type == 0x2b && !clips.ContainsKey(c.Name)) clips[c.Name] = dat[c.PayloadOffset..(c.PayloadOffset + c.PayloadLength)];
        }
        if (sk is null) return null;
        var meshes = ModelDecoder.DecodeCharacterMeshes(dat);
        return Build(meshes, new[] { dat }, sk.Bones, clips);
    }

    /// Assemble a PC / humanoid-NPC model: a race SKELETON DAT (0x29 + base 0x2b anims, no mesh) plus one
    /// or more PART DATs (equipment/face; 0x2a mesh only) whose meshes bind to the shared race skeleton.
    /// Extra animation clips (walk/run from separate race anim DATs) can be merged via <paramref name="clipDats"/>.
    public static CharacterModel? DecodeAssembled(byte[] skeletonDat, IReadOnlyList<byte[]> partDats, IReadOnlyList<byte[]>? clipDats = null)
    {
        ModelDecoder.Skeleton? sk = null;
        var clips = new Dictionary<string, byte[]>();
        void GatherClips(byte[] d)
        {
            foreach (var c in ChunkReader.Walk(d))
            {
                if (c.Type == 0x29 && sk is null) sk = ModelDecoder.DecodeSkeleton(d[c.PayloadOffset..(c.PayloadOffset + c.PayloadLength)]);
                else if (c.Type == 0x2b && !clips.ContainsKey(c.Name)) clips[c.Name] = d[c.PayloadOffset..(c.PayloadOffset + c.PayloadLength)];
            }
        }
        GatherClips(skeletonDat);
        if (clipDats is not null) foreach (var d in clipDats) GatherClips(d);
        if (sk is null) return null;

        var meshes = new List<MeshData>();
        var diag = new System.Text.StringBuilder();
        for (int pi = 0; pi < partDats.Count; pi++)
        {
            int before = meshes.Count, tris = 0;
            try { var pm = ModelDecoder.DecodeMeshesWithSkeleton(partDats[pi], sk); meshes.AddRange(pm); foreach (var m in pm) tris += m.TriangleCount; }
            catch (Exception e) { diag.Append($"\n  part{pi}: EXCEPTION {e.GetType().Name} ({ModelDecoder.MeshDiag.Trim()})"); continue; }
            diag.Append($"\n  part{pi}: {meshes.Count - before} mesh(es), {tris} tris  [{ModelDecoder.MeshDiag.Trim()}]");
        }
        ModelDecoder.MeshDiag = $"ASSEMBLY {partDats.Count} parts:" + diag; // so the viewer prints EVERY part, not just the last
        if (meshes.Count == 0) return null;

        return Build(meshes, partDats, sk.Bones, clips);
    }

    /// Shared: build per-mesh materials from the 0x20 textures across the given source DATs, and package.
    private static CharacterModel Build(List<MeshData> meshes, IReadOnlyList<byte[]> textureDats,
                                        ModelDecoder.SkelBone[] bones, Dictionary<string, byte[]> clips)
    {
        var texMat = new Dictionary<string, StandardMaterial3D>();
        foreach (var dat in textureDats)
            foreach (var c in ChunkReader.Walk(dat))
            {
                if (c.Type != 0x20) continue;
                ImgTexture? img;
                try { img = ImgDecoder.Decode(dat[c.PayloadOffset..(c.PayloadOffset + c.PayloadLength)]); } catch { continue; }
                if (img is null || texMat.ContainsKey(img.Id)) continue;
                var gi = Image.CreateFromData(img.Width, img.Height, false, Image.Format.Rgba8, img.Rgba);
                gi.GenerateMipmaps();
                texMat[img.Id] = new StandardMaterial3D
                {
                    AlbedoTexture = ImageTexture.CreateFromImage(gi),
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
                    // FFXI textures are pre-lit; shading them (with weak/absent ambient) is what made models
                    // muddy-dark and painted inward-facing faces solid black. Unshaded = true texture color.
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                };
            }

        var fallback = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.82f, 0.78f, 0.72f),
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        // Mesh TextureId and IMG Id are BOTH the 16-byte ASCII texture name — match on the normalized name
        // (exact first, then substring) instead of the old brittle last-4-chars heuristic that dropped most
        // equipment meshes to the grey fallback (the untextured dark tunic).
        Material MatFor(string? tex)
        {
            string key = Norm(tex);
            if (key.Length > 0)
            {
                foreach (var (id, m) in texMat) if (Norm(id) == key) return m;
                if (key.Length >= 3)
                    foreach (var (id, m) in texMat)
                    { var nid = Norm(id); if (nid.Length >= 3 && (nid.Contains(key) || key.Contains(nid))) return m; }
            }
            return fallback;
        }
        var mats = new Material[meshes.Count];
        for (int i = 0; i < meshes.Count; i++) mats[i] = MatFor(meshes[i].TextureId);

        return new CharacterModel(meshes.ToArray(), mats, bones, clips);
    }

    /// Mint a fresh scene instance: a root carrying the FFXI->Godot display flip (180° about Z), a
    /// Skeleton3D with FFXI-local rests, and one skinned MeshInstance3D per mesh. Returns the root to add
    /// to the scene and the skeleton to drive.
    public (Node3D root, Skeleton3D skel, Aabb bounds) BuildInstance()
    {
        var s = new Skeleton3D();
        for (int i = 0; i < _bones.Length; i++) s.AddBone($"b{i}");
        for (int i = 0; i < _bones.Length; i++)
        {
            var b = _bones[i];
            if (b.Parent >= 0 && b.Parent < i) s.SetBoneParent(i, b.Parent);
            var q = new Quaternion(b.Qx, b.Qy, b.Qz, b.Qw);
            bool bad = q.LengthSquared() is < 0.9f or > 1.1f
                       || Mathf.Abs(b.Tx) > 20 || Mathf.Abs(b.Ty) > 20 || Mathf.Abs(b.Tz) > 20;
            var rest = bad ? Transform3D.Identity
                           : new Transform3D(new Basis(q.Normalized()), new Vector3(b.Tx, b.Ty, b.Tz));
            s.SetBoneRest(i, rest);
            s.SetBonePosePosition(i, rest.Origin);
            s.SetBonePoseRotation(i, rest.Basis.GetRotationQuaternion());
        }

        var root = new Node3D { Transform = new Transform3D(new Basis(new Vector3(0, 0, 1), Mathf.Pi), Vector3.Zero) };
        root.AddChild(s);

        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (int mi = 0; mi < _meshes.Length; mi++)
        {
            var md = _meshes[mi];
            int vc = md.VertexCount;
            var vtx = new Vector3[vc]; var nrm = new Vector3[vc]; var uvs = new Vector2[vc];
            for (int i = 0; i < vc; i++)
            {
                vtx[i] = new Vector3(md.Positions[i * 3], md.Positions[i * 3 + 1], md.Positions[i * 3 + 2]);
                if (md.Normals is { Length: > 0 }) nrm[i] = new Vector3(md.Normals[i * 3], md.Normals[i * 3 + 1], md.Normals[i * 3 + 2]);
                if (md.Uvs is { Length: > 0 }) uvs[i] = new Vector2(md.Uvs[i * 2], md.Uvs[i * 2 + 1]);
                var p = vtx[i];
                if (p.X < min.X) min.X = p.X; if (p.Y < min.Y) min.Y = p.Y; if (p.Z < min.Z) min.Z = p.Z;
                if (p.X > max.X) max.X = p.X; if (p.Y > max.Y) max.Y = p.Y; if (p.Z > max.Z) max.Z = p.Z;
            }
            var arr = new Godot.Collections.Array();
            arr.Resize((int)Mesh.ArrayType.Max);
            arr[(int)Mesh.ArrayType.Vertex] = vtx;
            arr[(int)Mesh.ArrayType.Normal] = nrm;
            arr[(int)Mesh.ArrayType.TexUV] = uvs;
            arr[(int)Mesh.ArrayType.Bones] = md.BoneIndices!;   // always set for character meshes
            arr[(int)Mesh.ArrayType.Weights] = md.BoneWeights!;
            arr[(int)Mesh.ArrayType.Index] = md.Indices;
            var am = new ArrayMesh();
            am.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
            var inst = new MeshInstance3D { Mesh = am, MaterialOverride = _mat[mi] };
            s.AddChild(inst);
            inst.Skin = s.CreateSkinFromRestTransforms();
            inst.Skeleton = inst.GetPathTo(s);
        }
        return (root, s, new Aabb(min, max - min));
    }

    /// Normalize a texture name for matching: drop control/space bytes, lowercase (mesh refs and IMG ids
    /// are the same 16-byte name but can differ in padding/case).
    private static string Norm(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s) if (c > 32 && c < 127) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// Best clip whose name starts with any of the given prefixes (e.g. "wlk","run" / "idl"), else null.
    public string? FindClip(params string[] prefixes)
    {
        foreach (var pre in prefixes)
            foreach (var name in _clipPayloads.Keys)
                if (name.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) return name;
        return null;
    }

    /// Skeleton-independent driver tracks for a clip (decoded + cached). Returns (tracks, numFrames, fps)
    /// or null if the clip is missing/undecodable.
    public (AnimationDriver.Track[] tracks, int frames, float fps)? Clip(string name, float fps = 30f)
    {
        if (!_clipPayloads.TryGetValue(name, out var payload)) return null;
        if (!_clipCache.TryGetValue(name, out var tracks))
        {
            var anim = ModelDecoder.DecodeAnimation(payload);
            // FFXI playback rate = 30 * frameSpeed (ref: galkareeve TDWCharacter.cpp:896). The old fixed 30fps
            // ignored frameSpeed, so idle (small frameSpeed) played several times too fast.
            float useFps = anim.FrameSpeed > 0.001f ? 30f * anim.FrameSpeed : fps;
            tracks = AnimationDriver.ToTracks(anim, useFps);
            _clipCache[name] = tracks;
            _clipFrames[name] = anim.NumFrames;
            _clipFps[name] = useFps;
        }
        return (tracks, _clipFrames[name], _clipFps[name]);
    }

    private readonly Dictionary<string, int> _clipFrames = new();
    private readonly Dictionary<string, float> _clipFps = new();
}
