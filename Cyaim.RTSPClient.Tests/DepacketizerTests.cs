using Cyaim.RTSPClient.Rtp;
using Xunit;

namespace Cyaim.RTSPClient.Tests;

/// <summary>
/// H.264 FU-A 重组：大关键帧、丢包缺口、SDP 参数集注入、marker 透出
/// </summary>
public class H264DepacketizerTests
{
    /// <summary>
    /// 把一个 NAL 切成 FU-A 包序列（可选丢弃中间分片）
    /// </summary>
    private static List<RTPPacket> Fragment(byte[] nalData, ushort startSeq, uint ts, bool dropMiddle = false)
    {
        var packets = new List<RTPPacket>();
        byte fuIndicator = (byte)((nalData[0] & 0xE0) | 28);
        byte nalType = (byte)(nalData[0] & 0x1F);
        const int chunk = 1400;
        int offset = 1;
        ushort seq = startSeq;
        bool first = true;
        int totalFragments = (nalData.Length - 1 + chunk - 1) / chunk;
        int index = 0;

        while (offset < nalData.Length)
        {
            int len = Math.Min(chunk, nalData.Length - offset);
            bool last = offset + len >= nalData.Length;
            var payload = new byte[2 + len];
            payload[0] = fuIndicator;
            payload[1] = (byte)(nalType | (first ? 0x80 : 0) | (last ? 0x40 : 0));
            Array.Copy(nalData, offset, payload, 2, len);

            bool drop = dropMiddle && index == totalFragments / 2;
            if (!drop)
            {
                packets.Add(new RTPPacket(2, false, false, 0, last, 96, seq, ts, 0x1234, Array.Empty<uint>(),
                    payload, 0, StreamType.Video, payload));
            }
            seq++;
            offset += len;
            first = false;
            index++;
        }
        return packets;
    }

    private static byte[] MakeIdrNal(int size, int seed = 7)
    {
        var nal = new byte[size];
        nal[0] = 0x65; // NRI=3, type=5 (IDR)
        new Random(seed).NextBytes(nal.AsSpan(1));
        return nal;
    }

    [Fact]
    public void FuA_200KB大关键帧完整重组_不再受64KB上限截断()
    {
        var nal = MakeIdrNal(200 * 1024);
        using var depack = new H264Depacketizer();

        var frames = new List<MediaFrame>();
        foreach (var p in Fragment(nal, 100, 90000))
            frames.AddRange(depack.Feed(p));

        var frame = Assert.Single(frames);
        Assert.True(frame.Data.AsSpan().SequenceEqual(nal), "重组数据应与原始 NAL 一致");
        Assert.True(frame.IsKeyFrame);
        Assert.True(frame.IsAccessUnitEnd, "RTP marker 应透出为 IsAccessUnitEnd");
    }

    [Fact]
    public void FuA_丢失中间分片时丢弃整个NAL_不上抛损坏数据()
    {
        var nal = MakeIdrNal(200 * 1024);
        using var depack = new H264Depacketizer();

        var frames = new List<MediaFrame>();
        foreach (var p in Fragment(nal, 500, 180000, dropMiddle: true))
            frames.AddRange(depack.Feed(p));

        Assert.Empty(frames);
    }

    [Fact]
    public void FuA_SDP参数集在首个IDR前注入_顺序SPS_PPS_IDR()
    {
        var sps = new byte[] { 0x67, 0x42, 0x00, 0x1E };
        var pps = new byte[] { 0x68, 0xCE, 0x38, 0x80 };
        var nal = MakeIdrNal(50 * 1024);
        using var depack = new H264Depacketizer(sps, pps);

        var frames = new List<MediaFrame>();
        foreach (var p in Fragment(nal, 900, 270000))
            frames.AddRange(depack.Feed(p));

        Assert.Equal(3, frames.Count);
        Assert.True(frames[0].Data.AsSpan().SequenceEqual(sps));
        Assert.True(frames[1].Data.AsSpan().SequenceEqual(pps));
        Assert.True(frames[2].Data.AsSpan().SequenceEqual(nal));
    }
}

/// <summary>
/// AAC (RFC 3640)：多 AU 步长、时间戳推进、marker
/// </summary>
public class AacDepacketizerTests
{
    [Fact]
    public void 单包双AU_尺寸时间戳与marker正确()
    {
        var au1 = new byte[100]; au1[0] = 0xAA;
        var au2 = new byte[200]; au2[0] = 0xBB;

        var payload = new byte[2 + 4 + 300];
        payload[0] = 0x00; payload[1] = 0x20;                                   // AU-headers-length = 32 bits
        payload[2] = (byte)(100 >> 5); payload[3] = (byte)((100 & 0x1F) << 3);  // AU1: size=100, index=0
        payload[4] = (byte)(200 >> 5); payload[5] = (byte)((200 & 0x1F) << 3);  // AU2: size=200, delta=0
        Array.Copy(au1, 0, payload, 6, 100);
        Array.Copy(au2, 0, payload, 106, 200);

        var packet = new RTPPacket(2, false, false, 0, true, 97, 1, 44100, 0x5678, Array.Empty<uint>(),
            payload, 1, StreamType.Audio, payload);

        var frames = new AACDepacketizer(44100, 2).Feed(packet).ToList();

        Assert.Equal(2, frames.Count);
        Assert.Equal(100, frames[0].Data.Length);
        Assert.Equal(200, frames[1].Data.Length);
        Assert.Equal(44100u + 1024u, frames[1].Timestamp);
        Assert.True(frames[1].IsAccessUnitEnd);
        Assert.False(frames[0].IsAccessUnitEnd);
    }
}

/// <summary>
/// RTP 乱序重排缓冲（UDP 用）
/// </summary>
public class RtpReorderBufferTests
{
    private static RTPPacket Mk(ushort seq) => new(2, false, false, 0, false, 96, seq, seq, 1, Array.Empty<uint>(),
        new byte[] { 1 }, 0, StreamType.Video, new byte[] { 1 });

    [Fact]
    public void 乱序包恢复有序输出()
    {
        var buffer = new RtpReorderBuffer(maxWindow: 16, maxWaitMs: 50);
        var output = new List<ushort>();

        foreach (ushort seq in new ushort[] { 10, 11, 13, 12, 14, 16, 15, 17 })
            foreach (var p in buffer.Feed(Mk(seq)))
                output.Add(p.SequenceNumber);

        Assert.Equal(new ushort[] { 10, 11, 12, 13, 14, 15, 16, 17 }, output);
    }

    [Fact]
    public void 序列号回绕正确处理()
    {
        var buffer = new RtpReorderBuffer();
        var output = new List<ushort>();

        foreach (ushort seq in new ushort[] { 65534, 65535, 1, 0, 2 })
            foreach (var p in buffer.Feed(Mk(seq)))
                output.Add(p.SequenceNumber);

        Assert.Equal(new ushort[] { 65534, 65535, 0, 1, 2 }, output);
    }
}
