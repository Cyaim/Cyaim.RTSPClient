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

using var session = new RTSPSession(config);
```

#### 2. 连接服务器

```csharp
await session.ConnectAsync();
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
var reader = session.GetRTPReader(trackId: 0);
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
using var session = new RTSPSession(config);

// 事件监听
session.StateChanged += (s, e) => 
    Console.WriteLine($"状态变更: {e.OldState} -> {e.NewState}");

session.DataReceived += (s, e) => 
    Console.WriteLine($"收到数据: {e.Packet.PayloadType}");

session.Error += (s, e) => 
    Console.WriteLine($"错误: {e.Message}");

// 连接
await session.ConnectAsync();

// 设置视频通道
await session.SetupAsync("trackID=1", TransportMode.TcpInterleaved);

// 开始播放
await session.PlayAsync();

// 读取 RTP 数据
var reader = session.GetRTPReader(trackId: 0);
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

using var session = new RTSPSession(config);
```

#### 2. Connect to Server

```csharp
await session.ConnectAsync();
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
var reader = session.GetRTPReader(trackId: 0);
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
using var session = new RTSPSession(config);

// Event handlers
session.StateChanged += (s, e) => 
    Console.WriteLine($"State changed: {e.OldState} -> {e.NewState}");

session.DataReceived += (s, e) => 
    Console.WriteLine($"Data received: {e.Packet.PayloadType}");

session.Error += (s, e) => 
    Console.WriteLine($"Error: {e.Message}");

// Connect
await session.ConnectAsync();

// Setup video channel
await session.SetupAsync("trackID=1", TransportMode.TcpInterleaved);

// Start playing
await session.PlayAsync();

// Read RTP data
var reader = session.GetRTPReader(trackId: 0);
while (await reader.WaitToReadAsync())
{
    while (reader.TryRead(out var packet))
    {
        Console.WriteLine($"Seq: {packet.SequenceNumber}, TS: {packet.Timestamp}");
    }
}
```
