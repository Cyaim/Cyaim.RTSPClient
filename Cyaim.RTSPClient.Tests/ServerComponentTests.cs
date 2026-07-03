using Cyaim.RTSPServer.Media;
using Xunit;

namespace Cyaim.RTSPClient.Tests;

/// <summary>
/// 服务端 RTP 广播器：每订阅者独立队列（无声音问题的根因回归）
/// </summary>
public class RtpPacketBroadcasterTests
{
    [Fact]
    public async Task 多订阅者各自收到全部包_不互相抢包()
    {
        var broadcaster = new RtpPacketBroadcaster();
        const int packetCount = 200;

        async Task<int> SubscribeAndCount()
        {
            int count = 0;
            await foreach (var _ in broadcaster.SubscribeAsync(CancellationToken.None))
                count++;
            return count;
        }

        var sub1 = SubscribeAndCount();
        var sub2 = SubscribeAndCount();

        // 等订阅建立
        while (broadcaster.SubscriberCount < 2)
            await Task.Delay(10);

        for (int i = 0; i < packetCount; i++)
        {
            broadcaster.Publish(new RtpPacket
            {
                Data = new byte[] { 1, 2, 3 },
                TrackId = i % 2,
                SequenceNumber = (ushort)i
            });
        }
        broadcaster.Complete();

        // 旧实现共享单 Channel：每个包只被随机一个读者取走，音频包大量丢失
        Assert.Equal(packetCount, await sub1);
        Assert.Equal(packetCount, await sub2);
    }
}

/// <summary>
/// 服务端合规 H.264 测试流 + FU-A 分片与客户端解包器的交叉验证
/// </summary>
public class H264TestStreamTests
{
    [Fact]
    public void SPS_PPS_为合法NAL头()
    {
        Assert.Equal(0x67, H264TestStream.Sps[0]); // nal_ref_idc=3, type=7
        Assert.Equal(0x68, H264TestStream.Pps[0]); // nal_ref_idc=3, type=8
    }

    [Fact]
    public void IDR为IPCM大帧_远超旧64KB上限()
    {
        Assert.Equal(0x65, H264TestStream.IdrFrame[0]); // type=5
        Assert.True(H264TestStream.IdrFrame.Length > 100 * 1024,
            $"I_PCM IDR 应 >100KB，实际 {H264TestStream.IdrFrame.Length}");
    }

    [Fact]
    public void Packetize分片经客户端解包器重组_与原始NAL一致()
    {
        // 服务端 FU-A 打包 → 客户端 FU-A 解包，端到端交叉验证
        byte[] idr = H264TestStream.IdrFrame;
        var packets = H264TestStream.Packetize(idr, startSeq: 1000, timestamp: 90000, isMarker: true);
        Assert.True(packets.Count > 40, "200KB 级 IDR 应产生大量分片");

        using var depack = new Cyaim.RTSPClient.Rtp.H264Depacketizer();
        var frames = new List<Cyaim.RTSPClient.Rtp.MediaFrame>();
        foreach (var (data, _) in packets)
        {
            var packet = Cyaim.RTSPClient.Rtp.RTPPacketParser.Parse(
                data, 0, Cyaim.RTSPClient.StreamType.Video);
            frames.AddRange(depack.Feed(packet));
        }

        var frame = Assert.Single(frames);
        Assert.True(frame.Data.AsSpan().SequenceEqual(idr), "客户端重组结果应与服务端原始 NAL 一致");
        Assert.True(frame.IsKeyFrame);
    }

    [Fact]
    public void P帧为小体积全跳过帧()
    {
        var pFrame = H264TestStream.BuildPFrame(3);
        Assert.Equal(1, pFrame[0] & 0x1F); // type=1 non-IDR
        Assert.True(pFrame.Length < 32, "全跳过 P 帧应只有几字节");
    }
}

/// <summary>
/// 服务端 G.711 音频与客户端实现互相验证
/// </summary>
public class ServerG711Tests
{
    [Fact]
    public void 服务端与客户端Alaw编码一致()
    {
        for (int i = -32768; i <= 32767; i += 13)
        {
            short pcm = (short)i;
            byte server = G711Audio.LinearToALaw(pcm);
            byte client = Cyaim.RTSPClient.Common.G711Fast.LinearToALaw(pcm);
            if (server != client)
                Assert.Fail($"A-law 服务端/客户端编码不一致 at {pcm}: server=0x{server:X2}, client=0x{client:X2}");
        }
    }

    [Fact]
    public void 测试音RTP包结构正确()
    {
        double phase = 0;
        var frame = G711Audio.GenerateToneFrame(ref phase);
        Assert.Equal(G711Audio.SamplesPerFrame, frame.Length);

        var packet = G711Audio.CreateRtpPacket(frame, 42, 8000);
        Assert.Equal(0x80, packet[0]);
        Assert.Equal(8, packet[1] & 0x7F);       // PT=8 PCMA
        Assert.Equal(12 + 160, packet.Length);
        Assert.Equal(42, (packet[2] << 8) | packet[3]);
    }
}
