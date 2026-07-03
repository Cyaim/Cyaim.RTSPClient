# Hardware Acceleration / 硬件加速

[中文](#中文) | [English](#english)

---

## 中文

### 概述

Cyaim.RTSPClient 通过 FFmpeg 支持多种硬件加速方案，可显著降低 CPU 使用率并提升编解码性能。

### 运行时要求

- 需要 **FFmpeg 7.x** 共享库（FFmpeg.AutoGen 7.1.1 绑定 avcodec-61 等），需自行部署。
- 库查找顺序：`FFMPEG_PATH` 环境变量 → 应用输出目录及其 `ffmpeg` 子目录 → 常见安装位置。
- 可用 `FFmpegHelper.IsAvailable()` 探测 FFmpeg 是否可用（结果缓存，不抛异常）。

### 支持的硬件

| 硬件 | 厂商 | 平台 | 编码 | 解码 |
|------|------|------|------|------|
| **NVENC/NVDEC** | NVIDIA | Windows/Linux | H.264, H.265 | H.264, H.265, VP9 |
| **QSV** | Intel | Windows/Linux | H.264, H.265 | H.264, H.265 |
| **AMF** | AMD | Windows | H.264, H.265 | - |
| **VA-API** | - | Linux | H.264, H.265, VP8, VP9 | H.264, H.265 |
| **VideoToolbox** | Apple | macOS/iOS | H.264, H.265 | H.264, H.265 |
| **D3D11VA** | Microsoft | Windows | - | H.264, H.265, VP9 |
| **DXVA2** | Microsoft | Windows | - | H.264, H.265 |
| **Vulkan** | Khronos | 全平台 | - | - |

### 检测硬件支持

```csharp
using Cyaim.RTSPClient.Codecs.FFmpeg.Video;

// 获取所有支持的硬件
var supported = FFmpegHardwareHelper.GetSupportedTypes();
foreach (var type in supported)
{
    Console.WriteLine($"支持: {type}");
}

// 获取最佳硬件（按优先级）
var best = FFmpegHardwareHelper.GetBestType();
Console.WriteLine($"最佳: {best}");  // Cuda, Qsv, D3d11va, etc.
```

### 使用硬件解码器

#### 自动选择硬件

```csharp
// 传入 Config 的重载会自动完成初始化，无需再调用 InitializeAsync
var decoder = CodecFactory.Instance.CreateVideoDecoder(new VideoDecoderConfig
{
    Codec = VideoCodec.H264,
    Width = 1920,
    Height = 1080,
    EnableHardwareAcceleration = true,  // 自动选择最佳硬件
    ExtraData = annexBSpsPps  // 可选：SDP sprop-parameter-sets 带外 SPS/PPS（Annex-B）
});

Console.WriteLine(decoder.Name);  // "FFmpeg H.264 Decoder (Cuda)"
Console.WriteLine(decoder.IsHardwareAccelerated);  // true
```

#### 指定硬件设备

```csharp
var decoder = CodecFactory.Instance.CreateVideoDecoder(new VideoDecoderConfig
{
    Codec = VideoCodec.H264,
    EnableHardwareAcceleration = true,
    HardwareDevice = "cuda"  // 指定 NVIDIA GPU
});

// 或使用其他设备
var qsvDecoder = CodecFactory.Instance.CreateVideoDecoder(new VideoDecoderConfig
{
    Codec = VideoCodec.H264,
    HardwareDevice = "qsv"  // Intel QSV
});

var vaapiDecoder = CodecFactory.Instance.CreateVideoDecoder(new VideoDecoderConfig
{
    Codec = VideoCodec.H264,
    HardwareDevice = "vaapi"  // Linux VA-API
});
```

#### 解码与输出格式

`DecodeAsync` 返回可空结果：**null 表示解码器暂未产出帧（如 B 帧重排导致的输出延迟），不是错误**，后续输入会补齐输出。流式消费请用 `DecodeStreamAsync`（自动 EOF 排空尾帧）。

```csharp
// 单帧解码：必须做 null 检查
VideoFrame? frame = await decoder.DecodeAsync(encodedFrame);
if (frame != null)
{
    // 硬件解码帧下载到 CPU 后通常为 NV12，软件解码通常为 YUV420P，
    // 其余格式内部经 sws_scale 统一转 YUV420P——以 frame.Format 为准
    Console.WriteLine($"{frame.Width}x{frame.Height} {frame.Format}");
}

// 流式解码（推荐）
await foreach (var f in decoder.DecodeStreamAsync(encodedFrames))
{
    Render(f);  // 无需 null 检查
}
```

### 使用硬件编码器

```csharp
var config = new VideoEncoderConfig
{
    Codec = VideoCodec.H264,
    Width = 1920,
    Height = 1080,
    Bitrate = 4000000,
    Framerate = 30,
    EnableHardwareAcceleration = true,
    Preset = "fast"  // NVENC 预设: fast, medium, slow, hq
};

var encoder = CodecFactory.Instance.CreateVideoEncoder(config.Codec);
await encoder.InitializeAsync(config);

// 单帧编码：结果可空，null 表示编码器内部缓冲中（不是错误）
EncodedVideoFrame? encoded = await encoder.EncodeAsync(frame);
if (encoded != null)
{
    Console.WriteLine($"编码帧: {encoded.Data.Length} bytes");
    Console.WriteLine($"关键帧: {encoded.IsKeyFrame}");
}

// 流式编码（推荐）：自动 EOF 排空缓冲
await foreach (var packet in encoder.EncodeStreamAsync(frames))
{
    Send(packet);
}
```

### 自动回退机制

当硬件不可用时，自动回退到软件编码：

```csharp
var decoder = CodecFactory.Instance.CreateVideoDecoder(new VideoDecoderConfig
{
    Codec = VideoCodec.H264,
    EnableHardwareAcceleration = true
});

// 如果没有 NVIDIA GPU，会自动使用软件解码
Console.WriteLine(decoder.Name);
// 有 GPU: "FFmpeg H.264 Decoder (Cuda)"
// 无 GPU: "FFmpeg H.264 Decoder"
```

### 性能对比

| 场景 | 软件解码 | 硬件解码 | 提升 |
|------|----------|----------|------|
| 1080p H.264 | ~30% CPU | ~5% CPU | 6x |
| 4K H.265 | ~80% CPU | ~10% CPU | 8x |
| 1080p H.264 编码 | ~50% CPU | ~8% CPU | 6x |
| 4K H.265 编码 | ~95% CPU | ~15% CPU | 6x |

### 平台特定配置

#### Windows (NVIDIA)

```csharp
// 确保安装了 NVIDIA 驱动
// NVENC 支持 GTX 600+ / Quadro K 系列及以上
var config = new VideoDecoderConfig
{
    Codec = VideoCodec.H264,
    EnableHardwareAcceleration = true,
    HardwareDevice = "cuda"
};
```

#### Windows (Intel QSV)

```csharp
// 需要 Intel CPU 支持 Quick Sync Video
// 第 2 代 Core 处理器及以上
var config = new VideoDecoderConfig
{
    Codec = VideoCodec.H264,
    EnableHardwareAcceleration = true,
    HardwareDevice = "qsv"
};
```

#### Linux (VA-API)

```csharp
// 需要安装 vaapi 驱动
// sudo apt install vainfo intel-media-va-driver (Intel)
// sudo apt install mesa-vdpau-drivers (AMD)
var config = new VideoDecoderConfig
{
    Codec = VideoCodec.H264,
    EnableHardwareAcceleration = true,
    HardwareDevice = "vaapi"
};
```

#### macOS (VideoToolbox)

```csharp
// macOS 自动支持 VideoToolbox
var config = new VideoDecoderConfig
{
    Codec = VideoCodec.H264,
    EnableHardwareAcceleration = true,
    HardwareDevice = "videotoolbox"
};
```

---

## English

### Overview

Cyaim.RTSPClient supports multiple hardware acceleration solutions via FFmpeg, significantly reducing CPU usage and improving codec performance.

### Runtime Requirements

- Requires **FFmpeg 7.x** shared libraries (FFmpeg.AutoGen 7.1.1 binds avcodec-61, etc.), deployed separately.
- Library probe order: `FFMPEG_PATH` environment variable → application output directory and its `ffmpeg` subdirectory → common install locations.
- Use `FFmpegHelper.IsAvailable()` to probe availability (cached, non-throwing).

### Supported Hardware

| Hardware | Vendor | Platform | Encode | Decode |
|----------|--------|----------|--------|--------|
| **NVENC/NVDEC** | NVIDIA | Windows/Linux | H.264, H.265 | H.264, H.265, VP9 |
| **QSV** | Intel | Windows/Linux | H.264, H.265 | H.264, H.265 |
| **AMF** | AMD | Windows | H.264, H.265 | - |
| **VA-API** | - | Linux | H.264, H.265, VP8, VP9 | H.264, H.265 |
| **VideoToolbox** | Apple | macOS/iOS | H.264, H.265 | H.264, H.265 |
| **D3D11VA** | Microsoft | Windows | - | H.264, H.265, VP9 |
| **DXVA2** | Microsoft | Windows | - | H.264, H.265 |
| **Vulkan** | Khronos | All | - | - |

### Detect Hardware Support

```csharp
using Cyaim.RTSPClient.Codecs.FFmpeg.Video;

var supported = FFmpegHardwareHelper.GetSupportedTypes();
foreach (var type in supported)
    Console.WriteLine($"Supported: {type}");

var best = FFmpegHardwareHelper.GetBestType();
Console.WriteLine($"Best: {best}");
```

### Use Hardware Decoder

```csharp
// Auto-select hardware; the config overload initializes the decoder automatically
var decoder = CodecFactory.Instance.CreateVideoDecoder(new VideoDecoderConfig
{
    Codec = VideoCodec.H264,
    Width = 1920,
    Height = 1080,
    EnableHardwareAcceleration = true
});

Console.WriteLine(decoder.Name);  // "FFmpeg H.264 Decoder (Cuda)"

// Specify device
var qsvDecoder = CodecFactory.Instance.CreateVideoDecoder(new VideoDecoderConfig
{
    Codec = VideoCodec.H264,
    HardwareDevice = "qsv"
});

// Single-shot decode is nullable: null means no frame produced yet
// (e.g. B-frame reordering delay) — not an error
VideoFrame? frame = await decoder.DecodeAsync(encodedFrame);
if (frame != null)
{
    // Hardware frames downloaded to CPU are typically NV12; software decode is
    // typically YUV420P; other formats are converted to YUV420P via sws_scale.
    // Always check frame.Format.
    Render(frame);
}

// Streaming (recommended): drains trailing frames via EOF automatically
await foreach (var f in decoder.DecodeStreamAsync(encodedFrames))
    Render(f);
```

### Use Hardware Encoder

```csharp
var config = new VideoEncoderConfig
{
    Codec = VideoCodec.H264,
    Width = 1920,
    Height = 1080,
    Bitrate = 4000000,
    Framerate = 30,
    EnableHardwareAcceleration = true,
    Preset = "fast"
};

var encoder = CodecFactory.Instance.CreateVideoEncoder(config.Codec);
await encoder.InitializeAsync(config);

// Nullable result: null means the encoder is buffering internally (not an error)
EncodedVideoFrame? encoded = await encoder.EncodeAsync(frame);
if (encoded != null)
    Send(encoded);

// Streaming (recommended): drains buffered packets via EOF automatically
await foreach (var packet in encoder.EncodeStreamAsync(frames))
    Send(packet);
```

### Performance Comparison

| Scenario | Software | Hardware | Speedup |
|----------|----------|----------|---------|
| 1080p H.264 decode | ~30% CPU | ~5% CPU | 6x |
| 4K H.265 decode | ~80% CPU | ~10% CPU | 8x |
| 1080p H.264 encode | ~50% CPU | ~8% CPU | 6x |
| 4K H.265 encode | ~95% CPU | ~15% CPU | 6x |

### Auto-fallback

```csharp
// Falls back to software if hardware unavailable
var decoder = CodecFactory.Instance.CreateVideoDecoder(new VideoDecoderConfig
{
    Codec = VideoCodec.H264,
    EnableHardwareAcceleration = true
});

// With GPU: "FFmpeg H.264 Decoder (Cuda)"
// Without GPU: "FFmpeg H.264 Decoder"
```
