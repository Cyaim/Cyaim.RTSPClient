# FAQ / 常见问题

[中文](#中文) | [English](#english)

---

## 中文

### Q: 如何连接需要认证的 RTSP 服务器？

```csharp
var config = new RTSPSessionConfig
{
    Url = "rtsp://192.168.1.127:554",
    Username = "admin",
    Password = "password"
};
```

### Q: 如何使用 UDP 传输？

```csharp
var config = new RTSPSessionConfig
{
    Url = "rtsp://192.168.1.127:554",
    TransportMode = TransportMode.UdpUnicast
};

await session.SetupAsync("trackID=1", TransportMode.UdpUnicast);
```

### Q: 如何自动重连？

```csharp
var config = new RTSPSessionConfig
{
    AutoReconnect = true,
    MaxReconnectAttempts = 5,
    ReconnectDelay = TimeSpan.FromSeconds(3)
};
```

### Q: 如何发送音频到摄像头（ONVIF 回传）？

```csharp
var config = new RTSPSessionConfig
{
    UseBackchannel = true
};

await session.ConnectAsync();
await session.SetupAsync("trackID=3", TransportMode.TcpInterleaved);
await session.PlayAsync();

byte[] audio = File.ReadAllBytes("audio.g711a");
await session.SendAudioAsync(audio, 8000, RTPPayloadType.PCMA);
```

### Q: 如何读取 H.264 视频帧？

```csharp
var reader = session.GetMediaFrameReader(trackId: 0);
while (await reader.WaitToReadAsync())
{
    while (reader.TryRead(out var frame))
    {
        if (frame.IsKeyFrame)
        {
            // 处理关键帧
            File.WriteAllBytes($"frame_{frame.Timestamp}.h264", frame.Data);
        }
    }
}
```

### Q: 如何处理连接断开？

```csharp
session.StateChanged += (s, e) =>
{
    if (e.NewState == RTSPConnectionState.Disconnected)
    {
        Console.WriteLine("连接断开");
        // AutoReconnect 会自动尝试重连
    }
};

session.Error += (s, e) =>
{
    Console.WriteLine($"错误: {e.Message}");
};
```

---

## English

### Q: How to connect to an authenticated RTSP server?

```csharp
var config = new RTSPSessionConfig
{
    Url = "rtsp://192.168.1.127:554",
    Username = "admin",
    Password = "password"
};
```

### Q: How to use UDP transport?

```csharp
var config = new RTSPSessionConfig
{
    Url = "rtsp://192.168.1.127:554",
    TransportMode = TransportMode.UdpUnicast
};

await session.SetupAsync("trackID=1", TransportMode.UdpUnicast);
```

### Q: How to enable auto-reconnect?

```csharp
var config = new RTSPSessionConfig
{
    AutoReconnect = true,
    MaxReconnectAttempts = 5,
    ReconnectDelay = TimeSpan.FromSeconds(3)
};
```

### Q: How to send audio to camera (ONVIF backchannel)?

```csharp
var config = new RTSPSessionConfig
{
    UseBackchannel = true
};

await session.ConnectAsync();
await session.SetupAsync("trackID=3", TransportMode.TcpInterleaved);
await session.PlayAsync();

byte[] audio = File.ReadAllBytes("audio.g711a");
await session.SendAudioAsync(audio, 8000, RTPPayloadType.PCMA);
```

### Q: How to read H.264 video frames?

```csharp
var reader = session.GetMediaFrameReader(trackId: 0);
while (await reader.WaitToReadAsync())
{
    while (reader.TryRead(out var frame))
    {
        if (frame.IsKeyFrame)
        {
            // Process key frame
            File.WriteAllBytes($"frame_{frame.Timestamp}.h264", frame.Data);
        }
    }
}
```
