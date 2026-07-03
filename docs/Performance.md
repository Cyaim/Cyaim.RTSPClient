# Performance / 性能指南

[中文](#中文) | [English](#english)

---

## 中文

面向单机上千路并发拉流的热路径设计。

### 零拷贝接收路径

每个 RTP 包从 socket 到 `DataReceived` 只有 **1 次数组分配**（旧版为 3 次分配 + 2 次全量拷贝）：

- `RTPPacket.PayloadSegment`（`ArraySegment<byte>`）零拷贝切片原始包数据——**热路径请用它**
- `RTPPacket.Payload`（`byte[]`）为兼容属性，访问时才实体化一份拷贝
- 库内全部解包器（H.264/H.265/AAC/G.711）都走 `PayloadSegment`

```csharp
session.DataReceived += (s, e) =>
{
    var seg = e.Packet.PayloadSegment;          // 零拷贝
    Process(seg.Array!, seg.Offset, seg.Count);
    // 避免在热路径访问 e.Packet.Payload（每次分配拷贝）
};
```

### SIMD 加速

- **G.711 编码**（`G711Fast.EncodeALaw/EncodeMuLaw`）：x86 AVX2 下向量化，每次处理 16 个样本，实测 **约 14× 于标量**（9.6M 样本：6.5ms vs 91ms）；非 x86/旧运行时自动回退无分支查表标量
- **G.711 解码**：256 项查表，单次内存读取每样本
- RTSP 头部扫描使用运行时内部向量化的 `Span.IndexOf`

```csharp
using Cyaim.RTSPClient.Common;

G711Fast.EncodeALaw(pcmSamples, alawOutput);   // 自动选择 SIMD/标量
G711Fast.DecodeALaw(alawBytes, pcmOutput);
```

> 注：视频解码不在本库内（由 FFmpeg 插件包提供，可用 D3D11VA/NVDEC 等硬件加速），GPU 对协议层每包微秒级处理无收益。

### 分片重组缓冲

FU-A/FU 重组缓冲从 `ArrayPool` 租用、按需增长——大关键帧不再触发每帧 64KB 的固定分配。

### 调优参数

| 参数 | 默认 | 说明 |
|------|------|------|
| `ReceiveQueueCapacity` | 4096 | RTP 分发队列容量；高码率多轨可增大，配合 `PacketsDropped` 观测 |
| `RTSPSessionConfig.RtpChannelBufferSize` | 1024 | `GetRtpReader` 每轨道通道容量 |
| `WaitResponseTimeout` | 10000ms | 信令响应超时 |
| `ConnectTimeoutMs` | 10000ms | TCP 连接超时 |

### 千路并发建议

- 每路一个 `RTSPSession`；会话内部零共享静态热点，可安全并行
- 消费 `GetMediaFrameReader` / `GetRtpReader`（`ChannelReader`）而不是事件，天然异步背压
- 监控 `PacketsDropped` 与 `RtpReorderBuffer.PacketsLost`（UDP）作为消费能力指标
- 服务端 GC 建议 `<ServerGarbageCollection>true</ServerGarbageCollection>`

---

## English

### Zero-copy receive path

One array allocation per RTP packet from socket to `DataReceived` (was 3 allocations + 2 copies). Use `RTPPacket.PayloadSegment` (`ArraySegment<byte>`, zero-copy slice) on hot paths; `Payload` is a compatibility property that materializes a copy on access. All built-in depacketizers use the segment.

### SIMD

- **G.711 encode** (`G711Fast`): AVX2-vectorized, 16 samples/iteration, measured **~14× over scalar** (9.6M samples: 6.5 ms vs 91 ms); branch-free table-based scalar fallback elsewhere
- **G.711 decode**: 256-entry lookup table
- RTSP header scanning uses the runtime's vectorized `Span.IndexOf`

Video decoding lives in the FFmpeg plugin packages (hardware acceleration there); GPU offload has no benefit for microsecond-scale per-packet protocol work.

### Tuning

`ReceiveQueueCapacity` (4096), `RTSPSessionConfig.RtpChannelBufferSize` (1024), `WaitResponseTimeout` (10 s), `ConnectTimeoutMs` (10 s). For 1000+ concurrent streams: one `RTSPSession` per stream, consume via `ChannelReader`s, watch `PacketsDropped`, enable server GC.
