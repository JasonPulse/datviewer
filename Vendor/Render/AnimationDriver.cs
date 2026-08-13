using System.Collections.Generic;
using Godot;
using Vellichor.Dat;

namespace Vellichor.Render;

/// <summary>
/// Drives a Godot <see cref="Skeleton3D"/> from per-bone keyframe tracks (the decoded form of an FFXI
/// 0x2b animation): each frame it advances a clock, finds the surrounding keyframes per bone, and applies
/// an interpolated LOCAL pose (rotation, optional translation) via SetBonePose*. Rotations slerp,
/// translations lerp; bones with no track keep their rest pose.
///
/// Decoupled from the DAT decoder on purpose — it takes plain Godot types, so the 0x2b decoder can be
/// finished independently and adapted into <see cref="Track"/>s with a thin converter. Verified with a
/// synthetic sine wobble (VELLICHOR_ANIMTEST) before real animation data is wired in.
/// </summary>
public partial class AnimationDriver : Node
{
    public readonly record struct Key(float Time, Quaternion Rot, Vector3? Pos);

    /// Converts a decoded 0x2b animation into keyframe Tracks (the thin decoder→driver adapter). Lives
    /// here rather than in a viewer because the output type is <see cref="Track"/> — both the game client
    /// and the DAT viewer reuse it.
    public static Track[] ToTracks(ModelDecoder.Animation anim, float fps)
    {
        float f = fps > 0.01f ? fps : 30f;
        var tracks = new List<Track>(anim.Tracks.Length);
        foreach (var bt in anim.Tracks)
        {
            var keys = new Key[bt.Keys.Length];
            for (int k = 0; k < bt.Keys.Length; k++)
            {
                var kf = bt.Keys[k];
                var q = new Quaternion(kf.Rot.X, kf.Rot.Y, kf.Rot.Z, kf.Rot.W);
                var pos = new Vector3(kf.Trans.X, kf.Trans.Y, kf.Trans.Z);
                keys[k] = new Key(kf.Frame / f, q.Normalized(), pos);
            }
            tracks.Add(new Track { Bone = bt.Bone, Keys = keys });
        }
        return tracks.ToArray();
    }

    /// One bone's keyframes, sorted by Time (seconds). Empty Keys = bone untouched (keeps rest).
    public sealed class Track
    {
        public required int Bone { get; init; }
        public required Key[] Keys { get; init; }
    }

    private Skeleton3D _skel = null!;
    private Track[] _tracks = System.Array.Empty<Track>();
    private float _duration;         // seconds
    private double _clock;           // seconds since anim start
    public bool Loop { get; set; } = true;
    public float Speed { get; set; } = 1f;
    public bool Playing { get; set; } = true;

    /// <param name="fps">frames per second the animation plays at (from the 0x2b frameSpeed).</param>
    /// <param name="numFrames">total frame count; duration = numFrames / fps.</param>
    public void Setup(Skeleton3D skel, IEnumerable<Track> tracks, int numFrames, float fps)
    {
        _skel = skel;
        _tracks = new List<Track>(tracks).ToArray();
        float f = fps > 0.01f ? fps : 30f;
        _duration = numFrames > 0 ? numFrames / f : 0f;
        _clock = 0;
    }

    /// Jump to an absolute time (seconds) and apply immediately — useful for offscreen frame captures.
    public void Seek(float seconds) { _clock = seconds; Apply((float)_clock); }

    public override void _Process(double delta)
    {
        if (!Playing || _skel is null || _duration <= 0f) return;
        _clock += delta * Speed;
        if (_clock >= _duration)
        {
            if (Loop) _clock = Mathf.PosMod((float)_clock, _duration);
            else { _clock = _duration; Playing = false; }
        }
        Apply((float)_clock);
    }

    private void Apply(float t)
    {
        foreach (var tr in _tracks)
        {
            var keys = tr.Keys;
            if (keys.Length == 0 || tr.Bone < 0 || tr.Bone >= _skel.GetBoneCount()) continue;
            if (keys.Length == 1) { ApplyKey(tr.Bone, keys[0].Rot, keys[0].Pos); continue; }

            // find the segment [a,b] containing t (keys sorted by Time); clamp at the ends
            int a = 0;
            while (a < keys.Length - 1 && keys[a + 1].Time <= t) a++;
            int b = System.Math.Min(a + 1, keys.Length - 1);
            var ka = keys[a];
            var kb = keys[b];
            float span = kb.Time - ka.Time;
            float u = span > 1e-5f ? Mathf.Clamp((t - ka.Time) / span, 0f, 1f) : 0f;

            var rot = ka.Rot.Slerp(kb.Rot, u);
            Vector3? pos = null;
            if (ka.Pos.HasValue && kb.Pos.HasValue) pos = ka.Pos.Value.Lerp(kb.Pos.Value, u);
            else if (ka.Pos.HasValue) pos = ka.Pos;
            ApplyKey(tr.Bone, rot, pos);
        }
    }

    /// When true, each key is a DELTA composed onto the bone's rest pose (rest.rot * key.rot, and the
    /// key translation is a rest-local offset) — the FFXI 0x2b convention, where the skeleton bind carries
    /// the real bone orientation and the animation stores small per-frame offsets. When false, keys are
    /// absolute local poses that replace the rest.
    public bool Additive { get; set; } = true;

    private void ApplyKey(int bone, Quaternion rot, Vector3? pos)
    {
        if (Additive)
        {
            var rest = _skel.GetBoneRest(bone);
            var restRot = rest.Basis.GetRotationQuaternion();
            // ref TDWCharacter.cpp:385 skinLocalRotation = animationRotation * jointLocalRotation (anim * rest).
            // We had rest*anim (reversed) — wrong on bones with a non-identity rest (female hips → crossed legs).
            _skel.SetBonePoseRotation(bone, (rot * restRot).Normalized());
            _skel.SetBonePosePosition(bone, pos.HasValue ? rest.Origin + rest.Basis * pos.Value : rest.Origin);
        }
        else
        {
            // Absolute local rotation, but KEEP the bone's rest translation — FFXI animates rotation per bone
            // (bones don't slide); overriding position with the ~0 animation translation collapses the skeleton.
            var rest = _skel.GetBoneRest(bone);
            _skel.SetBonePoseRotation(bone, rot.Normalized());
            _skel.SetBonePosePosition(bone, rest.Origin);
        }
    }
}
