using System.Collections.Generic;
using System.Linq;
using Godot;
using Vellichor.Dat;

namespace Vellichor.Render;

/// <summary>
/// Builds a playable Godot node from a decoded FFXI <see cref="EffectData"/> (a spell/ability effect DAT).
/// Each 0x05 generator becomes an additive, camera-facing particle emitter drawing the effect's own decoded
/// 0x20 sprite(s), tinted by the generator's base color. FFXI's exact generator scheduler (keyframe-animated
/// size/color/velocity in the 0x19 pools) isn't fully reversed yet, so emission uses tuned defaults — the
/// result is a recognizable, real-texture effect (Fire looks like the game's fire), refinable toward
/// frame-accuracy as the generator format is decoded. Shared render code, so the DAT viewer inherits it.
/// </summary>
public static class EffectPlayer
{
    /// Build a Node3D playing the effect. <paramref name="scale"/> scales the whole thing to taste.
    public static Node3D Build(EffectData eff, float scale = 1f)
    {
        var root = new Node3D { Name = "Effect" };
        if (eff.IsEmpty) return root;

        // Decode the effect's IMG sprites to Godot textures (largest first — the primary sprite is usually
        // the biggest, e.g. the fire body vs small glints).
        var texs = eff.Textures
            .OrderByDescending(t => t.Width * t.Height)
            .Select(t => (Texture2D)ImageTexture.CreateFromImage(
                Image.CreateFromData(t.Width, t.Height, false, Image.Format.Rgba8, t.Rgba)))
            .ToList();
        if (texs.Count == 0) return root;

        // One emitter per generator (fallback: a single emitter using the primary sprite).
        var gens = eff.Generators.Count > 0 ? eff.Generators : new List<EffectGenerator> { new() { Name = "g", Payload = System.Array.Empty<byte>() } };
        // Additive emitters stacked at one origin blow out to white as their count grows (we don't yet have
        // the scheduler's per-generator positions/counts), so damp per-emitter budget by the total.
        float damp = 1f / Mathf.Sqrt(gens.Count);
        for (int i = 0; i < gens.Count; i++)
        {
            var tex = texs[Mathf.Min(i, texs.Count - 1)];
            root.AddChild(Emitter(tex, gens[i], scale, damp));
        }
        return root;
    }

    private static GpuParticles3D Emitter(Texture2D tex, EffectGenerator gen, float scale, float damp)
    {
        var c = gen.Color;
        var tint = new Color(c[0], c[1], c[2], (c.Length > 3 ? c[3] : 1f) * Mathf.Clamp(damp * 1.6f, 0.15f, 1f));
        var pm = new ParticleProcessMaterial
        {
            Direction = new Vector3(0, 1, 0),
            Spread = 20f,
            InitialVelocityMin = 0.6f * scale, InitialVelocityMax = 1.4f * scale,
            Gravity = new Vector3(0, 0.4f * scale, 0),
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.15f * scale,
            ScaleMin = 0.6f * scale * gen.Scale * 2f, ScaleMax = 1.1f * scale * gen.Scale * 2f,
            Color = tint,
            // fade in→out over life so particles don't pop
            AlphaCurve = MakeCurve((0f, 0f), (0.15f, 1f), (1f, 0f)),
        };
        return new GpuParticles3D
        {
            Amount = Mathf.Max(8, (int)(40 * damp)),
            Lifetime = 1.0,
            ProcessMaterial = pm,
            DrawPass1 = new QuadMesh { Size = new Vector2(0.5f, 0.5f) * scale, Material = EffectFx.BillboardMat(tex, additive: true) },
            Emitting = true,
        };
    }

    private static CurveTexture MakeCurve(params (float t, float v)[] pts)
    {
        var curve = new Curve();
        foreach (var (t, v) in pts) curve.AddPoint(new Vector2(t, v));
        return new CurveTexture { Curve = curve };
    }
}
