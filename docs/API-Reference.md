# API Reference / API 参考

[中文](#中文) | [English](#english)

---

## 中文

### 核心类

#### RTSPSession

主会话类，用于管理 RTSP 连接和媒体流。实现 `IRTSPSession` 接口。

```csharp
public class RTSPSession : IRTSPSession, IAsyncDisposable
{
    // 属性
    public RTSPConnectionState State { get; }
    public Uri? Uri { get; }
    public SDPSession? SDP { get; }
    public string? SessionId { get; }
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public bool AutoKeepAlive { get; set; }       // 默认 true，按 Session timeout 自动发送 GET_PARAMETER 保活
    public bool AutoReconnect { get; set; }       // 断线自动重连并恢复 SETUP/PLAY
    public int MaxReconnectAttempts { get; set; }
    public int ReconnectDelayMs { get; set; }
    public int ConnectTimeoutMs { get; set; }
    public bool EnableRtcp { get; set; }          // 默认 true，自动发送 RTCP RR
    public int ReceiveQueueCapacity { get; set; }
    public long PacketsDropped { get; }           // 因消费过慢被丢弃的 RTP 包计数
    
    // 事件
    public event EventHandler<RTSPConnectionStateChangedEventArgs>? StateChanged;
    public event EventHandler<RtpDataReceivedEventArgs>? DataReceived;  // 独立泵线程分发，慢处理不阻塞收流
    public event EventHandler<RTSPErrorEventArgs>? Error;
    public event EventHandler<KeepAliveEventArgs>? KeepAlive;
    public event EventHandler<int>? Reconnecting;                       // 参数为当前重连次数
    public event EventHandler? Reconnected;
    public event EventHandler<RtcpSenderReportEventArgs>? SenderReportReceived; // RTCP SR：RTP 时间戳 ↔ 墙钟映射（DVR 对时）
    
    // 构造函数
    public RTSPSession(RTSPSessionConfig config);
    public RTSPSession(string url);
    public RTSPSession(Uri uri);
    
    // 生命周期
    public Task StartAsync(CancellationToken ct = default);  // 一键：连接 → OPTIONS → DESCRIBE（自动认证）→ SETUP 全轨 → PLAY
    public Task ConnectAsync(CancellationToken ct = default);
    public Task DisconnectAsync(CancellationToken ct = default);
    public ValueTask DisposeAsync();                         // 优雅关闭（推荐 await using）
    public void Dispose();                                   // 立即关闭，不发送 TEARDOWN
    
    // RTSP 方法
    public Task<RTSPResponse> OptionsAsync(CancellationToken ct = default);
    public Task<RTSPResponse> DescribeAsync(bool useBackchannel = false, CancellationToken ct = default);
    public Task<RTSPResponse> SetupAsync(string trackUri, TransportMode mode, CancellationToken ct = default);
    public Task<RTSPResponse> SetupAsync(string channelUri, string transport, bool useBackchannel = false, CancellationToken ct = default);
    public Task<RTSPResponse> SetupUdpAsync(string channelUri, CancellationToken ct = default);
    public Task<RTSPResponse> PlayAsync(string? range = null, bool useBackchannel = false, CancellationToken ct = default);
    public Task<RTSPResponse> PauseAsync(CancellationToken ct = default);
    public Task<RTSPResponse> TeardownAsync(string? channelUri = null, bool useBackchannel = false, CancellationToken ct = default);
    public Task<RTSPResponse> GetParameterAsync(string[]? parameters = null, CancellationToken ct = default);
    public Task<RTSPResponse> SetParameterAsync(Dictionary<string, string> parameters, CancellationToken ct = default);
    
    // 媒体流
    public ChannelReader<RTPPacket> GetRtpReader(int trackId);
    public ChannelReader<MediaFrame> GetMediaFrameReader(int trackId);  // 自动选择 H264/H265/AAC/G711 解包器并注入 SDP 参数集
    public Task SendAudioAsync(byte[] audio, int sampleRate, RTPPayloadType codec, CancellationToken ct = default);
}
```

#### RTPPacket

RTP 数据包结构体。

```csharp
public readonly record struct RTPPacket
{
    public byte Version { get; }
    public bool Padding { get; }
    public bool Extension { get; }
    public byte CsrcCount { get; }
    public bool Marker { get; }
    public byte PayloadType { get; }
    public ushort SequenceNumber { get; }
    public uint Timestamp { get; }
    public uint Ssrc { get; }
    public uint[] Csrc { get; }
    public ArraySegment<byte> PayloadSegment { get; } // zero-copy payload slice
    public byte[] Payload { get; }
    public int TrackId { get; }
    public StreamType StreamType { get; }
    public byte[] Raw { get; }
}
```

#### MediaFrame

解包后的媒体帧。

```csharp
public readonly struct MediaFrame
{
    public byte[] Data { get; }
    public uint Timestamp { get; }
    public bool IsKeyFrame { get; }
    public bool IsAccessUnitEnd { get; } // 帧边界（源自 RTP marker 位）
    public StreamType StreamType { get; }
    public int TrackId { get; }
}
```

### 枚举

#### RTSPConnectionState

```csharp
public enum RTSPConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Ready,
    Setup,
    Playing,
    Paused,
    Disconnecting
}
```

#### TransportMode

```csharp
public enum TransportMode
{
    TcpInterleaved,  // RTP over TCP (interleaved)
    UdpUnicast,      // RTP over UDP
    UdpMulticast     // 暂不支持 / not supported yet
}
```

#### StreamType

```csharp
public enum StreamType
{
    Video,
    Audio,
    Application,
    Text
}
```

#### RTPPayloadType

```csharp
public enum RTPPayloadType
{
    PCMU = 0,
    GSM = 3,
    G723 = 4,
    PCMA = 8,
    G722 = 9,
    // ... 更多类型
}
```

---

## English

### Core Classes

#### RTSPSession

Main session class for managing RTSP connections and media streams. Implements the `IRTSPSession` interface.

```csharp
public class RTSPSession : IRTSPSession, IAsyncDisposable
{
    // Properties
    public RTSPConnectionState State { get; }
    public Uri? Uri { get; }
    public SDPSession? SDP { get; }
    public string? SessionId { get; }
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public bool AutoKeepAlive { get; set; }       // default true; sends GET_PARAMETER automatically based on the Session timeout
    public bool AutoReconnect { get; set; }       // auto reconnect and restore SETUP/PLAY on disconnect
    public int MaxReconnectAttempts { get; set; }
    public int ReconnectDelayMs { get; set; }
    public int ConnectTimeoutMs { get; set; }
    public bool EnableRtcp { get; set; }          // default true; sends RTCP RR automatically
    public int ReceiveQueueCapacity { get; set; }
    public long PacketsDropped { get; }           // RTP packets dropped due to slow consumers
    
    // Events
    public event EventHandler<RTSPConnectionStateChangedEventArgs>? StateChanged;
    public event EventHandler<RtpDataReceivedEventArgs>? DataReceived;  // dispatched on a dedicated pump thread; slow handlers do not block receiving
    public event EventHandler<RTSPErrorEventArgs>? Error;
    public event EventHandler<KeepAliveEventArgs>? KeepAlive;
    public event EventHandler<int>? Reconnecting;                       // argument is the current attempt count
    public event EventHandler? Reconnected;
    public event EventHandler<RtcpSenderReportEventArgs>? SenderReportReceived; // RTCP SR: RTP timestamp ↔ wall-clock mapping (DVR time sync)
    
    // Constructors
    public RTSPSession(RTSPSessionConfig config);
    public RTSPSession(string url);
    public RTSPSession(Uri uri);
    
    // Lifecycle
    public Task StartAsync(CancellationToken ct = default);  // one call: Connect → OPTIONS → DESCRIBE (auto auth) → SETUP all tracks → PLAY
    public Task ConnectAsync(CancellationToken ct = default);
    public Task DisconnectAsync(CancellationToken ct = default);
    public ValueTask DisposeAsync();                         // graceful shutdown (prefer await using)
    public void Dispose();                                   // immediate close, no TEARDOWN sent
    
    // RTSP Methods
    public Task<RTSPResponse> OptionsAsync(CancellationToken ct = default);
    public Task<RTSPResponse> DescribeAsync(bool useBackchannel = false, CancellationToken ct = default);
    public Task<RTSPResponse> SetupAsync(string trackUri, TransportMode mode, CancellationToken ct = default);
    public Task<RTSPResponse> SetupAsync(string channelUri, string transport, bool useBackchannel = false, CancellationToken ct = default);
    public Task<RTSPResponse> SetupUdpAsync(string channelUri, CancellationToken ct = default);
    public Task<RTSPResponse> PlayAsync(string? range = null, bool useBackchannel = false, CancellationToken ct = default);
    public Task<RTSPResponse> PauseAsync(CancellationToken ct = default);
    public Task<RTSPResponse> TeardownAsync(string? channelUri = null, bool useBackchannel = false, CancellationToken ct = default);
    public Task<RTSPResponse> GetParameterAsync(string[]? parameters = null, CancellationToken ct = default);
    public Task<RTSPResponse> SetParameterAsync(Dictionary<string, string> parameters, CancellationToken ct = default);
    
    // Media Streams
    public ChannelReader<RTPPacket> GetRtpReader(int trackId);
    public ChannelReader<MediaFrame> GetMediaFrameReader(int trackId);  // automatically picks the H264/H265/AAC/G711 depacketizer and injects SDP parameter sets
    public Task SendAudioAsync(byte[] audio, int sampleRate, RTPPayloadType codec, CancellationToken ct = default);
}
```

#### RTPPacket

RTP packet structure.

```csharp
public readonly record struct RTPPacket
{
    public byte Version { get; }
    public bool Padding { get; }
    public bool Extension { get; }
    public byte CsrcCount { get; }
    public bool Marker { get; }
    public byte PayloadType { get; }
    public ushort SequenceNumber { get; }
    public uint Timestamp { get; }
    public uint Ssrc { get; }
    public uint[] Csrc { get; }
    public ArraySegment<byte> PayloadSegment { get; } // zero-copy payload slice
    public byte[] Payload { get; }
    public int TrackId { get; }
    public StreamType StreamType { get; }
    public byte[] Raw { get; }
}
```

### Enums

#### RTSPConnectionState

```csharp
public enum RTSPConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Ready,
    Setup,
    Playing,
    Paused,
    Disconnecting
}
```

#### TransportMode

```csharp
public enum TransportMode
{
    TcpInterleaved,  // RTP over TCP (interleaved)
    UdpUnicast,      // RTP over UDP
    UdpMulticast     // 暂不支持 / not supported yet
}
```
