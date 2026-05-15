# Events / 事件

[中文](#中文) | [English](#english)

---

## 中文

### 事件列表

| 事件 | 说明 |
|------|------|
| `StateChanged` | 连接状态变更 |
| `DataReceived` | RTP 数据接收 |
| `Error` | 错误发生 |
| `KeepAlive` | 心跳结果 |

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
| `DataReceived` | RTP data received |
| `Error` | Error occurred |
| `KeepAlive` | Keep-alive result |

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
