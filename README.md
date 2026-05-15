# Cyaim.RTSPClient

[![NuGet Version](https://img.shields.io/nuget/v/Cyaim.RTSPClient.svg)](https://www.nuget.org/packages/Cyaim.RTSPClient/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Cyaim.RTSPClient.svg)](https://www.nuget.org/packages/Cyaim.RTSPClient/)
[![License](https://img.shields.io/github/license/Cyaim/Cyaim.RTSPClient.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-standard2.1%20%7C%2010.0-blue.svg)]()

[中文](#中文) | [English](#english)

---

## 中文

### 简介

Cyaim.RTSPClient 是一个功能完整的 C# RTSP 客户端库，支持完整的 RTSP 协议、RTP/RTCP 传输、H.264/H.265 视频流、G.711 音频流以及 ONVIF 回传通道。

### 功能特性

| 功能 | 说明 |
|------|------|
| **RTSP 协议** | OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, TEARDOWN, GET_PARAMETER, SET_PARAMETER, ANNOUNCE, RECORD |
| **传输模式** | TCP Interleaved, UDP Unicast |
| **视频编码** | H.264, H.265 (HEVC), MJPEG |
| **音频编码** | G.711A (PCMA), G.711U (PCMU), AAC |
| **认证方式** | Digest 认证 (RFC 2617) |
| **心跳保活** | 自动 OPTIONS/GET_PARAMETER 心跳 |
| **自动重连** | 可配置的重连策略 |
| **异步流** | Channel\<T\> 异步数据流 |
| **ONVIF** | 回传通道支持 |

### 安装

```bash
dotnet add package Cyaim.RTSPClient
```

### 快速开始

```csharp
using Cyaim.RTSPClient;
using Cyaim.RTSPClient.Session;

// 配置
var config = new RTSPSessionConfig
{
    Url = "rtsp://192.168.1.127:554",
    Username = "admin",
    Password = "admin",
    AutoReconnect = true
};

// 创建会话
using var session = new RTSPSession(config);

// 事件监听
session.StateChanged += (s, e) => 
    Console.WriteLine($"状态变更: {e.OldState} -> {e.NewState}");
session.DataReceived += (s, e) => 
    Console.WriteLine($"收到数据: {e.Packet.PayloadType}");

// 连接并播放
await session.ConnectAsync();
await session.SetupAsync("trackID=1", TransportMode.TcpInterleaved);
await session.PlayAsync();

// 读取 RTP 数据
var reader = session.GetRTPReader(trackId: 0);
while (await reader.WaitToReadAsync())
{
    while (reader.TryRead(out var packet))
    {
        Console.WriteLine($"Seq: {packet.SequenceNumber}");
    }
}
```

### 配置选项

```csharp
var config = new RTSPSessionConfig
{
    // 连接
    Url = "rtsp://host:port/path",
    ConnectTimeout = TimeSpan.FromSeconds(5),
    ResponseTimeout = TimeSpan.FromSeconds(10),
    
    // 认证
    Username = "admin",
    Password = "password",
    
    // 传输
    TransportMode = TransportMode.TcpInterleaved,
    
    // 自动重连
    AutoReconnect = true,
    MaxReconnectAttempts = 3,
    ReconnectDelay = TimeSpan.FromSeconds(2),
    
    // 心跳
    KeepAliveMethod = "OPTIONS",
    
    // ONVIF
    UseBackchannel = true
};
```

### 目录结构

```
Cyaim.RTSPClient/
├── Session/          # 会话管理
├── Protocol/         # 传输层 (TCP/UDP)
├── RTP/              # RTP 解析和解包
├── RTCP/             # RTCP 反馈
├── Media/            # 媒体描述和编码信息
├── Auth/             # 认证
├── KeepAlive/        # 心跳管理
├── Events/           # 事件参数
├── Exceptions/       # 自定义异常
└── Common/           # 常量和工具
```

### 许可证

MIT License - 详见 [LICENSE](LICENSE)

---

## English

### Introduction

Cyaim.RTSPClient is a full-featured C# RTSP client library supporting complete RTSP protocol, RTP/RTCP transport, H.264/H.265 video streams, G.711 audio streams, and ONVIF backchannel.

### Features

| Feature | Description |
|---------|-------------|
| **RTSP Protocol** | OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, TEARDOWN, GET_PARAMETER, SET_PARAMETER, ANNOUNCE, RECORD |
| **Transport Modes** | TCP Interleaved, UDP Unicast |
| **Video Codecs** | H.264, H.265 (HEVC), MJPEG |
| **Audio Codecs** | G.711A (PCMA), G.711U (PCMU), AAC |
| **Authentication** | Digest authentication (RFC 2617) |
| **Keep-Alive** | Automatic OPTIONS/GET_PARAMETER heartbeat |
| **Auto-Reconnect** | Configurable retry policy |
| **Async Streams** | Channel\<T\> async data flow |
| **ONVIF** | Backchannel support |

### Installation

```bash
dotnet add package Cyaim.RTSPClient
```

### Quick Start

```csharp
using Cyaim.RTSPClient;
using Cyaim.RTSPClient.Session;

// Configuration
var config = new RTSPSessionConfig
{
    Url = "rtsp://192.168.1.127:554",
    Username = "admin",
    Password = "admin",
    AutoReconnect = true
};

// Create session
using var session = new RTSPSession(config);

// Event handlers
session.StateChanged += (s, e) => 
    Console.WriteLine($"State changed: {e.OldState} -> {e.NewState}");
session.DataReceived += (s, e) => 
    Console.WriteLine($"Data received: {e.Packet.PayloadType}");

// Connect and play
await session.ConnectAsync();
await session.SetupAsync("trackID=1", TransportMode.TcpInterleaved);
await session.PlayAsync();

// Read RTP data
var reader = session.GetRTPReader(trackId: 0);
while (await reader.WaitToReadAsync())
{
    while (reader.TryRead(out var packet))
    {
        Console.WriteLine($"Seq: {packet.SequenceNumber}");
    }
}
```

### Configuration Options

```csharp
var config = new RTSPSessionConfig
{
    // Connection
    Url = "rtsp://host:port/path",
    ConnectTimeout = TimeSpan.FromSeconds(5),
    ResponseTimeout = TimeSpan.FromSeconds(10),
    
    // Authentication
    Username = "admin",
    Password = "password",
    
    // Transport
    TransportMode = TransportMode.TcpInterleaved,
    
    // Auto-reconnect
    AutoReconnect = true,
    MaxReconnectAttempts = 3,
    ReconnectDelay = TimeSpan.FromSeconds(2),
    
    // Keep-alive
    KeepAliveMethod = "OPTIONS",
    
    // ONVIF
    UseBackchannel = true
};
```

### Project Structure

```
Cyaim.RTSPClient/
├── Session/          # Session management
├── Protocol/         # Transport layer (TCP/UDP)
├── RTP/              # RTP parsing and depacketization
├── RTCP/             # RTCP feedback
├── Media/            # Media description and codec info
├── Auth/             # Authentication
├── KeepAlive/        # Heartbeat management
├── Events/           # Event arguments
├── Exceptions/       # Custom exceptions
└── Common/           # Constants and utilities
```

### License

MIT License - see [LICENSE](LICENSE)

---

## Links

- [NuGet Package](https://www.nuget.org/packages/Cyaim.RTSPClient/)
- [GitHub Repository](https://github.com/Cyaim/Cyaim.RTSPClient)
- [Documentation Wiki](https://github.com/Cyaim/Cyaim.RTSPClient/wiki)
- [Report Issues](https://github.com/Cyaim/Cyaim.RTSPClient/issues)
