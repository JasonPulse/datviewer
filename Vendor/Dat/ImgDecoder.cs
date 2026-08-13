namespace Vellichor.Dat;

/// <summary>A decoded IMG (type 0x20) texture: top-down RGBA8888, width×height, keyed by id.</summary>
public sealed class ImgTexture
{
    public required string Id { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Rgba { get; init; } // Width*Height*4, RGBA8, top-down
}

/// <summary>
/// Decodes FFXI IMG texture chunks. Header (IMGINFO / a Win32 BITMAPINFOHEADER, pack 1):
///   flg@0, id[16]@1, dwnazo1@0x11(==40), imgx@0x15, imgy@0x19, dwnazo2[6]@0x1D,
///   widthbyte@0x35, then a 256-entry region@0x39.
/// Format: 4 bytes @0x39 == "DXT1"/"DXT3" → raw DXT blocks at @0x45 (no DDS header);
/// else dwnazo2[0] (@0x1D) selects 0x80001=8-bit paletted / 0x200001=32-bit direct, and
/// widthbyte (@0x35) gives palette entry depth (32 or 16-bit). Ported from galkareeve
/// GetBMPImage/GetRGB1/GetRGB3 + xi-tinkerer image.rs. DXT is decoded to RGBA here because
/// Apple Silicon / Metal does not support BC/S3TC textures.
/// </summary>
public static class ImgDecoder
{
    private const int Pal = 0x39;   // 57
    private const int PixPaletted = 0x439; // 1081 (after 256-entry palette)

    public static ImgTexture? Decode(byte[] p)
    {
        if (p.Length < Pal || Bin.I32(p, 0x11) != 40) return null;
        int w = Bin.I32(p, 0x15), h = Bin.I32(p, 0x19);
        if (w is < 4 or > 4096 || h is < 4 or > 4096) return null;
        string id = Bin.Name(p, 0x01, 16);
        var rgba = new byte[w * h * 4];

        // FourCC is stored byte-reversed on disk ("1TXD"/"3TXD"); as a LE DWORD it reads
        // 'DXT1'/'DXT3'. (This is the endianness quirk that made every DXT look paletted.)
        uint fourcc = p.Length >= Pal + 4 ? Bin.U32(p, Pal) : 0;
        bool dxt1 = fourcc == 0x44585431; // "1TXD"
        bool dxt3 = fourcc == 0x44585433; // "3TXD"

        if (dxt1 || dxt3) DecodeDxt(p, w, h, rgba, dxt3);
        else if ((uint)Bin.U32(p, 0x1D) == 0x200001) DecodeDirect32(p, w, h, rgba);
        else DecodePaletted(p, w, h, rgba);

        return new ImgTexture { Id = id, Width = w, Height = h, Rgba = rgba };
    }

    // 8-bit paletted; palette entries 32-bit (BGRA, alpha 0..0x80) or 16-bit (A1 B5 G5 R5).
    private static void DecodePaletted(byte[] p, int w, int h, byte[] rgba)
    {
        byte flg = p[0];
        int palBase = flg == 0xB1 ? Pal + 4 : Pal;
        int pixBase = flg == 0xB1 ? PixPaletted + 4 : PixPaletted;
        bool pal16 = Bin.U32(p, 0x35) == 16;
        for (int i = 0; i < w * h; i++)
        {
            int idx = pixBase + i < p.Length ? p[pixBase + i] : 0;
            int o = i * 4;
            if (pal16)
            {
                int e = palBase + idx * 2;
                if (e + 2 > p.Length) continue;
                ushort c = Bin.U16(p, e);
                rgba[o] = (byte)((c & 0x1F) << 3);
                rgba[o + 1] = (byte)(((c >> 5) & 0x1F) << 3);
                rgba[o + 2] = (byte)(((c >> 10) & 0x1F) << 3);
                rgba[o + 3] = (byte)((c & 0x8000) != 0 ? 255 : 0);
            }
            else
            {
                int e = palBase + idx * 4;
                if (e + 4 > p.Length) continue;
                rgba[o] = p[e + 2];       // R
                rgba[o + 1] = p[e + 1];   // G
                rgba[o + 2] = p[e];       // B
                rgba[o + 3] = (byte)System.Math.Min(255, p[e + 3] * 2); // A: 0..0x80 -> 0..255
            }
        }
    }

    // Direct 32-bit color: DWORDs from the palet offset, in-file order B,G,R,A.
    private static void DecodeDirect32(byte[] p, int w, int h, byte[] rgba)
    {
        for (int i = 0; i < w * h; i++)
        {
            int e = Pal + i * 4, o = i * 4;
            if (e + 4 > p.Length) break;
            rgba[o] = p[e + 2]; rgba[o + 1] = p[e + 1]; rgba[o + 2] = p[e];
            rgba[o + 3] = (byte)System.Math.Min(255, p[e + 3] * 2);
        }
    }

    // Raw DXT blocks at offset 0x45 (FourCC + 8 reserved skipped), row-major 4x4 blocks.
    private static void DecodeDxt(byte[] p, int w, int h, byte[] rgba, bool dxt3)
    {
        int img = Pal + 12; // 0x45 / 69
        int bw = w / 4, bh = h / 4, blockSize = dxt3 ? 16 : 8;
        var c = new byte[16 * 4]; // 16 texels × RGBA, filled per block
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int bp = img + (by * bw + bx) * blockSize;
                if (bp + blockSize > p.Length) return;
                int colorOff = dxt3 ? bp + 8 : bp;
                DecodeColorBlock(p, colorOff, c, !dxt3); // DXT1 allows 1-bit alpha
                for (int k = 0; k < 16; k++)
                {
                    int px = bx * 4 + (k & 3), py = by * 4 + (k >> 2);
                    int o = (py * w + px) * 4;
                    rgba[o] = c[k * 4]; rgba[o + 1] = c[k * 4 + 1]; rgba[o + 2] = c[k * 4 + 2];
                    // DXT3: explicit 4-bit alpha (8 bytes at bp). FFXI's "opaque" is ~half-max
                    // (0..8 like the 8-bit 0..128 convention), so scale *32 (clamped) -> opaque=255.
                    rgba[o + 3] = dxt3
                        ? (byte)System.Math.Min(255, ((p[bp + (k >> 1)] >> ((k & 1) * 4)) & 0xF) * 32)
                        : c[k * 4 + 3];
                }
            }
    }

    // BC1 color block (8 bytes at off): endpoints c0,c1 (RGB565) + 2-bit indices.
    private static void DecodeColorBlock(byte[] p, int off, byte[] outRgba, bool dxt1Alpha)
    {
        ushort c0 = Bin.U16(p, off), c1 = Bin.U16(p, off + 2);
        Span<byte> r = stackalloc byte[4];
        Span<byte> g = stackalloc byte[4];
        Span<byte> b = stackalloc byte[4];
        Span<byte> a = stackalloc byte[4];
        Rgb565(c0, out r[0], out g[0], out b[0]);
        Rgb565(c1, out r[1], out g[1], out b[1]);
        a[0] = a[1] = a[2] = a[3] = 255;
        if (c0 > c1 || !dxt1Alpha)
        {
            r[2] = (byte)((2 * r[0] + r[1]) / 3); g[2] = (byte)((2 * g[0] + g[1]) / 3); b[2] = (byte)((2 * b[0] + b[1]) / 3);
            r[3] = (byte)((r[0] + 2 * r[1]) / 3); g[3] = (byte)((g[0] + 2 * g[1]) / 3); b[3] = (byte)((b[0] + 2 * b[1]) / 3);
        }
        else
        {
            r[2] = (byte)((r[0] + r[1]) / 2); g[2] = (byte)((g[0] + g[1]) / 2); b[2] = (byte)((b[0] + b[1]) / 2);
            r[3] = g[3] = b[3] = 0; a[3] = 0; // transparent
        }
        for (int k = 0; k < 16; k++)
        {
            int sel = (p[off + 4 + (k >> 2)] >> ((k & 3) * 2)) & 3;
            outRgba[k * 4] = r[sel]; outRgba[k * 4 + 1] = g[sel]; outRgba[k * 4 + 2] = b[sel]; outRgba[k * 4 + 3] = a[sel];
        }
    }

    private static void Rgb565(ushort c, out byte r, out byte g, out byte b)
    {
        r = (byte)(((c >> 11) & 0x1F) * 255 / 31);
        g = (byte)(((c >> 5) & 0x3F) * 255 / 63);
        b = (byte)((c & 0x1F) * 255 / 31);
    }
}
