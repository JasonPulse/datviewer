namespace Vellichor.Dat;

/// <summary>One placed instance from an MZB (type 0x1c) object list (SMZBBlock100, 100 bytes).</summary>
public sealed record MzbInstance(
    string Id,                 // 16-byte MMB model id (matches MmbModel.MmbId)
    float PosX, float PosY, float PosZ,
    float RotX, float RotY, float RotZ,   // radians, applied X then Y then Z
    float ScaleX, float ScaleY, float ScaleZ);

/// <summary>
/// Decodes the MZB object-placement list. Layout (docs/dat-format.md §... / galkareeve
/// OBJINFO ≡ xi ModelBlockInstance): count = u32@payload+4 &amp; 0xFFFFFF, entries are 100
/// bytes each from payload+32; per entry id@0x00(16), pos@0x10, rot@0x1C, scale@0x28.
/// </summary>
public static class MzbDecoder
{
    public static List<MzbInstance> Decode(byte[] payload)
    {
        var list = new List<MzbInstance>();
        if (payload.Length < 32) return list;
        DatCrypt.DecodeMzb(payload, payload.Length); // deobfuscate in place before parsing
        int count = (int)(Bin.U32(payload, 4) & 0xFFFFFF);
        int off = 32;
        for (int i = 0; i < count && off + 100 <= payload.Length; i++, off += 100)
        {
            list.Add(new MzbInstance(
                Bin.Name(payload, off, 16),
                Bin.F32(payload, off + 0x10), Bin.F32(payload, off + 0x14), Bin.F32(payload, off + 0x18),
                Bin.F32(payload, off + 0x1C), Bin.F32(payload, off + 0x20), Bin.F32(payload, off + 0x24),
                Bin.F32(payload, off + 0x28), Bin.F32(payload, off + 0x2C), Bin.F32(payload, off + 0x30)));
        }
        return list;
    }
}
