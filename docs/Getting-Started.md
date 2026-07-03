# Getting Started / 快速开始

[中文](#中文) | [English](#english)

---

## 中文

### 安装

#### 通过 .NET CLI

```bash
dotnet add package Cyaim.RTSPClient
```

#### 通过 NuGet 包管理器

```
Install-Package Cyaim.RTSPClient
```

#### 通过 PackageReference

```xml
<PackageReference Include="Cyaim.RTSPClient" Version="2.0.0" />
```

### 基本使用

> 提示：推荐直接调用 `session.StartAsync()` 一键完成 连接 → OPTIONS → DESCRIBE（自动认证）→ SETUP 全部媒体轨 → PLAY（见下方完整示例）。以下 2~4 步展示等价的低层 API。

#### 1. 创建会话

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
```

#### 2. 连接服务器

```csharp
await session.ConnectAsync();
await session.OptionsAsync();
await session.DescribeAsync(); // 遇到 401 自动完成 Digest/Basic 认证并重试
```

#### 3. 设置媒体通道

```csharp
// 设置视频通道
await session.SetupAsync("trackID=1", TransportMode.TcpInterleaved);

// 设置音频通道
await session.SetupAsync("trackID=2", TransportMode.TcpInterleaved);
```

#### 4. 开始播放

```csharp
await session.PlayAsync();
```

#### 5. 读取数据

```csharp
// trackId 与 SETUP 控制串中的 trackID 对应
var reader = session.GetRtpReader(trackId: 1);
while (await reader.WaitToReadAsync())
{
    while (reader.TryRead(out var packet))
    {
        // 处理 RTP 包
        Console.WriteLine($"Seq: {packet.SequenceNumber}");
    }
}
```

### 完整示例

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
await using var session = new RTSPSession(config);

// 事件监听
session.StateChanged += (s, e) => 
    Console.WriteLine($"状态变更: {e.OldState} -> {e.NewState}");

session.DataReceived += (s, e) => 
    Console.WriteLine($"收到数据: {e.Packet.PayloadType}");

session.Error += (s, e) => 
    Console.WriteLine($"错误: {e.Message}");

// 一键拉流：连接 → OPTIONS → DESCRIBE（自动认证）→ SETUP 全部媒体轨 → PLAY
await session.StartAsync();

// 读取 RTP 数据（trackId 与 SDP 控制串中的 trackID 对应）
var reader = session.GetRtpReader(trackId: 0);
while (await reader.WaitToReadAsync())
{
    while (reader.TryRead(out var packet))
    {
        Console.WriteLine($"Seq: {packet.SequenceNumber}, TS: {packet.Timestamp}");
    }
}
```

---

## English

### Installation

#### Via .NET CLI

```bash
dotnet add package Cyaim.RTSPClient
```

#### Via NuGet Package Manager

```
Install-Package Cyaim.RTSPClient
```

#### Via PackageReference

```xml
<PackageReference Include="Cyaim.RTSPClient" Version="2.0.0" />
```

### Basic Usage

> Tip: the recommended path is a single call to `session.StartAsync()`, which performs Connect → OPTIONS → DESCRIBE (auto authentication) → SETUP all tracks → PLAY (see the complete example below). Steps 2-4 show the equivalent low-level API.

#### 1. Create Session

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
```

#### 2. Connect to Server

```csharp
await session.ConnectAsync();
await session.OptionsAsync();
await session.DescribeAsync(); // 401 responses trigger automatic Digest/Basic authentication retry
```

#### 3. Setup Media Channels

```csharp
// Setup video channel
await session.SetupAsync("trackID=1", TransportMode.TcpInterleaved);

// Setup audio channel
await session.SetupAsync("trackID=2", TransportMode.TcpInterleaved);
```

#### 4. Start Playing

```csharp
await session.PlayAsync();
```

#### 5. Read Data

```csharp
// trackId matches the trackID in the SETUP control URI
var reader = session.GetRtpReader(trackId: 1);
while (await reader.WaitToReadAsync())
{
    while (reader.TryRead(out var packet))
    {
        // Process RTP packet
        Console.WriteLine($"Seq: {packet.SequenceNumber}");
    }
}
```

### Complete Example

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
await using var session = new RTSPSession(config);

// Event handlers
session.StateChanged += (s, e) => 
    Console.WriteLine($"State changed: {e.OldState} -> {e.NewState}");

session.DataReceived += (s, e) => 
    Console.WriteLine($"Data received: {e.Packet.PayloadType}");

session.Error += (s, e) => 
    Console.WriteLine($"Error: {e.Message}");

// One-call streaming: Connect → OPTIONS → DESCRIBE (auto auth) → SETUP all tracks → PLAY
await session.StartAsync();

// Read RTP data (trackId matches the trackID in the SDP control URI)
var reader = session.GetRtpReader(trackId: 0);
while (await reader.WaitToReadAsync())
{
    while (reader.TryRead(out var packet))
    {
        Console.WriteLine($"Seq: {packet.SequenceNumber}, TS: {packet.Timestamp}");
    }
}
```
