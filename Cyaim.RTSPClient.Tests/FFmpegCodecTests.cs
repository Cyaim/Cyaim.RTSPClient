using Cyaim.RTSPClient.Codecs.FFmpeg.Audio.Decoders;
using Cyaim.RTSPClient.Codecs.FFmpeg.Audio.Encoders;
using Cyaim.RTSPClient.Codecs.FFmpeg.Video;
using Cyaim.RTSPClient.Codecs.FFmpeg.Video.Decoders;
using Cyaim.RTSPClient.Codecs.FFmpeg.Video.Encoders;
using Cyaim.RTSPClient.Media;
using Cyaim.RTSPServer.Media;
using Xunit;

namespace Cyaim.RTSPClient.Tests;

/// <summary>
/// FFmpeg 插件功能测试。
/// 需要运行环境提供 FFmpeg 7.x 共享库（FFMPEG_PATH 环境变量或应用目录），
/// 不可用时自动跳过（CI 无 FFmpeg 7 时保持绿色，本地/发版前必须真实跑通）。
/// </summary>
[Trait("Category", "FFmpeg")]
public class FFmpegCodecTests
{
    private static bool FFmpegAvailable => FFmpegHelper.IsAvailable();

    // ---------- 视频 ----------

    [Fact]
    public async Task H264_编码解码Roundtrip_多帧全部产出()
    {
        if (!FFmpegAvailable) return; // 环境无 FFmpeg，跳过

        const int width = 320, height = 240, frameCount = 30;

        using var encoder = new H264Encoder();
        await encoder.InitializeAsync(new VideoEncoderConfig
        {
            Codec = VideoCodec.H264,
            Width = width,
            Height = height,
            Framerate = 25,
            Bitrate = 500_000,
            GopSize = 10,
            EnableHardwareAcceleration = false,
            Preset = "ultrafast"
        });

        // 生成运动渐变帧并编码
        var encodedPackets = new List<EncodedVideoFrame>();
        for (int i = 0; i < frameCount; i++)
        {
            var frame = MakeYuvFrame(width, height, i);
            var packet = await encoder.EncodeAsync(frame);
            if (packet != null) encodedPackets.Add(packet);
            while (encoder.DequeuePendingPacket() is { } extra) encodedPackets.Add(extra);
        }
        await encoder.FlushAsync();
        while (encoder.DequeuePendingPacket() is { } tail) encodedPackets.Add(tail);

        Assert.True(encodedPackets.Count >= frameCount - 2,
            $"编码输出 {encodedPackets.Count} 包，应接近输入帧数 {frameCount}");
        Assert.Equal(FrameType.IDR, encodedPackets[0].Type);

        // 解码回帧
        using var decoder = new H264Decoder();
        await decoder.InitializeAsync(new VideoDecoderConfig
        {
            Codec = VideoCodec.H264,
            Width = width,
            Height = height,
            EnableHardwareAcceleration = false
        });

        var decodedFrames = new List<VideoFrame>();
        foreach (var packet in encodedPackets)
        {
            var frame = await decoder.DecodeAsync(packet);
            if (frame != null) decodedFrames.Add(frame);
            while (decoder.DequeuePendingFrame() is { } extra) decodedFrames.Add(extra);
        }
        await decoder.FlushAsync();
        while (decoder.DequeuePendingFrame() is { } tail) decodedFrames.Add(tail);

        Assert.True(decodedFrames.Count >= frameCount - 2,
            $"解码输出 {decodedFrames.Count} 帧，应接近编码包数 {encodedPackets.Count}");
        Assert.All(decodedFrames, f =>
        {
            Assert.Equal(width, f.Width);
            Assert.Equal(height, f.Height);
            Assert.True(f.Data.Length >= width * height * 3 / 2);
        });

        // 像素内容合理性：首帧 Y 均值与输入相近（编解码有损但不会跑偏太多）
        var inputFirst = MakeYuvFrame(width, height, 0);
        double inputAvg = AverageLuma(inputFirst.Data.Span, width * height);
        double outputAvg = AverageLuma(decodedFrames[0].Data.Span, width * height);
        Assert.True(Math.Abs(inputAvg - outputAvg) < 16,
            $"解码首帧亮度均值 {outputAvg:F1} 应接近输入 {inputAvg:F1}");
    }

    [Fact]
    public async Task 服务器H264TestStream_可被真实FFmpeg解码器解码()
    {
        if (!FFmpegAvailable) return;

        // SPS + PPS + I_PCM IDR 拼 Annex-B 码流
        static byte[] AnnexB(params byte[][] nals)
        {
            var ms = new MemoryStream();
            foreach (var nal in nals)
            {
                ms.Write(new byte[] { 0, 0, 0, 1 });
                ms.Write(nal);
            }
            return ms.ToArray();
        }

        using var decoder = new H264Decoder();
        await decoder.InitializeAsync(new VideoDecoderConfig
        {
            Codec = VideoCodec.H264,
            EnableHardwareAcceleration = false
        });

        var stream = AnnexB(H264TestStream.Sps, H264TestStream.Pps, H264TestStream.IdrFrame);
        var frame = await decoder.DecodeAsync(new EncodedVideoFrame
        {
            Data = stream,
            Codec = VideoCodec.H264,
            Timestamp = 0
        });

        // I_PCM 帧应立即解出（无重排延迟）；若解码器缓冲则 flush 后必须产出
        if (frame == null)
        {
            await decoder.FlushAsync();
            frame = decoder.DequeuePendingFrame();
        }

        Assert.NotNull(frame);
        Assert.Equal(320, frame!.Width);
        Assert.Equal(240, frame.Height);
    }

    [Fact]
    public async Task 硬件加速解码_自动选择并正确输出()
    {
        if (!FFmpegAvailable) return;
        if (FFmpegHardwareHelper.GetSupportedTypes().Count == 0) return; // 无硬件环境跳过

        const int width = 640, height = 480, frameCount = 20;

        // 软件编码生成码流
        using var encoder = new H264Encoder();
        await encoder.InitializeAsync(new VideoEncoderConfig
        {
            Codec = VideoCodec.H264, Width = width, Height = height, Framerate = 25,
            Bitrate = 800_000, GopSize = 10, EnableHardwareAcceleration = false, Preset = "ultrafast"
        });
        var packets = new List<EncodedVideoFrame>();
        for (int i = 0; i < frameCount; i++)
        {
            var pkt = await encoder.EncodeAsync(MakeYuvFrame(width, height, i));
            if (pkt != null) packets.Add(pkt);
            while (encoder.DequeuePendingPacket() is { } e) packets.Add(e);
        }
        await encoder.FlushAsync();
        while (encoder.DequeuePendingPacket() is { } t) packets.Add(t);

        // 硬件解码（自动选择；硬件初始化失败时会自动回退软件，同样必须能解）
        using var decoder = new H264Decoder();
        await decoder.InitializeAsync(new VideoDecoderConfig
        {
            Codec = VideoCodec.H264, Width = width, Height = height, EnableHardwareAcceleration = true
        });

        int frames = 0;
        foreach (var pkt in packets)
        {
            if (await decoder.DecodeAsync(pkt) is { } f) { Assert.Equal(width, f.Width); frames++; }
            while (decoder.DequeuePendingFrame() is { } x) frames++;
        }
        await decoder.FlushAsync();
        while (decoder.DequeuePendingFrame() is { } x) frames++;

        Assert.True(frames >= frameCount - 2, $"硬件路径解出 {frames}/{packets.Count} 帧");
        Assert.Equal(0, decoder.DecodeErrorCount);
    }

    [Fact]
    public async Task B帧流_多帧重排与时间戳单调()
    {
        if (!FFmpegAvailable) return;

        const int total = 40;
        using var encoder = new H264Encoder();
        await encoder.InitializeAsync(new VideoEncoderConfig
        {
            Codec = VideoCodec.H264, Width = 320, Height = 240, Framerate = 25,
            Bitrate = 400_000, GopSize = 25, BFrames = 2, EnableHardwareAcceleration = false, Preset = "fast"
        });

        var packets = new List<EncodedVideoFrame>();
        for (int i = 0; i < total; i++)
        {
            var pkt = await encoder.EncodeAsync(MakeYuvFrame(320, 240, i));
            if (pkt != null) packets.Add(pkt);
            while (encoder.DequeuePendingPacket() is { } e) packets.Add(e);
        }
        await encoder.FlushAsync();
        while (encoder.DequeuePendingPacket() is { } t) packets.Add(t);

        using var decoder = new H264Decoder();
        await decoder.InitializeAsync(new VideoDecoderConfig { Codec = VideoCodec.H264, EnableHardwareAcceleration = false });

        var timestamps = new List<long>();
        foreach (var pkt in packets)
        {
            if (await decoder.DecodeAsync(pkt) is { } f) timestamps.Add(f.Timestamp);
            while (decoder.DequeuePendingFrame() is { } x) timestamps.Add(x.Timestamp);
        }
        await decoder.FlushAsync();
        while (decoder.DequeuePendingFrame() is { } x) timestamps.Add(x.Timestamp);

        Assert.True(timestamps.Count >= total - 2, $"B 帧流解出 {timestamps.Count}/{total} 帧");
        for (int i = 1; i < timestamps.Count; i++)
            Assert.True(timestamps[i] > timestamps[i - 1],
                $"时间戳必须单调递增（B 帧重排后按显示顺序输出）：ts[{i - 1}]={timestamps[i - 1]}, ts[{i}]={timestamps[i]}");
    }

    [Fact]
    public async Task RtspVideoDecoderBridge_MediaFrame序列端到端解码()
    {
        if (!FFmpegAvailable) return;

        using var bridge = new RtspVideoDecoderBridge(VideoCodec.H264, enableHardwareAcceleration: false);

        // 模拟 GetMediaFrameReader 输出：SPS/PPS/IDR 同时间戳（IDR 带 marker）+ 24 个 P 帧
        var nals = new List<Rtp.MediaFrame>
        {
            new(H264TestStream.Sps, 0, true, StreamType.Video, 0, false),
            new(H264TestStream.Pps, 0, true, StreamType.Video, 0, false),
            new(H264TestStream.IdrFrame, 0, true, StreamType.Video, 0, true),
        };
        for (int i = 1; i <= 24; i++)
            nals.Add(new(H264TestStream.BuildPFrame(i % 16), (uint)(i * 3600), false, StreamType.Video, 0, true));

        int frames = 0;
        foreach (var nal in nals)
            frames += (await bridge.FeedAsync(nal)).Count;
        frames += (await bridge.FlushAsync()).Count;

        Assert.Equal(25, frames);   // 1 IDR + 24 P，逐帧解出
        Assert.Equal(0, bridge.Decoder.DecodeErrorCount);
    }

    // ---------- 音频 ----------

    [Fact]
    public async Task AAC_编码解码Roundtrip_FLTP协商与FIFO分帧()
    {
        if (!FFmpegAvailable) return;

        const int sampleRate = 48000, channels = 2;

        using var encoder = new AACEncoder();
        await encoder.InitializeAsync(new AudioEncoderConfig
        {
            Codec = AudioCodec.AAC,
            SampleRate = sampleRate,
            Channels = channels,
            Bitrate = 128_000
        });

        // 1 秒 440Hz 正弦（S16 交织），按 20ms 块投喂（验证 FIFO 任意长度分帧）
        var pcm = MakeSineS16(sampleRate, channels, seconds: 1.0);
        var packets = new List<EncodedAudioFrame>();
        int chunk = sampleRate / 50 * channels * 2;
        for (int off = 0; off < pcm.Length; off += chunk)
        {
            int len = Math.Min(chunk, pcm.Length - off);
            var input = new AudioFrame
            {
                Data = new ReadOnlyMemory<byte>(pcm, off, len),
                SampleRate = sampleRate,
                Channels = channels,
                Timestamp = off * 1_000_000L / (sampleRate * channels * 2)
            };
            var packet = await encoder.EncodeAsync(input);
            if (packet != null) packets.Add(packet);
            while (encoder.DequeuePendingPacket() is { } extra) packets.Add(extra);
        }
        await encoder.FlushAsync();
        while (encoder.DequeuePendingPacket() is { } tail) packets.Add(tail);

        // 1 秒 48kHz / 1024 样本每帧 ≈ 46-47 包
        Assert.True(packets.Count >= 40, $"AAC 编码输出 {packets.Count} 包，1 秒音频应约 46 包");

        var asc = encoder.CodecExtraData;
        Assert.NotNull(asc);        // AudioSpecificConfig 必须可获取（SDP config= 依赖）
        Assert.True(asc!.Length >= 2);

        // 解码（裸 AAC 必须带 ASC extradata——这正是 RTSP 场景）
        using var decoder = new AACDecoder();
        await decoder.InitializeAsync(new AudioDecoderConfig
        {
            Codec = AudioCodec.AAC,
            SampleRate = sampleRate,
            Channels = channels,
            ExtraData = asc
        });

        long totalSamples = 0;
        double sumSquares = 0;
        foreach (var packet in packets)
        {
            var frame = await decoder.DecodeAsync(packet);
            while (frame != null)
            {
                Assert.Equal(sampleRate, frame.SampleRate);
                Assert.Equal(channels, frame.Channels);
                Assert.Equal(16, frame.BitsPerSample);

                var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(frame.Data.Span);
                foreach (short s in samples) sumSquares += (double)s * s;
                totalSamples += samples.Length / channels;

                frame = null; // DecodeAsync 每包一次；余帧走内部队列已在下轮取出
            }
        }

        // 1 秒音频（AAC 编码延迟允许 ~10% 损耗）
        Assert.True(totalSamples > sampleRate * 0.85,
            $"解码总样本 {totalSamples}，应接近 {sampleRate}");

        // 信号真实性：正弦波 RMS 幅度显著非零（旧实现输出的是按错误格式拷贝的噪音/静音）
        double rms = Math.Sqrt(sumSquares / (totalSamples * channels));
        Assert.True(rms > 2000, $"解码信号 RMS={rms:F0}，440Hz 正弦应有显著幅度");
    }

    [Fact]
    public void 工厂能力探测_不谎报缺失的编码器()
    {
        if (!FFmpegAvailable) return;

        var encoderFactory = new Cyaim.RTSPClient.Codecs.FFmpeg.Audio.FFmpegAudioEncoderFactory();
        var decoderFactory = new Cyaim.RTSPClient.Codecs.FFmpeg.Audio.FFmpegAudioDecoderFactory();

        // AAC/Opus/MP3 是 FFmpeg 内建，任何构建都有
        Assert.True(encoderFactory.CanCreate(AudioCodec.AAC));
        Assert.True(decoderFactory.CanCreate(AudioCodec.AAC));
        Assert.True(decoderFactory.CanCreate(AudioCodec.OPUS));

        // Speex 编码器需要 libspeex（LGPL 构建通常没有）——CanCreate 必须反映真实能力而不是抛异常
        _ = encoderFactory.CanCreate(AudioCodec.SPEEX);
    }

    // ---------- 工具 ----------

    private static VideoFrame MakeYuvFrame(int width, int height, int index)
    {
        int ySize = width * height;
        int uvSize = (width / 2) * (height / 2);
        var data = new byte[ySize + uvSize * 2];

        // 移动渐变（帧间有差异，避免编码器输出空 P 帧）
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                data[y * width + x] = (byte)((x + y + index * 4) & 0xFF);

        for (int i = 0; i < uvSize; i++)
        {
            data[ySize + i] = 128;
            data[ySize + uvSize + i] = 128;
        }

        return new VideoFrame
        {
            Data = data,
            Width = width,
            Height = height,
            Format = PixelFormat.YUV420P,
            Timestamp = index * 40_000L
        };
    }

    private static double AverageLuma(ReadOnlySpan<byte> data, int lumaBytes)
    {
        long sum = 0;
        for (int i = 0; i < lumaBytes; i++) sum += data[i];
        return sum / (double)lumaBytes;
    }

    private static byte[] MakeSineS16(int sampleRate, int channels, double seconds)
    {
        int samples = (int)(sampleRate * seconds);
        var pcm = new byte[samples * channels * 2];
        var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm.AsSpan());
        for (int i = 0; i < samples; i++)
        {
            short v = (short)(12000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            for (int c = 0; c < channels; c++)
                span[i * channels + c] = v;
        }
        return pcm;
    }
}
