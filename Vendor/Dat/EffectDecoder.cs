using System;
using System.Collections.Generic;

namespace Vellichor.Dat;

/// <summary>
/// One particle GENERATOR from an effect DAT (chunk type 0x05, named g000/gs00/…). FFXI drives most
/// generator params (color, size, velocity) through keyframe channels (int indices into the 0x19 "k###"
/// pools, mirroring the 0x2b skeletal-animation channel scheme) — full RE of that scheduler is ongoing, so
/// for now we keep the raw payload + a best-effort base color/scale and render a real-texture particle plume.
/// </summary>
public sealed class EffectGenerator
{
    public required string Name;
    public required byte[] Payload;
    public float[] Color = { 1f, 1f, 1f, 1f };   // base tint (fire etc. is white here — the texture carries color)
    public float Scale = 0.5f;
}

/// <summary>Decoded contents of an FFXI effect DAT (spells, abilities, weapon skills…).</summary>
public sealed class EffectData
{
    public List<ImgTexture> Textures = new();     // 0x20 IMG sprites the effect draws
    public List<EffectGenerator> Generators = new(); // 0x05 particle generators
    public string Diag = "";
    public bool IsEmpty => Textures.Count == 0 && Generators.Count == 0;
}

/// <summary>
/// Decodes an FFXI effect DAT into its sprites + generators. Pure C# (no Godot) so the client and the DAT
/// viewer share it, same as the model/animation decoders. Recovered by decompiling Altana Viewer (the
/// "FFXI Effect Viewer") — see the altana-decompile notes. Chunk map: 0x05 generators, 0x19 keyframe pools,
/// 0x1f timing, 0x20 textures, 0x07 a driving motion.
/// </summary>
public static class EffectDecoder
{
    public static EffectData Decode(byte[] dat)
    {
        var e = new EffectData();
        foreach (var c in ChunkReader.Walk(dat))
        {
            if (c.PayloadOffset < 0 || c.PayloadOffset + c.PayloadLength > dat.Length || c.PayloadLength <= 0) continue;
            var pay = dat[c.PayloadOffset..(c.PayloadOffset + c.PayloadLength)];
            switch (c.Type)
            {
                case 0x20:
                    ImgTexture? img = null;
                    try { img = ImgDecoder.Decode(pay); } catch { }
                    if (img is not null) e.Textures.Add(img);
                    break;
                case 0x05:
                    e.Generators.Add(ParseGenerator(c.Name, pay));
                    break;
            }
        }
        e.Diag = $"{e.Generators.Count} generator(s), {e.Textures.Count} texture(s)";
        return e;
    }

    private static EffectGenerator ParseGenerator(string name, byte[] p)
    {
        var g = new EffectGenerator { Name = name, Payload = p };
        // Best-effort base RGBA: the float group at +64 (observed = white on Fire; the sprite carries the hue).
        if (p.Length >= 80)
        {
            float[] col = { F(p, 64), F(p, 68), F(p, 72), F(p, 76) };
            bool valid = true;
            foreach (var v in col) if (v < 0f || v > 1f || float.IsNaN(v)) valid = false;
            if (valid && (col[0] + col[1] + col[2]) > 0f) g.Color = col;
        }
        return g;
    }

    private static float F(byte[] p, int o) => o + 4 <= p.Length ? BitConverter.ToSingle(p, o) : 0f;
}
