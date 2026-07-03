using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Cyaim.RTSPClient;
using Cyaim.RTSPClient.Common;
using Cyaim.RTSPClient.Rtp;

// 性能基准：发版前手动运行，守护热路径性能声明
//   dotnet run -c Release --project Cyaim.RTSPClient.Benchmarks
// 关注指标：Mean（回归 >20% 需排查）、Allocated（Parse/FU 路径应接近 0）
BenchmarkRunner.Run(typeof(Program).Assembly);

/// <summary>
/// G.711 编解码：SIMD vs 标量（文档声明 ~14x，此处守护）
/// </summary>
[MemoryDiagnoser]
public class G711Benchmarks
{
    private short[] _pcm = null!;
    private byte[] _encoded = null!;
    private short[] _decoded = null!;

    [GlobalSetup]
    public void Setup()
    {
        _pcm = new short[8000 * 60]; // 60 秒 8kHz
        var rnd = new Random(42);
        for (int i = 0; i < _pcm.Length; i++)
            _pcm[i] = (short)rnd.Next(short.MinValue, short.MaxValue);
        _encoded = new byte[_pcm.Length];
        _decoded = new short[_pcm.Length];
        G711Fast.EncodeALaw(_pcm, _encoded);
    }

    [Benchmark(Baseline = true)]
    public void EncodeALaw_Scalar()
    {
        for (int i = 0; i < _pcm.Length; i++)
            _encoded[i] = G711Fast.LinearToALaw(_pcm[i]);
    }

    [Benchmark]
    public void EncodeALaw_Simd() => G711Fast.EncodeALaw(_pcm, _encoded);

    [Benchmark]
    public void EncodeMuLaw_Simd() => G711Fast.EncodeMuLaw(_pcm, _encoded);

    [Benchmark]
    public void DecodeALaw_Table() => G711Fast.DecodeALaw(_encoded, _decoded);
}

/// <summary>
/// RTP 解析热路径：每包分配应接近 0（零拷贝声明的守护）
/// </summary>
[MemoryDiagnoser]
public class RtpParseBenchmarks
{
    private byte[] _packet = null!;

    [GlobalSetup]
    public void Setup()
    {
        _packet = new byte[1400];
        _packet[0] = 0x80;
        _packet[1] = 96;
        new Random(3).NextBytes(_packet.AsSpan(12));
    }

    [Benchmark]
    public int Parse_ZeroCopy()
    {
        var packet = RTPPacketParser.Parse(_packet, 0, StreamType.Video);
        return packet.PayloadSegment.Count;
    }

    [Benchmark]
    public int Parse_MaterializePayload()
    {
        var packet = RTPPacketParser.Parse(_packet, 0, StreamType.Video);
        return packet.Payload.Length;   // 兼容属性：会分配拷贝，对比展示零拷贝收益
    }
}

/// <summary>
/// H.264 FU-A 重组：池化缓冲路径（每帧分配应只有输出 NAL 本身）
/// </summary>
[MemoryDiagnoser]
public class FuReassemblyBenchmarks
{
    private List<byte[]> _fragments = null!;

    [GlobalSetup]
    public void Setup()
    {
        // 100KB IDR 切成 FU-A 分片（含 RTP 头的完整包）
        var nal = new byte[100 * 1024];
        nal[0] = 0x65;
        new Random(7).NextBytes(nal.AsSpan(1));

        _fragments = new List<byte[]>();
        const int chunk = 1400;
        int offset = 1;
        ushort seq = 0;
        bool first = true;
        while (offset < nal.Length)
        {
            int len = Math.Min(chunk, nal.Length - offset);
            bool last = offset + len >= nal.Length;
            var pkt = new byte[12 + 2 + len];
            pkt[0] = 0x80;
            pkt[1] = (byte)((last ? 0x80 : 0) | 96);
            pkt[2] = (byte)(seq >> 8);
            pkt[3] = (byte)seq;
            pkt[12] = (byte)((nal[0] & 0xE0) | 28);
            pkt[13] = (byte)((nal[0] & 0x1F) | (first ? 0x80 : 0) | (last ? 0x40 : 0));
            Array.Copy(nal, offset, pkt, 14, len);
            _fragments.Add(pkt);
            seq++;
            offset += len;
            first = false;
        }
    }

    [Benchmark]
    public int Reassemble_100KB_Idr()
    {
        using var depack = new H264Depacketizer();
        int frames = 0;
        foreach (var raw in _fragments)
        {
            var packet = RTPPacketParser.Parse(raw, 0, StreamType.Video);
            foreach (var _ in depack.Feed(packet))
                frames++;
        }
        return frames;
    }
}
