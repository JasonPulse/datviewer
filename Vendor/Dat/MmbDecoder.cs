using System.Collections.Generic;

namespace Vellichor.Dat;

/// <summary>Result of decoding one MMB (0x2e) chunk: an id (matched by MZB) + its meshes.</summary>
public sealed class MmbModel
{
    public required string MmbId { get; init; }
    public required List<MeshData> Meshes { get; init; }
    public string Diag { get; set; } = "";
    public bool Ok => Diag.Length == 0;
}

/// <summary>
/// Decodes MMB zone geometry. Layout (cross-verified galkareeve TDWAnalysis.h/TDWMap.cpp
/// + InoUno xi-tinkerer):
///   header@0x00: u32 (size:24|flag:8), u8 kind@0x04, char[8] moniker@0x08,
///                char[16] mmbId@0x10, u32 pieces@0x20 (9 if flag byte ≤ 1),
///                6×f32 bbox@0x24, u32[pieces] block-offset table@0x3C (stop at 0).
///   block@offset: u32 modelCount@0x00, 6×f32 bbox, u32 faceId@0x1C; models start @+32.
///   model@p:      char[16] textureId@0x00, u32 vertexCount@0x10 (low bits),
///                 verts@0x14 (stride 36 for kind!=2, else 48), u32 indexCount, u16 indices;
///                 advance to next model aligned up to 4 bytes.
///   vertex(36):   pos f32×3 @0x00, normal f32×3 @0x0C, color u32 @0x18, uv f32×2 @0x1C.
///   primitives:   triangle STRIP when kind==1||3, else list.
/// Fully bounds-checked: any inconsistency records a Diag and stops that block/model
/// rather than reading garbage — so a wrong assumption shows up as a failed parse.
/// </summary>
public static class MmbDecoder
{
    public static MmbModel Decode(byte[] p)
    {
        var meshes = new List<MeshData>();
        var model = new MmbModel { MmbId = "", Meshes = meshes };
        if (p.Length < 0x40) { model.Diag = "payload<0x40"; return model; }

        DatCrypt.DecodeMmb(p, p.Length); // deobfuscate in place before parsing

        int kind = p[4];
        int stride = kind == 2 ? 48 : 36;
        int normalOff = kind == 2 ? 0x18 : 0x0C;
        int uvOff = kind == 2 ? 0x28 : 0x1C;
        // galkareeve's DrawMMB draws EVERY model as a triangle strip (unconditional
        // D3DPT_TRIANGLESTRIP). Treating a strip mesh as a list generates giant spurious
        // triangles across the mesh — so always strip, matching the reference renderer.
        const bool strip = true;
        model = new MmbModel { MmbId = Bin.Name(p, 0x10, 16), Meshes = meshes };

        // Block-walk matches galkareeve's DrawMMB: skip the offset table by its SIZE (its
        // values are unused) and read `pieces` blocks sequentially, back to back. My earlier
        // table-jump stopped at the first zero table entry and silently dropped the trailing
        // blocks — which showed up as holes in the terrain.
        int pieces = (int)Bin.U32(p, 0x20);
        if (pieces is <= 0 or > 0xFF) { model.Diag = $"pieces={pieces};"; return model; }
        int mp = pieces == 1 ? 0x20 + 32 : pieces <= 16 ? 0x20 + 64 : 0x20 + pieces * 4;

        for (int blk = 0; blk < pieces; blk++)
        {
            if (mp + 4 > p.Length) { model.Diag += "blk-oob;"; break; }
            int modelCount = Bin.I32(p, mp);
            if (modelCount is < 0 or > 0xFFFF) { model.Diag += $"mc={modelCount};"; break; }
            mp += 32; // block header
            bool bail = false;
            for (int mi = 0; mi < modelCount; mi++)
            {
                if (mp + 0x14 > p.Length) { model.Diag += "mhdr-oob;"; bail = true; break; }
                string texId = Bin.Name(p, mp, 16);
                int vertexCount = Bin.U16(p, mp + 0x10); // 16-bit, matches galkareeve *(short*)
                if (vertexCount is <= 0 or > 0xFFFF) { model.Diag += $"vc={vertexCount};"; bail = true; break; }

                int vStart = mp + 0x14;
                int icOff = vStart + vertexCount * stride;
                if (icOff + 4 > p.Length) { model.Diag += "ic-oob;"; bail = true; break; }
                int indexCount = Bin.I32(p, icOff);
                if (indexCount is < 0 or > 0xFFFF) { model.Diag += $"ic={indexCount};"; bail = true; break; }

                int idxStart = icOff + 4;
                int idxEnd = idxStart + indexCount * 2;
                if (idxEnd > p.Length) { model.Diag += "idx-oob;"; bail = true; break; }

                var pos = new float[vertexCount * 3];
                var nrm = new float[vertexCount * 3];
                var uv = new float[vertexCount * 2];
                for (int v = 0; v < vertexCount; v++)
                {
                    int vo = vStart + v * stride;
                    pos[v * 3] = Bin.F32(p, vo);
                    pos[v * 3 + 1] = Bin.F32(p, vo + 4);
                    pos[v * 3 + 2] = Bin.F32(p, vo + 8);
                    nrm[v * 3] = Bin.F32(p, vo + normalOff);
                    nrm[v * 3 + 1] = Bin.F32(p, vo + normalOff + 4);
                    nrm[v * 3 + 2] = Bin.F32(p, vo + normalOff + 8);
                    uv[v * 2] = Bin.F32(p, vo + uvOff);
                    uv[v * 2 + 1] = Bin.F32(p, vo + uvOff + 4);
                }

                meshes.Add(new MeshData
                {
                    Positions = pos,
                    Normals = nrm,
                    Uvs = uv,
                    Indices = ToTriangles(p, idxStart, indexCount, strip),
                    TextureId = texId,
                });

                mp = 4 * ((idxEnd + 3) / 4); // next model, 4-byte aligned
            }
            if (bail) break;
        }
        return model;
    }

    private static int[] ToTriangles(byte[] p, int idxStart, int indexCount, bool strip)
    {
        var idx = new int[indexCount];
        for (int i = 0; i < indexCount; i++) idx[i] = Bin.U16(p, idxStart + i * 2);

        var tris = new List<int>();
        if (strip)
        {
            for (int i = 0; i + 2 < indexCount; i++)
            {
                int a = idx[i], b = idx[i + 1], c = idx[i + 2];
                if (a == b || b == c || a == c) continue; // degenerate / restart
                if ((i & 1) == 0) { tris.Add(a); tris.Add(b); tris.Add(c); }
                else { tris.Add(b); tris.Add(a); tris.Add(c); }
            }
        }
        else
        {
            for (int i = 0; i + 2 < indexCount; i += 3) { tris.Add(idx[i]); tris.Add(idx[i + 1]); tris.Add(idx[i + 2]); }
        }
        return tris.ToArray();
    }
}
