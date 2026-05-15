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
| AMR-NB | ✅ | ✅ |
| AMR-WB | ✅ | ✅ |
| Speex | ✅ | ✅ |
| Vorbis | ✅ | ✅ |
| MP3 | ✅ | ✅ |

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
var decoder = CodecFactory.Instance.CreateVideoDecoder(new VideoDecoderConfig
{
    Codec = VideoCodec.H264,
    EnableHardwareAcceleration = true,
    HardwareDevice = "cuda"  // 或 "qsv", "vaapi", "d3d11va"
});

// 自动回退：硬件不可用时使用软件解码
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

#### 编码

```csharp
// H.264 编码
var encoder = CodecFactory.Instance.CreateVideoEncoder(new VideoEncoderConfig
{
    Codec = VideoCodec.H264,
    Width = 1920,
    Height = 1080,
    Bitrate = 4000000,
    Framerate = 30,
    EnableHardwareAcceleration = true,
    Preset = "fast"  // NVENC: fast, medium, slow, hq
});

await encoder.InitializeAsync(config);
EncodedVideoFrame encoded = await encoder.EncodeAsync(frame);
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

    public Task<AudioFrame> DecodeAsync(EncodedAudioFrame input, CancellationToken ct = default)
    {
        byte[] pcm = DecodeOpus(input.Data.Span);
        return Task.FromResult(new AudioFrame { Data = pcm, /* ... */ });
    }

    // ... 其他方法
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

#### Video Codecs

| Codec | Decoder | Encoder | Hardware |
|-------|---------|---------|----------|
| H.264 | ✅ | ✅ | NVENC, QSV, AMF, VAAPI |
| H.265 | ✅ | ✅ | NVENC, QSV, AMF, VAAPI |
| VP8 | ✅ | ✅ | VAAPI |
| VP9 | ✅ | ✅ | VAAPI |
| MJPEG | ✅ | - | - |

#### Audio Codecs

| Codec | Decoder | Encoder |
|-------|---------|---------|
| AAC | ✅ | ✅ |
| Opus | ✅ | ✅ |
| AMR | ✅ | ✅ |
| Speex | ✅ | ✅ |
| Vorbis | ✅ | ✅ |
| MP3 | ✅ | ✅ |

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

    // Implement interface methods...
}

// Register
CodecFactory.Instance.RegisterFactory(new MyAudioDecoderFactory());
```
