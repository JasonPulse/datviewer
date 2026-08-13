using Godot;

namespace Vellichor.Render;

/// <summary>
/// Builds a Godot <see cref="Skeleton3D"/> + skinned <see cref="MeshInstance3D"/> from raw arrays —
/// the render target for FFXI character/NPC models (skeleton + skinned meshes). Decoders produce the
/// arrays; this turns them into a posable, GPU-skinned model. Verified with a synthetic 2-bone bend
/// test (VELLICHOR_SKINTEST) before real model data is wired in.
/// </summary>
public static class SkinnedMeshBuilder
{
    public readonly record struct Bone(int Parent, Transform3D Rest);

    /// <param name="pos">xyz * vertexCount</param>
    /// <param name="norm">xyz * vertexCount</param>
    /// <param name="uv">uv * vertexCount</param>
    /// <param name="boneIdx">4 bone indices per vertex</param>
    /// <param name="weight">4 weights per vertex (should sum to 1)</param>
    /// <param name="indices">triangle-list</param>
    public static (Skeleton3D skel, MeshInstance3D mesh) Build(
        float[] pos, float[] norm, float[] uv, int[] boneIdx, float[] weight, int[] indices,
        Bone[] bones, Material material)
    {
        var skel = new Skeleton3D();
        for (int i = 0; i < bones.Length; i++) skel.AddBone($"b{i}");
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i].Parent >= 0) skel.SetBoneParent(i, bones[i].Parent);
            skel.SetBoneRest(i, bones[i].Rest);
            skel.SetBonePosePosition(i, bones[i].Rest.Origin);
            skel.SetBonePoseRotation(i, bones[i].Rest.Basis.GetRotationQuaternion());
            skel.SetBonePoseScale(i, bones[i].Rest.Basis.Scale);
        }

        int vc = pos.Length / 3;
        var vtx = new Vector3[vc];
        var nrm = new Vector3[vc];
        var uvs = new Vector2[vc];
        for (int i = 0; i < vc; i++)
        {
            vtx[i] = new Vector3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);
            nrm[i] = new Vector3(norm[i * 3], norm[i * 3 + 1], norm[i * 3 + 2]);
            uvs[i] = new Vector2(uv[i * 2], uv[i * 2 + 1]);
        }

        var arr = new Godot.Collections.Array();
        arr.Resize((int)Mesh.ArrayType.Max);
        arr[(int)Mesh.ArrayType.Vertex] = vtx;
        arr[(int)Mesh.ArrayType.Normal] = nrm;
        arr[(int)Mesh.ArrayType.TexUV] = uvs;
        arr[(int)Mesh.ArrayType.Bones] = boneIdx;   // 4 per vertex
        arr[(int)Mesh.ArrayType.Weights] = weight;  // 4 per vertex
        arr[(int)Mesh.ArrayType.Index] = indices;

        var am = new ArrayMesh();
        am.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);

        var mi = new MeshInstance3D { Mesh = am, MaterialOverride = material };
        skel.AddChild(mi);
        mi.Skin = skel.CreateSkinFromRestTransforms();
        mi.Skeleton = mi.GetPathTo(skel);
        return (skel, mi);
    }

    /// <summary>
    /// Synthetic verification: a vertical capsule-ish cylinder skinned to 2 bones (lower half → root,
    /// upper half → a child bone). Posing the child bone bends the top — proof the skinning pipeline
    /// (bones/weights/skin) works before real model decode. Returns the skeleton (pose bone 1 to bend).
    /// </summary>
    public static Skeleton3D BuildBendTest(Material material)
    {
        const int rings = 10, seg = 12; const float r = 0.35f, h = 2.0f;
        var pos = new System.Collections.Generic.List<float>();
        var norm = new System.Collections.Generic.List<float>();
        var uv = new System.Collections.Generic.List<float>();
        var bi = new System.Collections.Generic.List<int>();
        var wt = new System.Collections.Generic.List<float>();
        for (int ry = 0; ry <= rings; ry++)
        {
            float v = ry / (float)rings, y = v * h;
            for (int s = 0; s <= seg; s++)
            {
                float u = s / (float)seg, a = u * Mathf.Tau;
                float nx = Mathf.Cos(a), nz = Mathf.Sin(a);
                pos.Add(nx * r); pos.Add(y); pos.Add(nz * r);
                norm.Add(nx); norm.Add(0); norm.Add(nz);
                uv.Add(u); uv.Add(v);
                // lower half → bone0, upper half → bone1, blended across the middle ring
                float w1 = Mathf.Clamp((v - 0.4f) / 0.2f, 0f, 1f);
                bi.Add(0); bi.Add(1); bi.Add(0); bi.Add(0);
                wt.Add(1f - w1); wt.Add(w1); wt.Add(0); wt.Add(0);
            }
        }
        var idx = new System.Collections.Generic.List<int>();
        int stride = seg + 1;
        for (int ry = 0; ry < rings; ry++)
            for (int s = 0; s < seg; s++)
            {
                int a = ry * stride + s, b = a + 1, c = a + stride, d = c + 1;
                idx.Add(a); idx.Add(c); idx.Add(b);
                idx.Add(b); idx.Add(c); idx.Add(d);
            }

        var bones = new[]
        {
            new Bone(-1, Transform3D.Identity),                                  // root at origin
            new Bone(0, new Transform3D(Basis.Identity, new Vector3(0, 1.0f, 0))), // child at mid-height
        };
        var (skel, _) = Build(pos.ToArray(), norm.ToArray(), uv.ToArray(), bi.ToArray(), wt.ToArray(), idx.ToArray(), bones, material);
        return skel;
    }
}
