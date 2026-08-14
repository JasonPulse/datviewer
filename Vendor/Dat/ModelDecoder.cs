using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Vellichor.Dat;

/// <summary>
/// Decodes FFXI character/NPC model chunks — the SKELETON (type 0x29) and SKINNED MESH (type 0x2a).
/// Unlike zone MMB/MZB, these payloads are PLAINTEXT (no DatCrypt). A monster/NPC lives in one DAT
/// (0x01 name, 0x29 skeleton, 0x2a mesh(es), 0x2b anims, 0x20 textures, 0x45 info).
/// Struct lineage: galkareeve/ffxi TDWAnalysis.h (BONE, DAT2AHeader, MODELVERTEX1/2).
/// </summary>
public static class ModelDecoder
{
    public static string MeshDiag = "";
    /// When set, DecodeOne2A appends per-part bone-table / used-bones / bbox detail to MeshDiag (debugging).
    public static bool VerboseDiag = false;
    /// FFXI two-pass mirror: symmetric parts (flip flag @0x04 set) render a 2nd pass using the RIGHT bone
    /// field + a reflection on the flg axis. Ref: galkareeve/ffxi TDWCharacter.cpp buildBindPosVertex.
    public static bool MirrorEnabled = true;

    public readonly record struct SkelBone(int Parent, float Qx, float Qy, float Qz, float Qw, float Tx, float Ty, float Tz);

    public sealed class Skeleton
    {
        public required SkelBone[] Bones { get; init; }
        public string Diag = "";
    }

    static float F(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadSingleLittleEndian(b[o..]);

    /// Parse a 0x29 skeleton payload: array of 30-byte BONE records after a small leading header.
    /// The header size varies (payloadLen % 30 is commonly 6), so we auto-detect the offset that
    /// yields the most unit-norm quaternions.
    public static Skeleton DecodeSkeleton(byte[] p)
    {
        const int rec = 30;
        int bestHdr = 0, bestScore = -1, bestN = 0;
        foreach (int hdr in new[] { 6, 0, 4, 8, 16, 2, 10 })
        {
            if (p.Length - hdr < rec) continue;
            int n = (p.Length - hdr) / rec, score = 0;
            for (int i = 0; i < n; i++)
            {
                int o = hdr + i * rec + 2;
                if (o + 16 > p.Length) break;
                float qx = F(p, o), qy = F(p, o + 4), qz = F(p, o + 8), qw = F(p, o + 12);
                double m = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
                if (Math.Abs(m - 1.0) < 0.02) score++;
            }
            if (score > bestScore) { bestScore = score; bestHdr = hdr; bestN = n; }
        }

        // The header carries the TRUE bone count at +0x02 (u16), confirmed by the sibling 0x2b anim header.
        // The 30-byte stride divides the whole payload (often 2-3x too many records = trailing garbage that
        // decodes as bad bones); clamp to the header count so bone indices line up with the anim + mesh refs.
        int hdrCount = p.Length >= 4 ? U16(p, 2) : 0;
        if (hdrCount is > 0 and <= 4096 && hdrCount <= bestN) bestN = hdrCount;

        var bones = new List<SkelBone>(bestN);
        for (int i = 0; i < bestN; i++)
        {
            int o = bestHdr + i * rec;
            if (o + rec > p.Length) break;
            bones.Add(new SkelBone(
                p[o],                                   // parent
                F(p, o + 2), F(p, o + 6), F(p, o + 10), F(p, o + 14), // quat x,y,z,w
                F(p, o + 18), F(p, o + 22), F(p, o + 26)));           // trans x,y,z
        }
        return new Skeleton
        {
            Bones = bones.ToArray(),
            Diag = $"hdr={bestHdr} bones={bones.Count} (hdrCount={hdrCount}) unitQuats={bestScore}",
        };
    }

    /// World bind matrices (model space) per bone: world[i] = local[i] * world[parent] (row-vector).
    static System.Numerics.Matrix4x4[] BindMatrices(Skeleton sk)
    {
        int n = sk.Bones.Length;
        var w = new System.Numerics.Matrix4x4[n];
        for (int i = 0; i < n; i++)
        {
            var b = sk.Bones[i];
            var q = new System.Numerics.Quaternion(b.Qx, b.Qy, b.Qz, b.Qw);
            var parent = (b.Parent >= 0 && b.Parent < i) ? w[b.Parent] : System.Numerics.Matrix4x4.Identity;
            // A garbage record (non-unit quat or absurd translation) would fling its verts across the
            // map — inherit the parent's transform instead so bad bones don't blow up the pose.
            bool bad = q.LengthSquared() is < 0.9f or > 1.1f
                       || MathF.Abs(b.Tx) > 20 || MathF.Abs(b.Ty) > 20 || MathF.Abs(b.Tz) > 20;
            if (bad) { w[i] = parent; continue; }
            var local = System.Numerics.Matrix4x4.CreateFromQuaternion(System.Numerics.Quaternion.Normalize(q));
            local.Translation = new System.Numerics.Vector3(b.Tx, b.Ty, b.Tz);
            w[i] = local * parent;
        }
        return w;
    }

    static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);

    /// Decode all 0x2a skinned meshes in a character/NPC DAT into bind-posed model-space MeshData
    /// (positions + normals in FFXI space, per-corner UVs, TextureId = bound texture name). Uses the
    /// sibling 0x29 skeleton. Header offsets/sizes are in 16-bit WORDS (×2 for bytes) — the key fix.
    public static List<MeshData> DecodeCharacterMeshes(byte[] dat)
    {
        var chunks = ChunkReader.Walk(dat);
        foreach (var c in chunks)
            if (c.Type == 0x29)
            {
                var skel = DecodeSkeleton(dat.AsSpan(c.PayloadOffset, c.PayloadLength).ToArray());
                return DecodeMeshesWithBind(dat, BindMatrices(skel));
            }
        return new List<MeshData>();
    }

    /// Decode the 0x2a meshes in an EQUIPMENT/part DAT (which has NO embedded 0x29) against a SEPARATE
    /// race skeleton — the FFXI PC-assembly model: a race skeleton DAT + per-slot equipment/face mesh
    /// DATs all skinned to the shared bones. Bone refs in the part index into the race skeleton.
    /// <param name="forceBone">if &gt;= 0, bind ALL vertices to this one bone (rigid weapon → hand grip).</param>
    public static List<MeshData> DecodeMeshesWithSkeleton(byte[] partDat, Skeleton raceSkeleton, int forceBone = -1)
        => DecodeMeshesWithBind(partDat, BindMatrices(raceSkeleton), forceBone);

    static List<MeshData> DecodeMeshesWithBind(byte[] dat, System.Numerics.Matrix4x4[] bind, int forceBone = -1)
    {
        var result = new List<MeshData>();
        var chunks = ChunkReader.Walk(dat);
        MeshDiag = "";
        int n2a = 0;
        foreach (var c in chunks)
        {
            if (c.Type != 0x2a) continue;
            n2a++;
            var pl = dat.AsSpan(c.PayloadOffset, c.PayloadLength).ToArray();
            ushort ty = pl.Length >= 6 ? U16(pl, 0x02) : (ushort)0;
            try { DecodeOne2A(pl, bind, result, forceBone); }
            catch (Exception ex) { MeshDiag += $"\n         part{n2a} type=0x{ty:x4} ERR {ex.GetType().Name}"; }
        }
        MeshDiag = $"{n2a} 0x2a part(s)" + MeshDiag;
        return result;
    }

    // ===== 0x2b SKELETAL ANIMATION (TVANIMATION / DAT2BHeader) ================================
    // Struct lineage: galkareeve/ffxi TDWAnalysis.h (DAT2BHeader, DAT2B) + TDWCharacter.cpp
    // (AddAnimation casts the payload straight to DAT2BHeader*; GetMotionMatrix samples it).
    //
    // Payload (PLAINTEXT) layout, offsets in BYTES:
    //   +0x00 u8   ver
    //   +0x01 u8   nazo (unknown/flags)
    //   +0x02 u16  element   = numBones (== 0x29 skeleton bone count)
    //   +0x04 u16  frame     = numFrames
    //   +0x06 f32  speed     = playback speed / frame duration
    //   +0x0A DAT2B[element]  per-bone track descriptors, 84 BYTES each
    //   ....  f32[] keyframe pool (channel value streams), immediately after the DAT2B array
    //
    // DAT2B (84 bytes), one per bone. Each of the 10 channels (rot xyzw, trans xyz, scale xyz)
    // is described by a float-pool INDEX + a constant fallback value:
    //   +0x00 i32 no                       target bone index
    //   +0x04 i32 idx_qtx,idx_qty,idx_qtz,idx_qtw   (4 x i32)
    //   +0x14 f32 qtx,qty,qtz,qtw                    (4 x f32) constant rotation
    //   +0x24 i32 idx_tx,idx_ty,idx_tz               (3 x i32)
    //   +0x30 f32 tx,ty,tz                           (3 x f32) constant translation
    //   +0x3C i32 idx_sx,idx_sy,idx_sz               (3 x i32)
    //   +0x48 f32 sx,sy,sz                           (3 x f32) constant scale
    //
    // Channel index semantics (indices are FLOAT indices from the union base at +0x0A):
    //   idx == 0                -> channel is CONSTANT: use the constant fallback value.
    //   (rot only) any of the 4 rot idx has bit 0x80000000 set -> rotation is IDENTITY (0,0,0,1).
    //   otherwise               -> animated: value at frame f = pool[idx + f]; consecutive
    //                              channels are spaced by `frame` floats (one float per frame).
    // Interpolation at runtime is SLERP for rotation and LERP for trans/scale between adjacent
    // frames; we bake one Keyframe per whole frame index [0, NumFrames) here.

    public readonly record struct AnimKeyframe(
        int Frame, System.Numerics.Quaternion Rot, System.Numerics.Vector3 Trans, System.Numerics.Vector3 Scale);

    public sealed class BoneTrack
    {
        public required int Bone { get; init; }
        public required AnimKeyframe[] Keys { get; init; }
    }

    public sealed class Animation
    {
        public required int NumBones { get; init; }
        public required int NumFrames { get; init; }
        public required float FrameSpeed { get; init; }
        public required BoneTrack[] Tracks { get; init; }
        public string Diag = "";
    }

    static int I32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b[o..]);

    /// Decode a 0x2b animation payload into per-bone tracks baked to one keyframe per frame.
    public static Animation DecodeAnimation(byte[] p)
    {
        if (p.Length < 0x0A) throw new ArgumentException("0x2b payload too small");
        int numBones = U16(p, 0x02);
        int numFrames = U16(p, 0x04);
        float speed = F(p, 0x06);
        const int poolBase = 0x0A;   // union base: DAT2B array AND float pool are addressed from here
        const int rec = 84;          // sizeof(DAT2B)

        // pool[k] = little-endian float at byte (poolBase + k*4); returns NaN if out of range.
        int poolFloats = (p.Length - poolBase) / 4;
        float Pool(int k) => (uint)k < (uint)poolFloats ? F(p, poolBase + k * 4) : float.NaN;

        var tracks = new List<BoneTrack>(numBones);
        int nonUnit = 0, sampled = 0;
        for (int b = 0; b < numBones; b++)
        {
            int o = poolBase + b * rec;
            if (o + rec > p.Length) break;

            int no = I32(p, o);
            int iqx = I32(p, o + 4), iqy = I32(p, o + 8), iqz = I32(p, o + 12), iqw = I32(p, o + 16);
            float cqx = F(p, o + 20), cqy = F(p, o + 24), cqz = F(p, o + 28), cqw = F(p, o + 32);
            int itx = I32(p, o + 36), ity = I32(p, o + 40), itz = I32(p, o + 44);
            float ctx = F(p, o + 48), cty = F(p, o + 52), ctz = F(p, o + 56);
            int isx = I32(p, o + 60), isy = I32(p, o + 64), isz = I32(p, o + 68);
            float csx = F(p, o + 72), csy = F(p, o + 76), csz = F(p, o + 80);

            // rotation identity flag: any rot channel index with the high bit set -> identity
            bool rotIdentity = ((iqx | iqy | iqz | iqw) & unchecked((int)0x80000000)) != 0;

            // A bone with the rotation-identity flag AND no translation channels is NOT animated at all — it
            // must KEEP its skeleton rest pose. Emitting an identity track here (the old behavior) wiped rest
            // rotations like the -90° Y coordinate bones, collapsing the pose. Skip it → driver leaves it at rest.
            if (rotIdentity && itx == 0 && ity == 0 && itz == 0) continue;

            // channel sampler: idx==0 -> constant; else animated stream pool[idx + frame]
            float Ch(int idx, float konst, int fr)
            {
                if (idx == 0) return konst;
                float v = Pool(idx + fr);
                return float.IsNaN(v) ? konst : v;
            }

            int keyCount = Math.Max(numFrames, 1);
            var keys = new AnimKeyframe[keyCount];
            for (int fr = 0; fr < keyCount; fr++)
            {
                System.Numerics.Quaternion q = rotIdentity
                    ? System.Numerics.Quaternion.Identity
                    : new System.Numerics.Quaternion(
                        Ch(iqx, cqx, fr), Ch(iqy, cqy, fr), Ch(iqz, cqz, fr), Ch(iqw, cqw, fr));
                var t = new System.Numerics.Vector3(Ch(itx, ctx, fr), Ch(ity, cty, fr), Ch(itz, ctz, fr));
                var s = new System.Numerics.Vector3(Ch(isx, csx, fr), Ch(isy, csy, fr), Ch(isz, csz, fr));
                keys[fr] = new AnimKeyframe(fr, q, t, s);

                sampled++;
                double m = Math.Sqrt(q.X * (double)q.X + q.Y * (double)q.Y + q.Z * (double)q.Z + q.W * (double)q.W);
                if (m < 0.98 || m > 1.02) nonUnit++;
            }
            tracks.Add(new BoneTrack { Bone = no, Keys = keys });
        }

        return new Animation
        {
            NumBones = numBones,
            NumFrames = numFrames,
            FrameSpeed = speed,
            Tracks = tracks.ToArray(),
            Diag = $"bones={tracks.Count}/{numBones} frames={numFrames} speed={speed:0.####} nonUnitQuats={nonUnit}/{sampled}",
        };
    }

    static void DecodeOne2A(byte[] p, System.Numerics.Matrix4x4[] bind, List<MeshData> outMeshes, int forceBone = -1)
    {
        if (p.Length < 0x40) return;
        ushort type = U16(p, 0x02);
        bool cloth = (type & 0x7f) == 1, indirect = (type & 0x80) != 0, flip = U16(p, 0x04) != 0;
        int offPoly = (int)BinaryPrimitives.ReadUInt32LittleEndian(p[0x06..]) * 2;
        int offBoneTbl = (int)BinaryPrimitives.ReadUInt32LittleEndian(p[0x0C..]) * 2;
        int offWeight = (int)BinaryPrimitives.ReadUInt32LittleEndian(p[0x12..]) * 2;
        int offBone = (int)BinaryPrimitives.ReadUInt32LittleEndian(p[0x18..]) * 2;
        int offVertex = (int)BinaryPrimitives.ReadUInt32LittleEndian(p[0x1E..]) * 2;
        int boneTblSuu = U16(p, 0x10);
        if (cloth) { MeshDiag += $"\n         part type=0x{type:x4} CLOTH-skip"; return; } // cloth vertices have no normals; skip for now

        int weight1 = (short)U16(p, offWeight);       // rigid (MODELVERTEX1, 24B)
        int weight2 = (short)U16(p, offWeight + 2);   // blended (MODELVERTEX2, 56B)
        int nverts = weight1 + weight2;
        if (nverts <= 0 || nverts > 200000) return;

        // Per-vertex model-space position + normal (bind pose) + skinning (4 bone idx + 4 weights).
        // Positions are the BIND-POSE world point P; GPU skinning recomputes each bone's local
        // position as bind[bone]^-1 * P, so a single P per vertex is correct even for 2-bone blends.
        var vPos = new System.Numerics.Vector3[nverts];
        var vNrm = new System.Numerics.Vector3[nverts];
        var vBone = new int[nverts * 4];
        var vWt = new float[nverts * 4];
        // FFXI mirror (ref: galkareeve/ffxi TDWCharacter.cpp buildBindPosVertex). A symmetric part stores ONE
        // side and is drawn TWICE. Each bone-reference u16 packs three fields:
        //   bits 0-6   = LEFT  bone-table index  (normal pass)
        //   bits 7-13  = RIGHT bone-table index  (mirror pass)
        //   bits 14-15 = flg   (mirror axis: 1=X, 2=Y, 3=Z) applied by REFLECTING the local vertex on that axis
        // The mirror pass is emitted only when the mesh flip flag (@0x04) is set (asymmetric parts like the
        // face have flip=0 and render once).
        var vPosM = new System.Numerics.Vector3[nverts];
        var vNrmM = new System.Numerics.Vector3[nverts];
        var vBoneM = new int[nverts * 4];
        bool doFlip = flip && MirrorEnabled;

        int boneRaw(int vtx, int corner) { int bo = offBone + (vtx * 2 + corner) * 2; return bo + 2 <= p.Length ? U16(p, bo) : 0; }
        int resolve(int idx) { if (indirect) { int to = offBoneTbl + idx * 2; idx = (to + 2 <= p.Length && idx < boneTblSuu) ? U16(p, to) : 0; } return idx >= 0 && idx < bind.Length ? idx : 0; }
        // forceBone (>=0): rigid weapon — every vertex binds to that one bone (the hand grip), ignoring the
        // part's own bone table (which points at a root bone and would drop the weapon at the feet).
        bool forced = forceBone >= 0 && forceBone < bind.Length;
        int boneId(int vtx, int corner) => forced ? forceBone : resolve(boneRaw(vtx, corner) & 0x7f);          // LEFT (normal)
        int boneIdR(int vtx, int corner) => forced ? forceBone : resolve((boneRaw(vtx, corner) >> 7) & 0x7f);  // RIGHT (mirror)
        int boneFlg(int vtx, int corner) => (boneRaw(vtx, corner) >> 14) & 0x3;
        System.Numerics.Vector3 Mir(System.Numerics.Vector3 v, int flg) =>
            flg == 1 ? new System.Numerics.Vector3(-v.X, v.Y, v.Z)
          : flg == 2 ? new System.Numerics.Vector3(v.X, -v.Y, v.Z)
          : flg == 3 ? new System.Numerics.Vector3(v.X, v.Y, -v.Z) : v;
        System.Numerics.Vector3 Rot(System.Numerics.Vector3 v, in System.Numerics.Matrix4x4 m) =>
            System.Numerics.Vector3.TransformNormal(v, m);

        int vp = offVertex;
        for (int i = 0; i < weight1; i++, vp += 24)   // rigid
        {
            var lp = new System.Numerics.Vector3(F(p, vp), F(p, vp + 4), F(p, vp + 8));
            var ln = new System.Numerics.Vector3(F(p, vp + 12), F(p, vp + 16), F(p, vp + 20));
            int rb = boneId(i, 0);
            var m = bind[rb];
            vPos[i] = Rot(lp, m) + m.Translation;
            vNrm[i] = System.Numerics.Vector3.Normalize(Rot(ln, m));
            vBone[i * 4] = rb; vWt[i * 4] = 1f;
            if (doFlip) // mirror pass: RIGHT bone + reflect the local vertex on the flg axis
            {
                int rbm = boneIdR(i, 0); var mm = bind[rbm]; int flg = boneFlg(i, 0);
                vPosM[i] = Rot(Mir(lp, flg), mm) + mm.Translation;
                vNrmM[i] = System.Numerics.Vector3.Normalize(Rot(Mir(ln, flg), mm));
                vBoneM[i * 4] = rbm;
            }
        }
        for (int j = 0; j < weight2; j++, vp += 56)   // blended (2-bone), pre-weighted
        {
            int vi = weight1 + j;
            float x1 = F(p, vp), x2 = F(p, vp + 4), y1 = F(p, vp + 8), y2 = F(p, vp + 12), z1 = F(p, vp + 16), z2 = F(p, vp + 20);
            float w1 = F(p, vp + 24), w2 = F(p, vp + 28);
            var h1 = new System.Numerics.Vector3(F(p, vp + 32), F(p, vp + 40), F(p, vp + 48));
            var h2 = new System.Numerics.Vector3(F(p, vp + 36), F(p, vp + 44), F(p, vp + 52));
            int b1 = boneId(vi, 0), b2 = boneId(vi, 1);
            var m1 = bind[b1]; var m2 = bind[b2];
            vPos[vi] = Rot(new System.Numerics.Vector3(x1, y1, z1), m1) + m1.Translation * w1
                     + Rot(new System.Numerics.Vector3(x2, y2, z2), m2) + m2.Translation * w2;
            vNrm[vi] = System.Numerics.Vector3.Normalize(Rot(h1, m1) + Rot(h2, m2));
            // normalize the two blend weights to sum 1 for GPU linear-blend skinning
            float ws12 = w1 + w2; if (ws12 < 1e-4f) { w1 = 1f; w2 = 0f; ws12 = 1f; }
            vBone[vi * 4] = b1; vBone[vi * 4 + 1] = b2;
            vWt[vi * 4] = w1 / ws12; vWt[vi * 4 + 1] = w2 / ws12;
            if (doFlip) // mirror pass: RIGHT bones + reflect each local vertex on its flg axis
            {
                int b1m = boneIdR(vi, 0), b2m = boneIdR(vi, 1); int f1 = boneFlg(vi, 0), f2 = boneFlg(vi, 1);
                var m1m = bind[b1m]; var m2m = bind[b2m];
                vPosM[vi] = Rot(Mir(new System.Numerics.Vector3(x1, y1, z1), f1), m1m) + m1m.Translation * w1
                          + Rot(Mir(new System.Numerics.Vector3(x2, y2, z2), f2), m2m) + m2m.Translation * w2;
                vNrmM[vi] = System.Numerics.Vector3.Normalize(Rot(Mir(h1, f1), m1m) + Rot(Mir(h2, f2), m2m));
                vBoneM[vi * 4] = b1m; vBoneM[vi * 4 + 1] = b2m;
            }
        }

        // Poly display-list -> triangles (per-corner UVs), grouped by bound texture.
        var acc = new Dictionary<string, (List<float> pos, List<float> nrm, List<float> uv, List<int> idx, List<int> bone, List<float> wt)>();
        string curTex = "";
        (List<float>, List<float>, List<float>, List<int>, List<int>, List<float>) A(string k)
        {
            if (!acc.TryGetValue(k, out var a)) { a = (new(), new(), new(), new(), new(), new()); acc[k] = a; }
            return a;
        }
        void Emit(string tex, int i0, float u0, float v0, int i1, float u1, float v1, int i2, float u2, float v2)
        {
            if ((uint)i0 >= nverts || (uint)i1 >= nverts || (uint)i2 >= nverts) return;
            var (po, no, uvo, io, bo, wo) = A(tex);
            int b = po.Count / 3;
            void V(int vi, float u, float v)
            {
                po.Add(vPos[vi].X); po.Add(vPos[vi].Y); po.Add(vPos[vi].Z);
                no.Add(vNrm[vi].X); no.Add(vNrm[vi].Y); no.Add(vNrm[vi].Z);
                uvo.Add(u); uvo.Add(v);
                bo.Add(vBone[vi * 4]); bo.Add(vBone[vi * 4 + 1]); bo.Add(vBone[vi * 4 + 2]); bo.Add(vBone[vi * 4 + 3]);
                wo.Add(vWt[vi * 4]); wo.Add(vWt[vi * 4 + 1]); wo.Add(vWt[vi * 4 + 2]); wo.Add(vWt[vi * 4 + 3]);
            }
            V(i2, u2, v2); V(i1, u1, v1); V(i0, u0, v0); // reversed winding (galkareeve list order)
            io.Add(b); io.Add(b + 1); io.Add(b + 2);

            // Mirror pass: re-emit the triangle skinned to the RIGHT bones (reflected) — the other side.
            if (doFlip)
            {
                int bm = po.Count / 3;
                void VM(int vi, float u, float v)
                {
                    po.Add(vPosM[vi].X); po.Add(vPosM[vi].Y); po.Add(vPosM[vi].Z);
                    no.Add(vNrmM[vi].X); no.Add(vNrmM[vi].Y); no.Add(vNrmM[vi].Z);
                    uvo.Add(u); uvo.Add(v);
                    bo.Add(vBoneM[vi * 4]); bo.Add(vBoneM[vi * 4 + 1]); bo.Add(vBoneM[vi * 4 + 2]); bo.Add(vBoneM[vi * 4 + 3]);
                    wo.Add(vWt[vi * 4]); wo.Add(vWt[vi * 4 + 1]); wo.Add(vWt[vi * 4 + 2]); wo.Add(vWt[vi * 4 + 3]);
                }
                VM(i0, u0, v0); VM(i1, u1, v1); VM(i2, u2, v2); // opposite winding (mirror flips orientation)
                io.Add(bm); io.Add(bm + 1); io.Add(bm + 2);
            }
        }

        // One (index, u, v) corner = 10 bytes: idx u16 @+0, u f32 @+2, v f32 @+6. A triangle is 3 such
        // corners packed back-to-back (30 B); a strip continues one corner (10 B) per extra triangle.
        (int i, float u, float v) C(int o) => (U16(p, o), F(p, o + 2), F(p, o + 6));

        int q = offPoly;
        int guard = 0, nT = 0, nST = 0; ushort termWf = 0;
        // The poly list ends where the next section begins (the header lays out poly < boneTbl < weight <
        // bone < vertex). Bound the walk there so we never march into bone/vertex bytes (the old code relied
        // on hitting an unknown token; the 0xFFFF at the poly end is just a 2-byte terminator before boneTbl).
        int polyEnd = p.Length;
        foreach (int o in new[] { offBoneTbl, offWeight, offBone, offVertex })
            if (o > offPoly && o < polyEnd) polyEnd = o;
        while (q + 4 <= polyEnd && guard++ < 100000)
        {
            ushort wf = U16(p, q), ws = U16(p, q + 2);
            if ((wf & 0x80F0) == 0x8010) { q += 0x2e; }
            else if ((wf & 0x80F0) == 0x8000)
            {
                // texture bind: 16-byte name at q+2; keep the trailing token as the id
                int no = q + 2; int end = no; while (end < no + 16 && end < p.Length && p[end] != 0) end++;
                curTex = System.Text.Encoding.ASCII.GetString(p[no..end]).Trim();
                q += 0x12;
            }
            else if (wf == 0x0054 && ws is > 0 and < 30000) // 'T' triangle list — ws triangles, 30B each
            {
                q += 4; nT += ws;
                for (int t = 0; t < ws && q + 30 <= p.Length; t++, q += 30)
                    Emit(curTex, U16(p, q), F(p, q + 6), F(p, q + 10), U16(p, q + 2), F(p, q + 14), F(p, q + 18), U16(p, q + 4), F(p, q + 22), F(p, q + 26));
            }
            else if (wf == 0x5453 && ws is > 0 and < 30000) // 'ST' triangle strip; ws = triangle count
            {
                q += 4; nST += ws;
                if (q + 30 > p.Length) break;
                // first triangle: packed indices + 3 UV pairs (same 30B layout as the T list)
                int a0 = U16(p, q), a1 = U16(p, q + 2), a2 = U16(p, q + 4);
                float au0 = F(p, q + 6), av0 = F(p, q + 10), au1 = F(p, q + 14), av1 = F(p, q + 18), au2 = F(p, q + 22), av2 = F(p, q + 26);
                q += 30;
                Emit(curTex, a0, au0, av0, a1, au1, av1, a2, au2, av2);
                // continuation: one interleaved 10-byte corner (idx@0, u@2, v@6) per subsequent triangle
                for (int pos = 3; pos < ws + 2 && q + 10 <= p.Length; pos++, q += 10)
                {
                    var (ai, uu, vv) = C(q);
                    if ((pos & 1) == 1) Emit(curTex, a1, au1, av1, a2, au2, av2, ai, uu, vv);
                    else Emit(curTex, a2, au2, av2, a1, au1, av1, ai, uu, vv);
                    a1 = a2; au1 = au2; av1 = av2; a2 = ai; au2 = uu; av2 = vv;
                }
            }
            else if (wf == 0x4353) { q += ws * 20 + 0x0C; }
            else if (wf == 0x0043) { q += ws * 10 + 0x04; }
            else { termWf = wf; break; } // unknown token before the section end = malformed; stop
        }
        MeshDiag += $"\n         part type=0x{type:x4} T={nT} ST={nST} verts={nverts}(r{weight1}/b{weight2}) flip={flip}";
        if (VerboseDiag)
        {
            var used = new System.Collections.Generic.SortedSet<int>();
            for (int i = 0; i < nverts; i++) { used.Add(boneId(i, 0)); if (i >= weight1) used.Add(boneId(i, 1)); }
            var tbl = new System.Collections.Generic.List<int>();
            for (int t = 0; t < boneTblSuu && offBoneTbl + t * 2 + 2 <= p.Length; t++) tbl.Add(U16(p, offBoneTbl + t * 2));
            float xmn = 1e9f, xmx = -1e9f, ymn = 1e9f, ymx = -1e9f, zmn = 1e9f, zmx = -1e9f;
            foreach (var (_, a) in acc) for (int i = 0; i + 2 < a.pos.Count; i += 3)
            { float x = a.pos[i], y = a.pos[i + 1], z = a.pos[i + 2]; if (x < xmn) xmn = x; if (x > xmx) xmx = x; if (y < ymn) ymn = y; if (y > ymx) ymx = y; if (z < zmn) zmn = z; if (z > zmx) zmx = z; }
            MeshDiag += $"\n           indirect={indirect} boneTblSuu={boneTblSuu} usedBones=[{string.Join(",", used)}] boneTbl=[{string.Join(",", tbl)}] bbox X[{xmn:0.0},{xmx:0.0}] Y[{ymn:0.0},{ymx:0.0}] Z[{zmn:0.0},{zmx:0.0}]";
        }

        foreach (var (tex, a) in acc)
            if (a.pos.Count > 0)
                outMeshes.Add(new MeshData
                {
                    Positions = a.pos.ToArray(), Normals = a.nrm.ToArray(), Uvs = a.uv.ToArray(), Indices = a.idx.ToArray(),
                    TextureId = tex, BoneIndices = a.bone.ToArray(), BoneWeights = a.wt.ToArray(),
                });
    }
}
