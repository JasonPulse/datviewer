using System.Buffers.Binary;
using System.Text;

namespace Vellichor.Dat;

/// <summary>Little-endian primitive readers over a byte[] (FFXI DATs are LE throughout).</summary>
internal static class Bin
{
    public static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
    public static int I32(byte[] d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o));
    public static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o));
    public static float F32(byte[] d, int o) => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o));

    /// <summary>Fixed-length name; trims trailing spaces and NULs (FFXI ids use both).</summary>
    public static string Name(byte[] d, int o, int len)
    {
        int end = o + len;
        var sb = new StringBuilder(len);
        for (int i = o; i < end && d[i] != 0; i++) sb.Append((char)d[i]);
        return sb.ToString().TrimEnd();
    }
}
