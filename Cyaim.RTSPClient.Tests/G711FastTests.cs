using Cyaim.RTSPClient.Common;
using Xunit;

namespace Cyaim.RTSPClient.Tests;

/// <summary>
/// G.711 编解码：SIMD 与标量全量等价、回环误差、ITU 标准已知值
/// </summary>
public class G711FastTests
{
    [Fact]
    public void EncodeALaw_批量与单样本全量等价_覆盖全部输入()
    {
        var input = new short[65536];
        for (int i = 0; i < 65536; i++) input[i] = (short)(i - 32768);

        var batch = new byte[input.Length];
        G711Fast.EncodeALaw(input, batch);

        for (int i = 0; i < input.Length; i++)
        {
            byte scalar = G711Fast.LinearToALaw(input[i]);
            if (batch[i] != scalar)
                Assert.Fail($"A-law mismatch at input {input[i]}: batch=0x{batch[i]:X2}, scalar=0x{scalar:X2}");
        }
    }

    [Fact]
    public void EncodeMuLaw_批量与单样本全量等价_覆盖全部输入()
    {
        var input = new short[65536];
        for (int i = 0; i < 65536; i++) input[i] = (short)(i - 32768);

        var batch = new byte[input.Length];
        G711Fast.EncodeMuLaw(input, batch);

        for (int i = 0; i < input.Length; i++)
        {
            byte scalar = G711Fast.LinearToMuLaw(input[i]);
            if (batch[i] != scalar)
                Assert.Fail($"μ-law mismatch at input {input[i]}: batch=0x{batch[i]:X2}, scalar=0x{scalar:X2}");
        }
    }

    [Fact]
    public void ALaw_编解码回环误差在段量化容差内()
    {
        for (int i = 0; i < 65536; i += 7)
        {
            short x = (short)(i - 32768);
            short decoded = G711Fast.ALawToLinear(G711Fast.LinearToALaw(x));
            int clamped = Math.Clamp((int)x, -32635, 32635);
            int tolerance = Math.Max(16, Math.Abs(clamped) / 16);
            Assert.True(Math.Abs(decoded - clamped) <= tolerance,
                $"A-law roundtrip {x} -> {decoded} (tolerance {tolerance})");
        }
    }

    [Fact]
    public void MuLaw_编解码回环误差在段量化容差内()
    {
        for (int i = 0; i < 65536; i += 7)
        {
            short x = (short)(i - 32768);
            short decoded = G711Fast.MuLawToLinear(G711Fast.LinearToMuLaw(x));
            int clamped = Math.Clamp((int)x, -32635, 32635);
            int tolerance = Math.Max(36, Math.Abs(clamped) / 8);
            Assert.True(Math.Abs(decoded - clamped) <= tolerance,
                $"μ-law roundtrip {x} -> {decoded} (tolerance {tolerance})");
        }
    }

    [Theory]
    [InlineData(0, 0xD5)]   // ITU-T G.711：正零 → 0xD5
    public void ALaw_ITU标准已知值(short pcm, byte expected)
    {
        Assert.Equal(expected, G711Fast.LinearToALaw(pcm));
    }

    [Theory]
    [InlineData(0, 0xFF)]   // ITU-T G.711：正零 → 0xFF
    public void MuLaw_ITU标准已知值(short pcm, byte expected)
    {
        Assert.Equal(expected, G711Fast.LinearToMuLaw(pcm));
    }

    [Fact]
    public void Decode_批量与单样本等价_覆盖全部256码字()
    {
        var codes = new byte[256];
        for (int i = 0; i < 256; i++) codes[i] = (byte)i;

        var pcmA = new short[256];
        var pcmU = new short[256];
        G711Fast.DecodeALaw(codes, pcmA);
        G711Fast.DecodeMuLaw(codes, pcmU);

        for (int i = 0; i < 256; i++)
        {
            Assert.Equal(G711Fast.ALawToLinear(codes[i]), pcmA[i]);
            Assert.Equal(G711Fast.MuLawToLinear(codes[i]), pcmU[i]);
        }
    }
}
