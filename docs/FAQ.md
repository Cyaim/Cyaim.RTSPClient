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
    TransportMode = TransportMode.UdpUnicast // UdpMulticast 暂不支持
};

await using var session = new RTSPSession(config);
await session.StartAsync(); // SETUP 自动使用 UDP

// 低层 API 也可以：await session.SetupUdpAsync("trackID=1");
// 或：await session.SetupAsync("trackID=1", TransportMode.UdpUnicast);
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
    Url = "rtsp://192.168.1.127:554",
    Username = "admin",
    Password = "admin",
    UseBackchannel = true
};

await using var session = new RTSPSession(config);
await session.StartAsync(); // 自动 SETUP 回传轨道

byte[] audio = File.ReadAllBytes("audio.g711a");
await session.SendAudioAsync(audio, 8000, RTPPayloadType.PCMA); // RTPPayloadType 位于 Cyaim.RTSPClient.Common
```

### Q: 如何读取 H.264 视频帧？

```csharp
// 需在 StartAsync()（或 DESCRIBE+SETUP）之后调用，自动按 SDP 选择解包器
var reader = session.GetMediaFrameReader(trackId: 0);
while (await reader.WaitToReadAsync())
{
    while (reader.TryRead(out var frame))
    {
        if (frame.IsKeyFrame)
        {
            // 处理关键帧（frame.IsAccessUnitEnd 可用于判断帧边界）
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
        // AutoReconnect 会自动尝试重连，并恢复 SETUP/PLAY
    }
};

session.Reconnecting += (s, attempt) =>
    Console.WriteLine($"正在第 {attempt} 次重连...");

session.Reconnected += (s, e) =>
    Console.WriteLine("重连成功");

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
    TransportMode = TransportMode.UdpUnicast // UdpMulticast is not supported yet
};

await using var session = new RTSPSession(config);
await session.StartAsync(); // SETUP automatically uses UDP

// Low-level API also works: await session.SetupUdpAsync("trackID=1");
// or: await session.SetupAsync("trackID=1", TransportMode.UdpUnicast);
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
    Url = "rtsp://192.168.1.127:554",
    Username = "admin",
    Password = "admin",
    UseBackchannel = true
};

await using var session = new RTSPSession(config);
await session.StartAsync(); // Backchannel track is set up automatically

byte[] audio = File.ReadAllBytes("audio.g711a");
await session.SendAudioAsync(audio, 8000, RTPPayloadType.PCMA); // RTPPayloadType lives in Cyaim.RTSPClient.Common
```

### Q: How to read H.264 video frames?

```csharp
// Call after StartAsync() (or DESCRIBE+SETUP); the depacketizer is chosen automatically from the SDP
var reader = session.GetMediaFrameReader(trackId: 0);
while (await reader.WaitToReadAsync())
{
    while (reader.TryRead(out var frame))
    {
        if (frame.IsKeyFrame)
        {
            // Process key frame (frame.IsAccessUnitEnd marks the frame boundary)
            File.WriteAllBytes($"frame_{frame.Timestamp}.h264", frame.Data);
        }
    }
}
```
