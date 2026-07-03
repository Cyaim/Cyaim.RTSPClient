# Codecs / 编解码器

[中文](#中文) | [English](#english)

---

## 中文

### 编解码器架构

Cyaim.RTSPClient 采用插件化架构，支持内置编解码器和 FFmpeg 编解码器。

```
┌─────────────────────────────────────────────────────────────┐
│                      CodecFactory                           │
│  CodecFactory.Instance.RegisterFactory(new MyFactory());    │
└─────────────────────────────────────────────────────────────┘
         │
         ├── IAudioDecoderFactory / IAudioEncoderFactory
         └── IVideoDecoderFactory / IVideoEncoderFactory
                     │
         ┌───────────┴───────────┐
         ▼                       ▼
   ┌───────────┐          ┌───────────┐
   │  内置实现   │          │  FFmpeg   │
   │ G.711/722  │          │ H.264/H265│
   │ G.726/729  │          │ AAC/Opus  │
   └───────────┘          └───────────┘
```

### 内置编解码器

纯 C# 实现，无需外部依赖。

| 编码 | 类型 | 采样率 | 比特率 | 实现 |
|------|------|--------|--------|------|
| G.711A (PCMA) | 音频 | 8kHz | 64kbps | 完整 |
| G.711U (PCMU) | 音频 | 8kHz | 64kbps | 完整 |
| G.722 | 音频 | 16kHz | 48/56/64kbps | 完整 |
| G.726 | 音频 | 8kHz | 16/24/32/40kbps | 完整 |
| G.729 | 音频 | 8kHz | 8kbps | 完整 |
| AAC-LC | 音频 | 44.1kHz | 128kbps | 完整 |

### FFmpeg 编解码器

基于 FFmpeg.AutoGen 库，支持硬件加速。

#### 运行时要求

- 需要 **FFmpeg 7.x** 共享库（FFmpeg.AutoGen 7.1.1 绑定 avcodec-61、avutil-59、swresample-5、swscale-8 等），FFmpeg 不随包分发，需自行部署。
- 库查找顺序：`FFMPEG_PATH` 环境变量 → 应用输出目录及其 `ffmpeg` 子目录 → 常见安装位置（Windows：`C:\ffmpeg\bin` 等；Linux：`/usr/lib/x86_64-linux-gnu` 等；macOS：`/opt/homebrew/lib` 等）。只有目录中真实存在 avcodec 动态库才算命中，否则回退到系统默认加载策略（PATH / 系统库路径）。
- 用 `FFmpegHelper.IsAvailable()` 探测 FFmpeg 是否可用（结果缓存；库缺失或版本不匹配时返回 false 而不抛异常）：

```csharp
using Cyaim.RTSPClient.Codecs.FFmpeg.Video;

if (FFmpegHelper.IsAvailable())
{
    Console.WriteLine($"FFmpeg 版本: {FFmpegHelper.GetVersion()}");
}
```

#### 视频编解码器

| 编码 | 解码器 | 编码器 | 硬件加速 |
|------|--------|--------|----------|
| H.264 | ✅ | ✅ | NVENC, QSV, AMF, VAAPI, VideoToolbox |
| H.265 | ✅ | ✅ | NVENC, QSV, AMF, VAAPI, VideoToolbox |
| VP8 | ✅ | ✅ | VAAPI |
| VP9 | ✅ | ✅ | VAAPI |
| MJPEG | ✅ | - | - |

#### 音频编解码器

| 编码 | 解码器 | 编码器 |
|------|--------|--------|
| AAC | ✅ | ✅ |
| Opus | ✅ | ✅ |
| AMR-NB | ✅ | 视构建* |
| AMR-WB | ✅ | 视构建* |
| Speex | ✅ | 视构建* |
| Vorbis | ✅ | ✅ |
| MP3 | ✅ | ✅ |

> \* AMR/Speex 编码器依赖第三方库（libopencore-amrnb、libvo-amrwbenc、libspeex），多数 FFmpeg 构建不含。工厂的 `CanCreate` 会按当前 FFmpeg 构建实际能力动态探测（`avcodec_find_encoder`/`avcodec_find_decoder`），缺失时返回 false，`CodecFactory` 会自动跳过该工厂。

### 数据格式约定

#### 视频解码输出

- 输出像素格式为 **YUV420P 或 NV12**（硬件解码帧下载到 CPU 后通常为 NV12）；其余格式（P010、YUV422 等）内部经 `sws_scale` 统一转为 YUV420P。
- 实际格式以 `VideoFrame.Format` 为准，消费端应按该字段处理，不要假定固定格式。

#### 音频解码输出 / 编码输入

- 解码输出统一为 **16-bit 交织 PCM**（解码器原生输出如 AAC 的 planar float 会经内部 libswresample 转换）。
- 编码输入为 **16-bit 交织 PCM**，长度任意：内部通过 `AVAudioFifo` 分帧缓冲，按编码器要求的 frame_size 自动切分。

### DecodeAsync / EncodeAsync 的 null 语义

`IVideoDecoder.DecodeAsync`、`IVideoEncoder.EncodeAsync`、`IAudioDecoder.DecodeAsync`、`IAudioEncoder.EncodeAsync` 均返回可空结果（`Task<VideoFrame?>` 等）。

**返回 null 表示编解码器暂未产出（如 B 帧重排导致的输出延迟、音频分帧缓冲不足一个编码帧），这不是错误**；后续输入或 `FlushAsync` 会补齐输出。因此单帧调用后必须做 null 检查：

```csharp
var frame = await decoder.DecodeAsync(encodedFrame);
if (frame != null)
{
    Render(frame);
}
// frame == null：帧还在解码器内部缓冲中，继续送下一个输入即可
```

流式场景请优先使用 `DecodeStreamAsync` / `EncodeStreamAsync`：一个输入可能产出 0..N 帧，流式接口会全部产出，且在输入结束时自动发送 EOF 排空缓冲中的尾帧：

```csharp
await foreach (var frame in decoder.DecodeStreamAsync(encodedFrames, ct))
{
    Render(frame);  // 无需 null 检查
}
```

### 带外参数（ExtraData）

#### 视频：`VideoDecoderConfig.ExtraData`

H.264/H.265 可传 **Annex-B 格式的 SPS/PPS（/VPS）**，来自 SDP 的 `sprop-parameter-sets`。码流内自带参数集时可不设。

```csharp
var decoder = CodecFactory.Instance.CreateVideoDecoder(new VideoDecoderConfig
{
    Codec = VideoCodec.H264,
    ExtraData = annexBSpsPps  // 来自 SDP sprop-parameter-sets（可选）
});
```

#### 音频：`AudioDecoderConfig.ExtraData`

**RTSP 裸 AAC（RFC 3640，无 ADTS 头）必须提供 AudioSpecificConfig**——即 SDP `fmtp` 中 `config=` 十六进制字符串解码后的字节，否则 FFmpeg AAC 解码器无法初始化。

```csharp
// SDP: a=fmtp:96 ... config=1210; ...
byte[] audioSpecificConfig = Convert.FromHexString("1210");

var aacDecoder = CodecFactory.Instance.CreateAudioDecoder(AudioCodec.AAC);
await aacDecoder.InitializeAsync(new AudioDecoderConfig
{
    Codec = AudioCodec.AAC,
    SampleRate = 44100,
    Channels = 2,
    ExtraData = audioSpecificConfig  // RTSP 裸 AAC 必须提供
});
```

#### 编码侧：`FFmpegAudioEncoder.CodecExtraData`

FFmpeg 音频编码器初始化后，可通过 `CodecExtraData` 属性取出编码器全局参数（AAC 为 AudioSpecificConfig），用于生成 SDP 的 `fmtp config=`，或直接作为对应解码器的 `AudioDecoderConfig.ExtraData`：

```csharp
var encoder = (FFmpegAudioEncoder)CodecFactory.Instance.CreateAudioEncoder(AudioCodec.AAC);
await encoder.InitializeAsync(new AudioEncoderConfig
{
    Codec = AudioCodec.AAC,
    SampleRate = 44100,
    Channels = 2,
    Bitrate = 128000
});

byte[]? asc = encoder.CodecExtraData;  // AAC AudioSpecificConfig
string fmtpConfig = Convert.ToHexString(asc!);  // 用于 SDP fmtp config=
```

### 硬件加速

支持的硬件加速类型：

| 硬件 | 平台 | 编码器 | 解码器 |
|------|------|--------|--------|
| NVIDIA NVENC/NVDEC | Windows/Linux | H.264, H.265 | H.264, H.265, VP9 |
| Intel QSV | Windows/Linux | H.264, H.265 | H.264, H.265 |
| AMD AMF | Windows | H.264, H.265 | - |
| VA-API | Linux | H.264, H.265, VP8, VP9 | H.264, H.265 |
| VideoToolbox | macOS/iOS | H.264, H.265 | H.264, H.265 |
| D3D11VA | Windows | - | H.264, H.265, VP9 |
| DXVA2 | Windows | - | H.264, H.265 |

#### 自动选择硬件

```csharp
var supported = FFmpegHardwareHelper.GetSupportedTypes();
var best = FFmpegHardwareHelper.GetBestType();

// 优先级: NVDEC > QSV > D3D11VA > DXVA2 > VAAPI > VideoToolbox > AMF
```

#### 使用硬件解码器

```csharp
// 传入 Config 的重载会自动完成初始化
var decoder = CodecFactory.Instance.CreateVideoDecoder(new VideoDecoderConfig
{
    Codec = VideoCodec.H264,
    EnableHardwareAcceleration = true,
    HardwareDevice = "cuda"  // 或 "qsv", "vaapi", "d3d11va"
});

// 自动回退：硬件不可用时使用软件解码
```

### RTSP 拉流直接解码（推荐：RtspVideoDecoderBridge）

RTSP 解包器输出的 `MediaFrame` 是不带起始码的单个 NAL，而 FFmpeg 解码器要求按完整访问单元
（一帧的全部 NAL 拼 Annex-B）投喂——直接把 `MediaFrame.Data` 喂给解码器会报
`No start code is found`。`RtspVideoDecoderBridge` 按 `IsAccessUnitEnd`（RTP marker）
与时间戳变化自动聚合 NAL 为访问单元，一行代码把拉流接到解码：

```csharp
using Cyaim.RTSPClient.Codecs.FFmpeg.Video;

await using var session = new RTSPSession(config);
await session.StartAsync();

using var bridge = new RtspVideoDecoderBridge(VideoCodec.H264);  // 默认启用硬件加速

var nals = session.GetMediaFrameReader(trackId: 0);
while (await nals.WaitToReadAsync())
{
    while (nals.TryRead(out var nal))
    {
        foreach (var frame in await bridge.FeedAsync(nal))
        {
            // frame: YUV420P 或 NV12（见 frame.Format），时间戳为 RTP 时间戳
            Render(frame);
        }
    }
}

foreach (var frame in await bridge.FlushAsync())   // 流结束排空尾帧
    Render(frame);
```

### 使用示例

#### 注册 FFmpeg 编解码器

```csharp
using Cyaim.RTSPClient.Codecs.FFmpeg.Video;
using Cyaim.RTSPClient.Codecs.FFmpeg.Audio;

// 注册工厂（优先级高于内置实现）
CodecFactory.Instance.RegisterFactory(new FFmpegVideoDecoderFactory());
CodecFactory.Instance.RegisterFactory(new FFmpegVideoEncoderFactory());
CodecFactory.Instance.RegisterFactory(new FFmpegAudioDecoderFactory());
CodecFactory.Instance.RegisterFactory(new FFmpegAudioEncoderFactory());
```

#### 创建解码器

```csharp
// 视频解码器
var h264Decoder = CodecFactory.Instance.CreateVideoDecoder(VideoCodec.H264);
var h265Decoder = CodecFactory.Instance.CreateVideoDecoder(VideoCodec.H265);

// 音频解码器
var aacDecoder = CodecFactory.Instance.CreateAudioDecoder(AudioCodec.AAC);
var opusDecoder = CodecFactory.Instance.CreateAudioDecoder(AudioCodec.OPUS);
```

#### 解码

```csharp
// 单帧解码：结果可空，null 表示暂未产出（不是错误）
VideoFrame? frame = await h264Decoder.DecodeAsync(encodedFrame);
if (frame != null)
{
    Console.WriteLine($"{frame.Width}x{frame.Height} {frame.Format}");  // YUV420P 或 NV12
}

// 流式解码（推荐）：自动 EOF 排空尾帧
await foreach (var f in h264Decoder.DecodeStreamAsync(encodedFrames))
{
    Render(f);
}
```

#### 编码

```csharp
// H.264 编码
var config = new VideoEncoderConfig
{
    Codec = VideoCodec.H264,
    Width = 1920,
    Height = 1080,
    Bitrate = 4000000,
    Framerate = 30,
    EnableHardwareAcceleration = true,
    Preset = "fast"  // NVENC: fast, medium, slow, hq
};

var encoder = CodecFactory.Instance.CreateVideoEncoder(config.Codec);
await encoder.InitializeAsync(config);

// 单帧编码：结果可空，null 表示编码器内部缓冲中（不是错误）
EncodedVideoFrame? encoded = await encoder.EncodeAsync(frame);
if (encoded != null)
{
    Send(encoded);
}

// 流式编码（推荐）：自动 EOF 排空缓冲
await foreach (var packet in encoder.EncodeStreamAsync(frames))
{
    Send(packet);
}
```

### 创建自定义编解码器

```csharp
public class MyAudioDecoder : IAudioDecoder
{
    public string Name => "My Decoder";
    public bool IsHardwareAccelerated => false;
    public ProcessorState State { get; private set; }
    public AudioCodec[] SupportedCodecs => new[] { AudioCodec.OPUS };

    public Task InitializeAsync(AudioDecoderConfig config, CancellationToken ct = default)
    {
        State = ProcessorState.Ready;
        return Task.CompletedTask;
    }

    // 返回值可空：暂未产出帧时返回 null（不是错误）
    public Task<AudioFrame?> DecodeAsync(EncodedAudioFrame input, CancellationToken ct = default)
    {
        byte[] pcm = DecodeOpus(input.Data.Span);
        return Task.FromResult<AudioFrame?>(new AudioFrame { Data = pcm, /* ... */ });
    }

    // ... 其他方法（DecodeStreamAsync / FlushAsync / Dispose）
}

// 注册
CodecFactory.Instance.RegisterFactory(new MyAudioDecoderFactory());
```

---

## English

### Codec Architecture

Cyaim.RTSPClient uses a plugin architecture supporting built-in and FFmpeg codecs.

### Built-in Codecs

Pure C# implementation, no external dependencies.

| Codec | Type | Sample Rate | Bitrate | Status |
|-------|------|-------------|---------|--------|
| G.711A (PCMA) | Audio | 8kHz | 64kbps | Complete |
| G.711U (PCMU) | Audio | 8kHz | 64kbps | Complete |
| G.722 | Audio | 16kHz | 48/56/64kbps | Complete |
| G.726 | Audio | 8kHz | 16/24/32/40kbps | Complete |
| G.729 | Audio | 8kHz | 8kbps | Complete |
| AAC-LC | Audio | 44.1kHz | 128kbps | Complete |

### FFmpeg Codecs

Based on FFmpeg.AutoGen library with hardware acceleration support.

#### Runtime Requirements

- Requires **FFmpeg 7.x** shared libraries (FFmpeg.AutoGen 7.1.1 binds avcodec-61, avutil-59, swresample-5, swscale-8, etc.). FFmpeg is not bundled and must be deployed separately.
- Library probe order: `FFMPEG_PATH` environment variable → application output directory and its `ffmpeg` subdirectory → common install locations (Windows: `C:\ffmpeg\bin`, etc.; Linux: `/usr/lib/x86_64-linux-gnu`, etc.; macOS: `/opt/homebrew/lib`, etc.). A directory only matches if it actually contains the avcodec library; otherwise the default loader strategy (PATH / system library paths) is used.
- Use `FFmpegHelper.IsAvailable()` to probe availability (result is cached; returns false instead of throwing when libraries are missing or mismatched).

#### Video Codecs

| Codec | Decoder | Encoder | Hardware |
|-------|---------|---------|----------|
| H.264 | ✅ | ✅ | NVENC, QSV, AMF, VAAPI, VideoToolbox |
| H.265 | ✅ | ✅ | NVENC, QSV, AMF, VAAPI, VideoToolbox |
| VP8 | ✅ | ✅ | VAAPI |
| VP9 | ✅ | ✅ | VAAPI |
| MJPEG | ✅ | - | - |

#### Audio Codecs

| Codec | Decoder | Encoder |
|-------|---------|---------|
| AAC | ✅ | ✅ |
| Opus | ✅ | ✅ |
| AMR-NB | ✅ | Build-dependent* |
| AMR-WB | ✅ | Build-dependent* |
| Speex | ✅ | Build-dependent* |
| Vorbis | ✅ | ✅ |
| MP3 | ✅ | ✅ |

> \* AMR/Speex encoders require third-party libraries and are missing from most FFmpeg builds. Factory `CanCreate` dynamically probes the actual FFmpeg build (`avcodec_find_encoder`/`avcodec_find_decoder`) and returns false when unavailable, so `CodecFactory` skips the factory automatically.

### Data Format Conventions

- **Video decode output**: YUV420P or NV12 (hardware frames downloaded to CPU are typically NV12); other formats are converted to YUV420P internally via `sws_scale`. Always check `VideoFrame.Format` for the actual format.
- **Audio decode output**: always **16-bit interleaved PCM** (planar formats such as AAC's FLTP are converted internally via libswresample).
- **Audio encode input**: **16-bit interleaved PCM** of any length; an internal `AVAudioFifo` buffers and re-chunks it to the encoder's required frame_size.

### Null Semantics of DecodeAsync / EncodeAsync

`IVideoDecoder.DecodeAsync`, `IVideoEncoder.EncodeAsync`, `IAudioDecoder.DecodeAsync`, and `IAudioEncoder.EncodeAsync` return nullable results (`Task<VideoFrame?>`, etc.).

**A null return means the codec has not produced output yet (e.g. B-frame reordering delay, or the audio FIFO not yet holding a full encoder frame) — it is not an error.** Subsequent input or `FlushAsync` will produce the pending output. Always null-check single-shot calls:

```csharp
var frame = await decoder.DecodeAsync(encodedFrame);
if (frame != null)
{
    Render(frame);
}
```

For streaming, prefer `DecodeStreamAsync` / `EncodeStreamAsync`: one input may yield 0..N outputs, all of which are emitted, and an EOF is sent automatically at end of input to drain trailing frames — no null checks needed.

### Out-of-band Parameters (ExtraData)

- `VideoDecoderConfig.ExtraData`: Annex-B SPS/PPS(/VPS) for H.264/H.265, from SDP `sprop-parameter-sets`. Optional when parameter sets are in-band.
- `AudioDecoderConfig.ExtraData`: **required for raw RTSP AAC (RFC 3640, no ADTS header)** — pass the AudioSpecificConfig, i.e. the hex-decoded bytes of the SDP `fmtp` `config=` value; otherwise the FFmpeg AAC decoder cannot initialize.
- `FFmpegAudioEncoder.CodecExtraData`: after initialization, returns the encoder's global header (AudioSpecificConfig for AAC) for SDP `fmtp config=` generation or as the matching decoder's `ExtraData`.

### Hardware Acceleration

| Hardware | Platform | Encoder | Decoder |
|----------|----------|---------|---------|
| NVIDIA NVENC/NVDEC | Windows/Linux | H.264, H.265 | H.264, H.265, VP9 |
| Intel QSV | Windows/Linux | H.264, H.265 | H.264, H.265 |
| AMD AMF | Windows | H.264, H.265 | - |
| VA-API | Linux | H.264, H.265, VP8, VP9 | H.264, H.265 |
| VideoToolbox | macOS/iOS | H.264, H.265 | H.264, H.265 |
| D3D11VA | Windows | - | H.264, H.265, VP9 |
| DXVA2 | Windows | - | H.264, H.265 |

#### Auto-select Hardware

```csharp
var supported = FFmpegHardwareHelper.GetSupportedTypes();
var best = FFmpegHardwareHelper.GetBestType();

// Priority: NVDEC > QSV > D3D11VA > DXVA2 > VAAPI > VideoToolbox > AMF
```

#### Use Hardware Decoder

```csharp
// The config overload initializes the decoder automatically
var decoder = CodecFactory.Instance.CreateVideoDecoder(new VideoDecoderConfig
{
    Codec = VideoCodec.H264,
    EnableHardwareAcceleration = true,
    HardwareDevice = "cuda"  // or "qsv", "vaapi", "d3d11va"
});

// Auto-fallback: uses software if hardware unavailable
```

### Custom Codec Implementation

```csharp
public class MyAudioDecoder : IAudioDecoder
{
    public string Name => "My Decoder";
    public bool IsHardwareAccelerated => false;
    public ProcessorState State { get; private set; }
    public AudioCodec[] SupportedCodecs => new[] { AudioCodec.OPUS };

    // Nullable result: return null when no frame is available yet (not an error)
    public Task<AudioFrame?> DecodeAsync(EncodedAudioFrame input, CancellationToken ct = default)
    {
        // ...
    }

    // Implement remaining interface methods...
}

// Register
CodecFactory.Instance.RegisterFactory(new MyAudioDecoderFactory());
```
