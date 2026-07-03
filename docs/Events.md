# Events / 事件

[中文](#中文) | [English](#english)

---

## 中文

### 事件列表

| 事件 | 说明 |
|------|------|
| `StateChanged` | 连接状态变更 |
| `DataReceived` | RTP 数据接收（独立泵线程触发，处理慢不会阻塞网络接收） |
| `Error` | 错误发生 |
| `KeepAlive` | 心跳结果（`AutoKeepAlive` 默认开启，自动按会话超时调度） |
| `Reconnecting` | 自动重连开始（参数为尝试次数，需 `AutoReconnect = true`） |
| `Reconnected` | 自动重连成功，媒体流已恢复 |
| `SenderReportReceived` | 收到 RTCP SR，提供 RTP 时间戳 ↔ NTP 墙钟映射（DVR 对时用） |

### StateChanged - 状态变更

```csharp
session.StateChanged += (sender, e) =>
{
    Console.WriteLine($"[{e.Timestamp}] {e.OldState} -> {e.NewState}");
    if (!string.IsNullOrEmpty(e.Reason))
        Console.WriteLine($"原因: {e.Reason}");
};
```

### DataReceived - 数据接收

```csharp
session.DataReceived += (sender, e) =>
{
    var packet = e.Packet;
    Console.WriteLine($"Track: {packet.TrackId}");
    Console.WriteLine($"序列号: {packet.SequenceNumber}");
    Console.WriteLine($"时间戳: {packet.Timestamp}");
    Console.WriteLine($"载荷类型: {packet.PayloadType}");
    Console.WriteLine($"数据大小: {packet.Payload.Length} bytes");
};
```

### Error - 错误

```csharp
session.Error += (sender, e) =>
{
    Console.WriteLine($"错误: {e.Message}");
    if (e.Exception != null)
    {
        Console.WriteLine($"异常: {e.Exception.GetType().Name}");
        Console.WriteLine($"堆栈: {e.Exception.StackTrace}");
    }
};
```

### KeepAlive - 心跳

```csharp
session.KeepAlive += (sender, e) =>
{
    Console.WriteLine($"成功: {e.Success}");
    Console.WriteLine($"往返时间: {e.RoundTripMs}ms");
};
```

### Reconnecting / Reconnected - 自动重连

`AutoReconnect = true` 时，接收循环意外断开后自动重连并重放 SETUP/PLAY 恢复媒体流：

```csharp
session.Reconnecting += (sender, attempt) =>
    Console.WriteLine($"第 {attempt} 次重连中...");

session.Reconnected += (sender, e) =>
    Console.WriteLine("重连成功，媒体流已恢复");
```

### SenderReportReceived - RTCP 墙钟映射

DVR 录制时可据此把 RTP 时间戳映射到真实时间，并跨音视频轨对时：

```csharp
session.SenderReportReceived += (sender, e) =>
{
    Console.WriteLine($"Track {e.TrackId}: RTP {e.RtpTimestamp} = {e.NtpTimeUtc:O}");
};
```

### 背压与丢包观测

`DataReceived` 在独立泵线程触发，消费过慢时丢弃最旧的包而不是阻塞 TCP：

```csharp
Console.WriteLine($"因消费过慢丢弃的包数: {session.PacketsDropped}");
// 队列容量可调（默认 4096）
session.ReceiveQueueCapacity = 8192;
```

### 连接状态

```csharp
public enum RTSPConnectionState
{
    Disconnected,   // 未连接
    Connecting,     // 正在连接
    Connected,      // 已连接
    Ready,          // 就绪
    Setup,          // 已设置
    Playing,        // 播放中
    Paused,         // 已暂停
    Disconnecting   // 断开中
}
```

---

## English

### Event List

| Event | Description |
|-------|-------------|
| `StateChanged` | Connection state changed |
| `DataReceived` | RTP data received (raised on a dedicated pump thread; slow handlers never stall the socket) |
| `Error` | Error occurred |
| `KeepAlive` | Keep-alive result (`AutoKeepAlive` is on by default) |
| `Reconnecting` | Auto-reconnect attempt started (int attempt count; requires `AutoReconnect = true`) |
| `Reconnected` | Auto-reconnect succeeded, media restored |
| `SenderReportReceived` | RTCP SR received — RTP timestamp ↔ NTP wall-clock mapping for DVR timing |

### StateChanged

```csharp
session.StateChanged += (sender, e) =>
{
    Console.WriteLine($"[{e.Timestamp}] {e.OldState} -> {e.NewState}");
    if (!string.IsNullOrEmpty(e.Reason))
        Console.WriteLine($"Reason: {e.Reason}");
};
```

### DataReceived

```csharp
session.DataReceived += (sender, e) =>
{
    var packet = e.Packet;
    Console.WriteLine($"Track: {packet.TrackId}");
    Console.WriteLine($"Sequence: {packet.SequenceNumber}");
    Console.WriteLine($"Timestamp: {packet.Timestamp}");
    Console.WriteLine($"Payload Type: {packet.PayloadType}");
    Console.WriteLine($"Size: {packet.Payload.Length} bytes");
};
```

### Error

```csharp
session.Error += (sender, e) =>
{
    Console.WriteLine($"Error: {e.Message}");
    if (e.Exception != null)
    {
        Console.WriteLine($"Exception: {e.Exception.GetType().Name}");
        Console.WriteLine($"Stack: {e.Exception.StackTrace}");
    }
};
```

### KeepAlive

```csharp
session.KeepAlive += (sender, e) =>
{
    Console.WriteLine($"Success: {e.Success}");
    Console.WriteLine($"Round-trip: {e.RoundTripMs}ms");
};
```

### Connection States

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
