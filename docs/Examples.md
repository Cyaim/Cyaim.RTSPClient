# Examples / 示例

[中文](#中文) | [English](#english)

---

## 中文

### 示例 1: 基本视频流

```csharp
using Cyaim.RTSPClient;
using Cyaim.RTSPClient.Session;

var config = new RTSPSessionConfig
{
    Url = "rtsp://192.168.1.127:554",
    Username = "admin",
    Password = "admin"
};

await using var session = new RTSPSession(config);

session.DataReceived += (s, e) =>
{
    var p = e.Packet;
    if (p.StreamType == StreamType.Video)
    {
        Console.WriteLine($"视频: Seq={p.SequenceNumber}, Size={p.Payload.Length}");
    }
};

await session.StartAsync(); // Connect → OPTIONS → DESCRIBE → SETUP → PLAY

await Task.Delay(Timeout.Infinite);
```

### 示例 2: 保存 H.264 到文件

```csharp
await using var session = new RTSPSession(config);
await session.StartAsync(); // Connect → OPTIONS → DESCRIBE → SETUP → PLAY

using var fs = File.Create("output.h264");
var reader = session.GetMediaFrameReader(0);

await foreach (var frame in reader.ReadAllAsync())
{
    if (frame.IsKeyFrame)
    {
        // 写入 SPS/PPS (如果有)
    }
    await fs.WriteAsync(frame.Data);
}
```

### 示例 3: 音频回传

```csharp
using Cyaim.RTSPClient;
using Cyaim.RTSPClient.Common;
using Cyaim.RTSPClient.Session;

var config = new RTSPSessionConfig
{
    Url = "rtsp://192.168.1.127:554",
    Username = "admin",
    Password = "admin",
    UseBackchannel = true
};

await using var session = new RTSPSession(config);
await session.StartAsync(); // UseBackchannel = true: backchannel track is set up automatically

byte[] audio = File.ReadAllBytes("audio.g711a");
await session.SendAudioAsync(audio, 8000, RTPPayloadType.PCMA);
```

### 示例 4: UDP 传输

```csharp
var config = new RTSPSessionConfig
{
    Url = "rtsp://192.168.1.127:554",
    TransportMode = TransportMode.UdpUnicast // UdpMulticast is not supported yet
};

await using var session = new RTSPSession(config);
await session.StartAsync(); // TransportMode.UdpUnicast: SETUP automatically uses UDP
```

---

## English

### Example 1: Basic Video Stream

```csharp
using Cyaim.RTSPClient;
using Cyaim.RTSPClient.Session;

var config = new RTSPSessionConfig
{
    Url = "rtsp://192.168.1.127:554",
    Username = "admin",
    Password = "admin"
};

await using var session = new RTSPSession(config);

session.DataReceived += (s, e) =>
{
    var p = e.Packet;
    if (p.StreamType == StreamType.Video)
    {
        Console.WriteLine($"Video: Seq={p.SequenceNumber}, Size={p.Payload.Length}");
    }
};

await session.StartAsync(); // Connect → OPTIONS → DESCRIBE → SETUP → PLAY

await Task.Delay(Timeout.Infinite);
```

### Example 2: Save H.264 to File

```csharp
await using var session = new RTSPSession(config);
await session.StartAsync(); // Connect → OPTIONS → DESCRIBE → SETUP → PLAY

using var fs = File.Create("output.h264");
var reader = session.GetMediaFrameReader(0);

await foreach (var frame in reader.ReadAllAsync())
{
    if (frame.IsKeyFrame)
    {
        // Write SPS/PPS if available
    }
    await fs.WriteAsync(frame.Data);
}
```

### Example 3: Audio Backchannel

```csharp
using Cyaim.RTSPClient;
using Cyaim.RTSPClient.Common;
using Cyaim.RTSPClient.Session;

var config = new RTSPSessionConfig
{
    Url = "rtsp://192.168.1.127:554",
    Username = "admin",
    Password = "admin",
    UseBackchannel = true
};

await using var session = new RTSPSession(config);
await session.StartAsync(); // UseBackchannel = true: backchannel track is set up automatically

byte[] audio = File.ReadAllBytes("audio.g711a");
await session.SendAudioAsync(audio, 8000, RTPPayloadType.PCMA);
```

### Example 4: UDP Transport

```csharp
var config = new RTSPSessionConfig
{
    Url = "rtsp://192.168.1.127:554",
    TransportMode = TransportMode.UdpUnicast // UdpMulticast is not supported yet
};

await using var session = new RTSPSession(config);
await session.StartAsync(); // TransportMode.UdpUnicast: SETUP automatically uses UDP
```
