using System.Collections.Generic;

namespace Vellichor.Dat;

/// <summary>
/// Decodes the MZB collision mesh (Section D) — the continuous walkable-ground surface that
/// the visible MMB tiles don't fully cover. Rendered as an untextured ground fill so paths
/// and gaps aren't see-through. Ported verbatim from teschnei/ffxi mzb.cpp (parseMesh /
/// parseGridEntry / parseGridMesh):
///   SMZBHeader:  collisionMeshOffset@0x08 (u32), gridWidth@0x0C (u8), gridHeight@0x0D (u8)
///   SMZBHeader2 @collisionMeshOffset: mesh_count@0x00, mesh_data@0x04, grid_offset@0x10
///   MeshBlock @off: vtxOff@0, nrmOff@4, triOff@8, triCount@0x0C(u16); verts=3×f32,
///                   tris=triCount×(4×u16) v1,v2,v3,n (indices &amp; 0x3FFF); next=triOff+triCount*8
///   Grid: (gridWidth*10)×(gridHeight*10) u32 cell pointers; each cell = u32s until 0,
///         entry[0]=info, then (placementOffset, geometryOffset) pairs.
///   Placement (o2w): 4 rows of {3×f32, 2×u16}; basis cols = rows 0..2 xyz, origin = row 3 xyz.
/// Output is baked to Godot world space (positions Y-flipped), merged into one MeshData.
/// </summary>
public static class MzbCollisionDecoder
{
    public static MeshData? Decode(byte[] rawPayload)
    {
        var p = (byte[])rawPayload.Clone();
        DatCrypt.DecodeMzb(p, p.Length);
        if (p.Length < 0x20) return null;

        int collOff = (int)Bin.U32(p, 0x08);
        int gridW = p[0x0C] * 10, gridH = p[0x0D] * 10;
        if (collOff <= 0 || collOff + 0x14 > p.Length) return null;

        int meshCount = (int)Bin.U32(p, collOff + 0x00);
        int meshData = (int)Bin.U32(p, collOff + 0x04);
        int gridOff = (int)Bin.U32(p, collOff + 0x10);

        // Parse mesh blocks sequentially; remember each block's local verts + indices by offset.
        var meshVerts = new Dictionary<int, float[]>();
        var meshIdx = new Dictionary<int, int[]>();
        int off = meshData;
        for (int i = 0; i < meshCount && off > 0 && off + 16 <= p.Length; i++)
        {
            int vtxOff = (int)Bin.U32(p, off + 0);
            int nrmOff = (int)Bin.U32(p, off + 4);
            int triOff = (int)Bin.U32(p, off + 8);
            int triCount = Bin.U16(p, off + 0x0C);
            if (vtxOff < 0 || nrmOff < vtxOff || triOff < nrmOff || triOff + triCount * 8 > p.Length) break;

            int vcount = (nrmOff - vtxOff) / 12;
            var verts = new float[vcount * 3];
            for (int v = 0; v < vcount; v++)
            {
                verts[v * 3] = Bin.F32(p, vtxOff + v * 12);
                verts[v * 3 + 1] = Bin.F32(p, vtxOff + v * 12 + 4);
                verts[v * 3 + 2] = Bin.F32(p, vtxOff + v * 12 + 8);
            }
            var idx = new int[triCount * 3];
            for (int t = 0; t < triCount; t++)
            {
                idx[t * 3] = Bin.U16(p, triOff + (t * 4 + 0) * 2) & 0x3FFF;
                idx[t * 3 + 1] = Bin.U16(p, triOff + (t * 4 + 1) * 2) & 0x3FFF;
                idx[t * 3 + 2] = Bin.U16(p, triOff + (t * 4 + 2) * 2) & 0x3FFF;
            }
            meshVerts[off] = verts;
            meshIdx[off] = idx;
            off = triOff + triCount * 8;
        }

        // Walk the grid, collect unique (placement, geometry) pairs.
        var pairs = new HashSet<(int, int)>();
        if (gridOff > 0)
        {
            for (int y = 0; y < gridH; y++)
                for (int x = 0; x < gridW; x++)
                {
                    int cellPtrOff = gridOff + (y * gridW + x) * 4;
                    if (cellPtrOff + 4 > p.Length) continue;
                    int eo = (int)Bin.U32(p, cellPtrOff);
                    if (eo == 0) continue;
                    // read u32s until 0: entry[0]=info, then (placement, geometry) pairs
                    var entries = new List<int>();
                    int e = eo;
                    while (e + 4 <= p.Length)
                    {
                        int v = (int)Bin.U32(p, e);
                        if (v == 0) break;
                        entries.Add(v);
                        e += 4;
                    }
                    for (int i = 1; i + 1 < entries.Count; i += 2)
                        pairs.Add((entries[i], entries[i + 1]));
                }
        }

        // Build one merged, world-space, Y-flipped mesh from the unique pairs.
        var positions = new List<float>();
        var indices = new List<int>();
        foreach (var (placementOff, geoOff) in pairs)
        {
            if (!meshVerts.TryGetValue(geoOff, out var verts) || placementOff + 60 > p.Length) continue;
            // o2w basis columns + origin (ignore the 2×u16 opts at the end of each row)
            float c0x = Bin.F32(p, placementOff + 0), c0y = Bin.F32(p, placementOff + 4), c0z = Bin.F32(p, placementOff + 8);
            float c1x = Bin.F32(p, placementOff + 16), c1y = Bin.F32(p, placementOff + 20), c1z = Bin.F32(p, placementOff + 24);
            float c2x = Bin.F32(p, placementOff + 32), c2y = Bin.F32(p, placementOff + 36), c2z = Bin.F32(p, placementOff + 40);
            float ox = Bin.F32(p, placementOff + 48), oy = Bin.F32(p, placementOff + 52), oz = Bin.F32(p, placementOff + 56);

            int baseIdx = positions.Count / 3;
            for (int v = 0; v < verts.Length / 3; v++)
            {
                float lx = verts[v * 3], ly = verts[v * 3 + 1], lz = verts[v * 3 + 2];
                float wx = c0x * lx + c1x * ly + c2x * lz + ox;
                float wy = c0y * lx + c1y * ly + c2y * lz + oy;
                float wz = c0z * lx + c1z * ly + c2z * lz + oz;
                positions.Add(wx);
                positions.Add(-wy); // FFXI Y-down -> Godot Y-up
                positions.Add(wz);
            }
            // Reverse winding to compensate for the Y-negation above (a mirror flips winding);
            // otherwise computed normals point INTO the surface and it renders inside-out.
            var mi = meshIdx[geoOff];
            for (int t = 0; t + 2 < mi.Length; t += 3)
            {
                indices.Add(baseIdx + mi[t]);
                indices.Add(baseIdx + mi[t + 2]);
                indices.Add(baseIdx + mi[t + 1]);
            }
        }

        if (positions.Count == 0) return null;

        // Smooth per-vertex normals (accumulate face normals) so the fill is lit and its
        // curvature reads — collision blocks carry no usable per-vertex normals for this.
        var pos = positions.ToArray();
        var idxA = indices.ToArray();
        var nrm = new float[pos.Length];
        for (int t = 0; t + 2 < idxA.Length; t += 3)
        {
            int a = idxA[t], b = idxA[t + 1], c = idxA[t + 2];
            float ax = pos[a * 3], ay = pos[a * 3 + 1], az = pos[a * 3 + 2];
            float ux = pos[b * 3] - ax, uy = pos[b * 3 + 1] - ay, uz = pos[b * 3 + 2] - az;
            float vx = pos[c * 3] - ax, vy = pos[c * 3 + 1] - ay, vz = pos[c * 3 + 2] - az;
            float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            foreach (int i in stackalloc[] { a, b, c }) { nrm[i * 3] += nx; nrm[i * 3 + 1] += ny; nrm[i * 3 + 2] += nz; }
        }
        for (int v = 0; v < pos.Length / 3; v++)
        {
            float nx = nrm[v * 3], ny = nrm[v * 3 + 1], nz = nrm[v * 3 + 2];
            float len = (float)System.Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 1e-6f) { nrm[v * 3] = nx / len; nrm[v * 3 + 1] = ny / len; nrm[v * 3 + 2] = nz / len; }
        }
        return new MeshData { Positions = pos, Normals = nrm, Indices = idxA };
    }
}
