using Godot;

namespace Vellichor.Render;

/// <summary>
/// GPU particle effects (smoke / fire / glow). FFXI's real effect scheduler/generator format is
/// undocumented, so this is the "reads-as-an-effect" approximation: camera-facing additive
/// billboards driven by a <see cref="ParticleProcessMaterial"/>. The billboard texture can be a
/// generated soft dot (default) or a decoded FFXI effect IMG — both plug into the same draw pass.
/// </summary>
public static class EffectFx
{
    /// A soft radial alpha dot — the particle sprite when no FFXI effect texture is supplied.
    public static ImageTexture SoftDot(int size = 64)
    {
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        float c = (size - 1) / 2f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                float a = Mathf.Clamp(1f - d, 0f, 1f);
                a = a * a; // soft falloff
                img.SetPixel(x, y, new Color(1, 1, 1, a));
            }
        return ImageTexture.CreateFromImage(img);
    }

    public static StandardMaterial3D BillboardMat(Texture2D tex, bool additive)
    {
        return new StandardMaterial3D
        {
            AlbedoTexture = tex,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = additive ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            BillboardKeepScale = true,
            VertexColorUseAsAlbedo = true, // per-particle color/alpha from the process material
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            DisableReceiveShadows = true,
        };
    }

    private static GradientTexture1D Ramp(params Color[] stops)
    {
        var g = new Gradient();
        var offs = new float[stops.Length];
        for (int i = 0; i < stops.Length; i++) offs[i] = stops.Length == 1 ? 0 : i / (float)(stops.Length - 1);
        g.Offsets = offs;
        g.Colors = stops;
        return new GradientTexture1D { Gradient = g };
    }

    private static CurveTexture Curve(params (float t, float v)[] pts)
    {
        var c = new Curve();
        foreach (var (t, v) in pts) c.AddPoint(new Vector2(t, v));
        return new CurveTexture { Curve = c };
    }

    /// A drifting smoke plume. <paramref name="tex"/> overrides the sprite (e.g. a real FFXI IMG).
    public static GpuParticles3D Smoke(Texture2D? tex = null)
    {
        var pm = new ParticleProcessMaterial
        {
            Direction = new Vector3(0, 1, 0),
            Spread = 18f,
            InitialVelocityMin = 0.6f, InitialVelocityMax = 1.1f,
            Gravity = new Vector3(0, 0.15f, 0),
            EmissionShapeScale = Vector3.One * 0.25f,
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.25f,
            ScaleMin = 0.6f, ScaleMax = 1.2f,
            ScaleCurve = Curve((0, 0.4f), (1, 1.6f)),
            ColorRamp = Ramp(new Color(0.7f, 0.7f, 0.72f, 0.0f), new Color(0.6f, 0.6f, 0.62f, 0.5f), new Color(0.5f, 0.5f, 0.52f, 0f)),
            AngularVelocityMin = -30, AngularVelocityMax = 30,
        };
        return new GpuParticles3D
        {
            Amount = 40,
            Lifetime = 2.4,
            ProcessMaterial = pm,
            DrawPass1 = new QuadMesh { Size = new Vector2(0.7f, 0.7f), Material = BillboardMat(tex ?? SoftDot(), additive: false) },
        };
    }

    /// A flickering fire. <paramref name="tex"/> overrides the sprite (e.g. a real FFXI IMG).
    public static GpuParticles3D Fire(Texture2D? tex = null)
    {
        var pm = new ParticleProcessMaterial
        {
            Direction = new Vector3(0, 1, 0),
            Spread = 12f,
            InitialVelocityMin = 1.0f, InitialVelocityMax = 1.8f,
            Gravity = new Vector3(0, 0.6f, 0),
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.18f,
            ScaleMin = 0.5f, ScaleMax = 0.9f,
            ScaleCurve = Curve((0, 1.1f), (1, 0.1f)),
            ColorRamp = Ramp(new Color(1.0f, 0.95f, 0.5f, 1f), new Color(1.0f, 0.5f, 0.1f, 0.9f), new Color(0.6f, 0.1f, 0.05f, 0f)),
        };
        return new GpuParticles3D
        {
            Amount = 60,
            Lifetime = 0.9,
            ProcessMaterial = pm,
            DrawPass1 = new QuadMesh { Size = new Vector2(0.5f, 0.5f), Material = BillboardMat(tex ?? SoftDot(), additive: true) },
        };
    }
}
