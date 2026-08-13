using System;

namespace DatViewer;

/// <summary>Decoded BGW audio: interleaved little-endian PCM16, ready for a Godot AudioStreamWav.</summary>
public sealed class BgwAudio
{
    public byte[] Pcm = Array.Empty<byte>(); // interleaved 16-bit LE (L,R,L,R… for stereo)
    public int SampleRate;
    public int Channels;
    public int LoopStartSample = -1;         // -1 = no loop
    public int SampleCount;                  // per channel
    public double Seconds => SampleRate > 0 ? (double)SampleCount / SampleRate : 0;
}

/// <summary>
/// Decoder for FINAL FANTASY XI <c>.bgw</c> music files (the streamed music/ambient container).
///
/// Two codecs exist (see vgmstream's bgw.c):
///   • codec 0 = PlayStation ADPCM (configurable-frame variant, no flag byte) — the "playable" tracks
///     (early expansions: sound/sound2/sound3). Decoded here.
///   • codec 3 = ATRAC3 (Sony, encrypted) — later expansions (sound4+). Not decodable without an
///     ATRAC3 codec, exactly as the original Altana Viewer notes; reported as unsupported.
///
/// Header (little-endian): magic "BGMStream\0\0\0" (0x00); codec u32 (0x0c); file_size u32 (0x10);
/// block_size u32 (0x18); loop_start s32 (0x1c); sample_rate = (u32@0x20 + u32@0x24) &amp; 0x7FFFFFFF;
/// start_offset u32 (0x28); channels s8 (0x2e); block_align u8 (0x2f).
/// PSX frame_size = block_align/2 + 1; samples/frame = block_align; frames/channel = block_size.
/// </summary>
public static class BgwDecoder
{
    public enum Result { Ok, NotBgw, Atrac3Unsupported, UnknownCodec, Empty }

    // PS-ADPCM filter coefficients (spec_coef * 64), first 5 rows (the rest are PS3-only).
    private static readonly int[,] Coefs = { { 0, 0 }, { 60, 0 }, { 115, -52 }, { 98, -55 }, { 122, -60 } };

    public static Result TryDecode(byte[] data, out BgwAudio? audio)
    {
        audio = null;
        if (data.Length < 0x30) return Result.NotBgw;
        // "BGMStream" — bgw.c checks "BGMS","trea","m\0\0\0"
        if (data[0] != 'B' || data[1] != 'G' || data[2] != 'M' || data[3] != 'S' ||
            data[4] != 't' || data[5] != 'r' || data[6] != 'e' || data[7] != 'a' || data[8] != 'm')
            return Result.NotBgw;

        uint codec = U32(data, 0x0c);
        uint blockSize = U32(data, 0x18);
        int loopStart = (int)U32(data, 0x1c);
        int sampleRate = (int)((U32(data, 0x20) + U32(data, 0x24)) & 0x7FFFFFFF);
        int startOffset = (int)U32(data, 0x28);
        int channels = (sbyte)data[0x2e];
        int blockAlign = data[0x2f];

        if (codec == 3) return Result.Atrac3Unsupported;
        if (codec != 0) return Result.UnknownCodec;
        if (channels is < 1 or > 2 || blockAlign < 2 || sampleRate <= 0 || startOffset >= data.Length)
            return Result.NotBgw;

        int frameSize = blockAlign / 2 + 1;
        int samplesPerFrame = blockAlign;                  // = (frameSize-1)*2
        int available = (data.Length - startOffset) / (frameSize * channels);
        int framesPerChannel = Math.Min((int)blockSize, available);
        if (framesPerChannel <= 0) return Result.Empty;

        int sampleCount = framesPerChannel * samplesPerFrame;
        var pcm = new short[sampleCount * channels];       // interleaved

        for (int c = 0; c < channels; c++)
        {
            int hist1 = 0, hist2 = 0;
            for (int f = 0; f < framesPerChannel; f++)
            {
                int frameOff = startOffset + (f * channels + c) * frameSize;
                int header = data[frameOff];
                int coefIndex = (header >> 4) & 0xf;
                int shift = header & 0xf;
                if (coefIndex > 5) coefIndex = 0;          // upper filters are PS3-only
                if (shift > 12) shift = 9;

                for (int i = 0; i < samplesPerFrame; i++)
                {
                    int nibbles = data[frameOff + 1 + i / 2];
                    int n = (i & 1) != 0 ? (nibbles >> 4) & 0xf : nibbles & 0xf;
                    int sample = (short)((n << 12) & 0xf000) >> shift;         // 4-bit → 16-bit sign-extend, scale
                    sample += (Coefs[coefIndex, 0] * hist1 + Coefs[coefIndex, 1] * hist2) >> 6;
                    sample = Math.Clamp(sample, short.MinValue, short.MaxValue);
                    pcm[(f * samplesPerFrame + i) * channels + c] = (short)sample;
                    hist2 = hist1;
                    hist1 = sample;
                }
            }
        }

        var bytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);  // little-endian on all target platforms

        audio = new BgwAudio
        {
            Pcm = bytes,
            SampleRate = sampleRate,
            Channels = channels,
            SampleCount = sampleCount,
            LoopStartSample = loopStart > 0 ? (loopStart - 1) * blockAlign : -1,
        };
        return Result.Ok;
    }

    private static uint U32(byte[] d, int o) =>
        (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
}
