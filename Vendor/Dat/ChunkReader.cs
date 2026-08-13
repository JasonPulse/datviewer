using System.Text;

namespace Vellichor.Dat;

/// <summary>One chunk in a DAT chunk-container (see docs/dat-format.md §4.1).</summary>
public readonly record struct DatChunk(string Name, int Type, int LengthBytes, int Offset)
{
    /// <summary>Offset of this chunk's payload (header is a fixed 16 bytes).</summary>
    public int PayloadOffset => Offset + 16;
    public int PayloadLength => LengthBytes - 16;
}

/// <summary>
/// Walks a DAT that is a linear sequence of 16-byte-aligned chunks. Each chunk header:
///   name[4], then a uint32 LE bitfield: type = bits 0..6, next = bits 7..25 (length in
///   16-byte units). Payload begins at +16; advance by next*16.
/// Ported from galkareeve `FFXIFile::NextData` / `TDWAnalysis.h` (docs/dat-format.md §4.1).
/// Defensive: a non-positive or overrunning length stops the walk rather than guessing,
/// so a non-chunked DAT (e.g. a fixed-record data blob) yields few/zero chunks instead of
/// marching through garbage.
/// </summary>
public static class ChunkReader
{
    public static List<DatChunk> Walk(byte[] data, int maxChunks = 200_000)
    {
        var chunks = new List<DatChunk>();
        int pos = 0;
        while (pos + 16 <= data.Length && chunks.Count < maxChunks)
        {
            string name = MakeName(data, pos);
            uint packed = (uint)(data[pos + 4] | (data[pos + 5] << 8) | (data[pos + 6] << 16) | (data[pos + 7] << 24));
            int type = (int)(packed & 0x7F);
            int nextUnits = (int)((packed >> 7) & 0x7FFFF);
            int lenBytes = nextUnits * 16;
            if (lenBytes <= 0 || pos + lenBytes > data.Length) break; // end / not a chunk stream
            chunks.Add(new DatChunk(name, type, lenBytes, pos));
            pos += lenBytes;
        }
        return chunks;
    }

    /// <summary>True if the file plausibly is a chunk container (first header parses and
    /// the walk covers most of the file).</summary>
    public static bool LooksChunked(byte[] data)
    {
        var chunks = Walk(data);
        if (chunks.Count == 0) return false;
        var last = chunks[^1];
        int covered = last.Offset + last.LengthBytes;
        return covered >= data.Length - 16; // walked cleanly to (near) the end
    }

    private static string MakeName(byte[] d, int off)
    {
        var sb = new StringBuilder(4);
        for (int i = 0; i < 4; i++)
        {
            byte b = d[off + i];
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }
        return sb.ToString();
    }
}
