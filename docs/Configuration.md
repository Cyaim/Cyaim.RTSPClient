# Configuration / 配置

[中文](#中文) | [English](#english)

---

## 中文

### RTSPSessionConfig 配置类

```csharp
var config = new RTSPSessionConfig
{
    // 连接配置
    Url = "rtsp://192.168.1.127:554",
    ConnectTimeout = TimeSpan.FromSeconds(5),
    ResponseTimeout = TimeSpan.FromSeconds(10),
    
    // 认证配置
    Username = "admin",
    Password = "password",
    
    // 传输配置
    TransportMode = TransportMode.TcpInterleaved,
    
    // 自动重连配置
    AutoReconnect = true,
    MaxReconnectAttempts = 3,
    ReconnectDelay = TimeSpan.FromSeconds(2),
    
    // 心跳配置
    KeepAliveMethod = "OPTIONS",
    
    // ONVIF 配置
    UseBackchannel = true,
    BackchannelRequire = "www.onvif.org/ver20/backchannel",
    
    // 缓冲区配置
    RtpChannelBufferSize = 1024
};
```

### 配置项说明

| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| `Url` | string | - | RTSP 服务器地址 |
| `ConnectTimeout` | TimeSpan | 5s | TCP 连接超时 |
| `ResponseTimeout` | TimeSpan | 10s | RTSP 响应超时 |
| `Username` | string | - | 认证用户名 |
| `Password` | string | - | 认证密码 |
| `TransportMode` | TransportMode | TcpInterleaved | 传输模式 |
| `AutoReconnect` | bool | true | 是否自动重连 |
| `MaxReconnectAttempts` | int | 3 | 最大重连次数 |
| `ReconnectDelay` | TimeSpan | 2s | 重连延迟 |
| `KeepAliveMethod` | string | "OPTIONS" | 心跳方法 |
| `UseBackchannel` | bool | false | 是否使用 ONVIF 回传 |
| `RtpChannelBufferSize` | int | 1024 | RTP 通道缓冲大小 |

### 传输模式

```csharp
public enum TransportMode
{
    TcpInterleaved,  // RTP over TCP (默认)
    UdpUnicast,      // RTP over UDP 单播
    UdpMulticast     // RTP over UDP 多播
}
```

---

## English

### RTSPSessionConfig Class

```csharp
var config = new RTSPSessionConfig
{
    // Connection
    Url = "rtsp://192.168.1.127:554",
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
    UseBackchannel = true,
    BackchannelRequire = "www.onvif.org/ver20/backchannel",
    
    // Buffer
    RtpChannelBufferSize = 1024
};
```

### Configuration Options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `Url` | string | - | RTSP server URL |
| `ConnectTimeout` | TimeSpan | 5s | TCP connection timeout |
| `ResponseTimeout` | TimeSpan | 10s | RTSP response timeout |
| `Username` | string | - | Authentication username |
| `Password` | string | - | Authentication password |
| `TransportMode` | TransportMode | TcpInterleaved | Transport mode |
| `AutoReconnect` | bool | true | Enable auto-reconnect |
| `MaxReconnectAttempts` | int | 3 | Max reconnect attempts |
| `ReconnectDelay` | TimeSpan | 2s | Reconnect delay |
| `KeepAliveMethod` | string | "OPTIONS" | Keep-alive method |
| `UseBackchannel` | bool | false | Enable ONVIF backchannel |
| `RtpChannelBufferSize` | int | 1024 | RTP channel buffer size |

### Transport Modes

```csharp
public enum TransportMode
{
    TcpInterleaved,  // RTP over TCP (default)
    UdpUnicast,      // RTP over UDP unicast
    UdpMulticast     // RTP over UDP multicast
}
```
