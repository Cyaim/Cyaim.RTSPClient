# Cyaim.RTSPClient

[![NuGet Version](https://img.shields.io/nuget/v/Cyaim.RTSPClient.svg)](https://www.nuget.org/packages/Cyaim.RTSPClient/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Cyaim.RTSPClient.svg)](https://www.nuget.org/packages/Cyaim.RTSPClient/)
[![License](https://img.shields.io/github/license/Cyaim/Cyaim.RTSPClient.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-standard2.1%20%7C%2010.0-blue.svg)]()

[中文](#中文) | [English](#english)

---

## 中文

### 简介

Cyaim.RTSPClient 是一个功能完整的 C# RTSP 客户端库，支持完整的 RTSP 协议、RTP/RTCP 传输、H.264/H.265 视频流、多种音频编码以及 ONVIF 回传通道。支持 NVIDIA NVENC/NVDEC、Intel QSV 等硬件加速。

### 功能特性

| 功能 | 说明 |
|------|------|
| **RTSP 协议** | OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, TEARDOWN, GET_PARAMETER, SET_PARAMETER, ANNOUNCE, RECORD |
| **传输模式** | TCP Interleaved, UDP Unicast |
| **视频编码** | H.264, H.265 (HEVC), VP8, VP9, MJPEG |
| **音频编码** | G.711A/U, G.722, G.726, G.729, AAC, Opus, AMR, Speex, Vorbis, MP3 |
| **硬件加速** | NVIDIA NVENC/NVDEC, Intel QSV, AMD AMF, VA-API, VideoToolbox, D3D11VA |
| **认证方式** | Digest 认证 (RFC 2617) |
| **心跳保活** | 自动 OPTIONS/GET_PARAMETER 心跳 |
| **自动重连** | 可配置的重连策略 |
| **异步流** | Channel\<T\> 异步数据流 |
| **ONVIF** | 回传通道支持 |

### 安装

```bash
# 核心库
dotnet add package Cyaim.RTSPClient

# FFmpeg 视频编解码器 (可选，支持硬件加速)
dotnet add package Cyaim.RTSPClient.Codecs.FFmpeg.Video

# FFmpeg 音频编解码器 (可选)
dotnet add package Cyaim.RTSPClient.Codecs.FFmpeg.Audio
```

### 快速开始

#### 基本用法

```csharp
using Cyaim.RTSPClient;
using Cyaim.RTSPClient.Session;

var config = new RTSPSessionConfig
{
    Url = "rtsp://192.168.1.127:554",
    Username = "admin",
    Password = "admin",
    AutoReconnect = true
};

using var session = new RTSPSession(config);

session.StateChanged += (s, e) => 
    Console.WriteLine($"状态变更: {e.OldState} -> {e.NewState}");

await session.ConnectAsync();
await session.SetupAsync("trackID=1", TransportMode.TcpInterleaved);
await session.PlayAsync();

var reader = session.GetRTPReader(trackId: 0);
while (await reader.WaitToReadAsync())
{
    while (reader.TryRead(out var packet))
    {
        Console.WriteLine($"Seq: {packet.SequenceNumber}");
    }
}
```

#### 视频接收

```csharp
using Cyaim.RTSPClient.Rtp;

var videoReceiver = VideoReceiver.CreateH264Receiver(session.GetRTPReader(0));
videoReceiver.KeyFrameReceived += (s, e) =>
    Console.WriteLine($"关键帧: {e.Frame.Data.Length} bytes");

videoReceiver.StartReceiving();

var reader = videoReceiver.GetFrameReader();
while (await reader.WaitToReadAsync())
{
    while (reader.TryRead(out var frame))
    {
        // 处理视频帧
    }
}
```

#### 硬件加速解码

```csharp
using Cyaim.RTSPClient.Codecs.FFmpeg.Video;

// 注册 FFmpeg 编解码器
CodecFactory.Instance.RegisterFactory(new FFmpegVideoDecoderFactory());
CodecFactory.Instance.RegisterFactory(new FFmpegVideoEncoderFactory());

// 创建硬件解码器
var decoder = CodecFactory.Instance.CreateVideoDecoder(new VideoDecoderConfig
{
    Codec = VideoCodec.H264,
    Width = 1920,
    Height = 1080,
    EnableHardwareAcceleration = true  // 自动选择最佳硬件
});

Console.WriteLine(decoder.Name);  // "FFmpeg H.264 Decoder (Cuda)"
Console.WriteLine(decoder.IsHardwareAccelerated);  // true
```

#### FFmpeg 音频编解码

```csharp
using Cyaim.RTSPClient.Codecs.FFmpeg.Audio;

CodecFactory.Instance.RegisterFactory(new FFmpegAudioDecoderFactory());
CodecFactory.Instance.RegisterFactory(new FFmpegAudioEncoderFactory());

// AAC 解码
var aacDecoder = CodecFactory.Instance.CreateAudioDecoder(AudioCodec.AAC);

// Opus 编码
var opusEncoder = CodecFactory.Instance.CreateAudioEncoder(AudioCodec.OPUS);
```

### 编解码器

#### 内置编解码器 (纯 C#)

| 编码 | 解码 | 编码 | 说明 |
|------|------|------|------|
| G.711A/U | ✅ | ✅ | 8kHz, 64kbps |
| G.722 | ✅ | ✅ | 16kHz, 48/56/64kbps |
| G.726 | ✅ | ✅ | 8kHz, 16/24/32/40kbps |
| G.729 | ✅ | ✅ | 8kHz, 8kbps |
| AAC-LC | ✅ | ✅ | 44.1kHz |

#### FFmpeg 编解码器

| 编码 | 解码 | 编码 | 硬件加速 |
|------|------|------|----------|
| H.264 | ✅ | ✅ | NVENC, QSV, AMF, VAAPI |
| H.265 | ✅ | ✅ | NVENC, QSV, AMF, VAAPI |
| VP8 | ✅ | ✅ | VAAPI |
| VP9 | ✅ | ✅ | VAAPI |
| AAC | ✅ | ✅ | - |
| Opus | ✅ | ✅ | - |
| AMR | ✅ | ✅ | - |
| Speex | ✅ | ✅ | - |

### 配置选项

```csharp
var config = new RTSPSessionConfig
{
    Url = "rtsp://host:port/path",
    ConnectTimeout = TimeSpan.FromSeconds(5),
    ResponseTimeout = TimeSpan.FromSeconds(10),
    Username = "admin",
    Password = "password",
    TransportMode = TransportMode.TcpInterleaved,
    AutoReconnect = true,
    MaxReconnectAttempts = 3,
    KeepAliveMethod = "OPTIONS",
    UseBackchannel = true
};
```

### 项目结构

```
Cyaim.RTSPClient/                  # 核心库
├── Session/                        # 会话管理
├── Protocol/                       # 传输层 (TCP/UDP)
├── RTP/                            # RTP 解析和解包
├── RTCP/                           # RTCP 反馈
├── Media/Codecs/                   # 内置编解码器
├── Auth/                           # 认证
└── KeepAlive/                      # 心跳管理

Cyaim.RTSPClient.Codecs.FFmpeg.Video/  # FFmpeg 视频编解码器
├── Decoders/                       # H.264, H.265, VP8, VP9, MJPEG
├── Encoders/                       # H.264, H.265, VP8, VP9
└── FFmpegHardwareHelper.cs         # 硬件加速工具

Cyaim.RTSPClient.Codecs.FFmpeg.Audio/  # FFmpeg 音频编解码器
├── Decoders/                       # AAC, Opus, AMR, Speex, Vorbis, MP3
└── Encoders/                       # AAC, Opus, AMR, Speex, Vorbis, MP3
```

### 许可证

MIT License - 详见 [LICENSE](LICENSE)

---

## English

### Introduction

Cyaim.RTSPClient is a full-featured C# RTSP client library with complete RTSP protocol, RTP/RTCP transport, H.264/H.265 video, multi-codec audio, and ONVIF backchannel support. Hardware acceleration via NVIDIA NVENC/NVDEC, Intel QSV, and more.

### Features

| Feature | Description |
|---------|-------------|
| **RTSP Protocol** | OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, TEARDOWN, GET_PARAMETER, SET_PARAMETER, ANNOUNCE, RECORD |
| **Transport** | TCP Interleaved, UDP Unicast |
| **Video Codecs** | H.264, H.265 (HEVC), VP8, VP9, MJPEG |
| **Audio Codecs** | G.711A/U, G.722, G.726, G.729, AAC, Opus, AMR, Speex, Vorbis, MP3 |
| **HW Accel** | NVIDIA NVENC/NVDEC, Intel QSV, AMD AMF, VA-API, VideoToolbox, D3D11VA |
| **Auth** | Digest (RFC 2617) |
| **Keep-Alive** | Auto OPTIONS/GET_PARAMETER heartbeat |
| **Reconnect** | Configurable retry policy |
| **ONVIF** | Backchannel support |

### Installation

```bash
# Core
dotnet add package Cyaim.RTSPClient

# FFmpeg video codecs (optional, with hardware acceleration)
dotnet add package Cyaim.RTSPClient.Codecs.FFmpeg.Video

# FFmpeg audio codecs (optional)
dotnet add package Cyaim.RTSPClient.Codecs.FFmpeg.Audio
```

### Quick Start

```csharp
using Cyaim.RTSPClient;
using Cyaim.RTSPClient.Session;

var config = new RTSPSessionConfig
{
    Url = "rtsp://192.168.1.127:554",
    Username = "admin",
    Password = "admin",
    AutoReconnect = true
};

using var session = new RTSPSession(config);
await session.ConnectAsync();
await session.SetupAsync("trackID=1", TransportMode.TcpInterleaved);
await session.PlayAsync();

var reader = session.GetRTPReader(trackId: 0);
while (await reader.WaitToReadAsync())
{
    while (reader.TryRead(out var packet))
        Console.WriteLine($"Seq: {packet.SequenceNumber}");
}
```

### Hardware Acceleration

```csharp
using Cyaim.RTSPClient.Codecs.FFmpeg.Video;

CodecFactory.Instance.RegisterFactory(new FFmpegVideoDecoderFactory());

var decoder = CodecFactory.Instance.CreateVideoDecoder(new VideoDecoderConfig
{
    Codec = VideoCodec.H264,
    Width = 1920,
    Height = 1080,
    EnableHardwareAcceleration = true
});

// Auto-selects best hardware: CUDA > QSV > D3D11VA > DXVA2 > VAAPI
Console.WriteLine(decoder.Name);  // "FFmpeg H.264 Decoder (Cuda)"
```

### License

MIT License - see [LICENSE](LICENSE)

---

## Links

- [NuGet Package](https://www.nuget.org/packages/Cyaim.RTSPClient/)
- [GitHub Repository](https://github.com/Cyaim/Cyaim.RTSPClient)
- [Documentation Wiki](https://github.com/Cyaim/Cyaim.RTSPClient/wiki)
- [Report Issues](https://github.com/Cyaim/Cyaim.RTSPClient/issues)
