# API Reference / API 参考

[中文](#中文) | [English](#english)

---

## 中文

### 核心类

#### RTSPSession

主会话类，用于管理 RTSP 连接和媒体流。

```csharp
public class RTSPSession : IDisposable
{
    // 属性
    public RTSPConnectionState State { get; }
    public Uri Uri { get; }
    public SDP SDP { get; }
    public string SessionId { get; }
    
    // 事件
    public event EventHandler<RTSPConnectionStateChangedEventArgs> StateChanged;
    public event EventHandler<RTPDataReceivedEventArgs> DataReceived;
    public event EventHandler<RTSPErrorEventArgs> Error;
    public event EventHandler<KeepAliveEventArgs> KeepAlive;
    
    // 构造函数
    public RTSPSession(RTSPSessionConfig config);
    
    // 生命周期
    public Task ConnectAsync(CancellationToken ct = default);
    public Task DisconnectAsync(CancellationToken ct = default);
    
    // RTSP 方法
    public Task<RTSPResponse> OptionsAsync(CancellationToken ct = default);
    public Task<RTSPResponse> DescribeAsync(CancellationToken ct = default);
    public Task<RTSPResponse> SetupAsync(string trackUri, TransportMode mode, CancellationToken ct = default);
    public Task<RTSPResponse> PlayAsync(string? range = null, CancellationToken ct = default);
    public Task<RTSPResponse> PauseAsync(CancellationToken ct = default);
    public Task<RTSPResponse> TeardownAsync(CancellationToken ct = default);
    public Task<RTSPResponse> GetParameterAsync(string[]? parameters = null, CancellationToken ct = default);
    public Task<RTSPResponse> SetParameterAsync(Dictionary<string, string> parameters, CancellationToken ct = default);
    
    // 媒体流
    public ChannelReader<RTPPacket> GetRTPReader(int trackId);
    public ChannelReader<MediaFrame> GetMediaFrameReader(int trackId);
    public Task SendAudioAsync(byte[] audio, int sampleRate, RTPPayloadType codec, CancellationToken ct = default);
}
```

#### RTPPacket

RTP 数据包结构体。

```csharp
public readonly struct RTPPacket
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
    TcpInterleaved,
    UdpUnicast,
    UdpMulticast
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

Main session class for managing RTSP connections and media streams.

```csharp
public class RTSPSession : IDisposable
{
    // Properties
    public RTSPConnectionState State { get; }
    public Uri Uri { get; }
    public SDP SDP { get; }
    public string SessionId { get; }
    
    // Events
    public event EventHandler<RTSPConnectionStateChangedEventArgs> StateChanged;
    public event EventHandler<RTPDataReceivedEventArgs> DataReceived;
    public event EventHandler<RTSPErrorEventArgs> Error;
    public event EventHandler<KeepAliveEventArgs> KeepAlive;
    
    // Constructor
    public RTSPSession(RTSPSessionConfig config);
    
    // Lifecycle
    public Task ConnectAsync(CancellationToken ct = default);
    public Task DisconnectAsync(CancellationToken ct = default);
    
    // RTSP Methods
    public Task<RTSPResponse> OptionsAsync(CancellationToken ct = default);
    public Task<RTSPResponse> DescribeAsync(CancellationToken ct = default);
    public Task<RTSPResponse> SetupAsync(string trackUri, TransportMode mode, CancellationToken ct = default);
    public Task<RTSPResponse> PlayAsync(string? range = null, CancellationToken ct = default);
    public Task<RTSPResponse> PauseAsync(CancellationToken ct = default);
    public Task<RTSPResponse> TeardownAsync(CancellationToken ct = default);
    
    // Media Streams
    public ChannelReader<RTPPacket> GetRTPReader(int trackId);
    public ChannelReader<MediaFrame> GetMediaFrameReader(int trackId);
    public Task SendAudioAsync(byte[] audio, int sampleRate, RTPPayloadType codec, CancellationToken ct = default);
}
```

#### RTPPacket

RTP packet structure.

```csharp
public readonly struct RTPPacket
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
    TcpInterleaved,
    UdpUnicast,
    UdpMulticast
}
```
