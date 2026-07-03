# Reliability / 可靠性指南

[中文](#中文) | [English](#english)

---

## 中文

本库面向服务端长期运行的拉流/DVR 场景设计（7×24、网络抖动、设备重启）。以下机制默认开启，也可精细控制。

### 自动 Keep-Alive

会话在 PLAY 成功后自动按 `Session: timeout=` 的一半间隔（最少 5 秒，服务器未声明时 30 秒）发送 GET_PARAMETER 保活；服务器不支持 GET_PARAMETER 时自动退回 OPTIONS。连续两次保活失败视为连接已死，主动关闭连接（配合自动重连即自动恢复）。

```csharp
session.AutoKeepAlive = true;   // 默认开启
session.KeepAlive += (s, e) => Console.WriteLine($"心跳 {(e.Success ? "OK" : "失败")} {e.RoundTripMs}ms");
```

### 自动重连

接收循环意外断开（网络抖动、相机重启、服务器踢线）后自动重连，并**完整恢复媒体会话**：重新握手（含认证）→ 按原参数重放全部 SETUP → PLAY。指数退避（初始 `ReconnectDelayMs`，上限 30 秒）。

```csharp
session.AutoReconnect = true;          // 通过 RTSPSessionConfig.AutoReconnect 默认开启
session.MaxReconnectAttempts = 0;      // 0 = 无限重试（长期值守推荐）
session.ReconnectDelayMs = 2000;

session.Reconnecting += (s, n) => log.Warn($"重连第 {n} 次");
session.Reconnected  += (s, e) => log.Info("媒体流已恢复");
```

主动调用 `DisconnectAsync()`/`DisposeAsync()` 不会触发重连。

### RTCP

- **自动发送 Receiver Report**（每 5 秒/轨，含丢包率、抖动、LSR/DLSR）——部分相机与流媒体服务器收不到 RTCP 会主动断开会话
- **解析 Sender Report** 并通过 `SenderReportReceived` 事件透出 RTP↔NTP 墙钟映射，DVR 录制据此定位真实时间、跨轨对时

```csharp
session.EnableRtcp = true;  // 默认开启
```

### 背压与消费者隔离

`DataReceived` 在独立泵线程触发，与网络接收解耦：

- 消费者处理慢时**丢弃最旧的包**（有界队列，容量 `ReceiveQueueCapacity`，默认 4096），TCP 窗口不会被填满，其他轨道不受影响
- 消费者事件处理器抛异常**不会**杀死接收循环
- 丢包量可通过 `session.PacketsDropped` 观测，持续增长说明消费侧需要扩容

### 错误模型

| 场景 | 表现 |
|------|------|
| 认证失败 | `RTSPAuthenticationException`（`StartAsync`/`LoginDigestAsync`）或响应 `StatusCode == "401"` |
| 信令失败（404 等） | `RTSPProtocolException`（`StartAsync`）或检查响应 `StatusCode` |
| 响应超时 | `RTSPTimeoutException` |
| 连接断开 | 在途请求立即以 `RTSPConnectionException` 失败（不再干等超时）+ `Error` 事件 + `StateChanged → Disconnected` |
| 连接超时 | `RTSPTimeoutException`（`ConnectTimeoutMs`，默认 10 秒，相机失联不再卡 OS 级 20 秒） |

### 释放语义

- `await session.DisposeAsync()` — 优雅关闭：发送 TEARDOWN（限时 2 秒）后关闭，**推荐**
- `session.Dispose()` — 立即关闭：不发 TEARDOWN、不做任何网络等待，永不阻塞、永不死锁

### 帧完整性（媒体层）

- FU-A/FU 分片重组按 RTP **序列号连续性**检测丢包，缺口时丢弃整个 NAL——不会把损坏的帧交给解码器
- 重组缓冲从 ArrayPool 租用并按需增长，4K/8K 大关键帧不会被截断丢弃
- SDP `sprop-parameter-sets`（H.264）/`sprop-vps/sps/pps`（H.265）自动注入，只在 SDP 携带参数集的相机开箱即用

---

## English

Designed for 24×7 server-side stream pulling / DVR workloads. All mechanisms below are on by default and individually controllable.

### Auto keep-alive

After PLAY, the session sends GET_PARAMETER at half the server's `Session: timeout=` (min 5 s; 30 s when unspecified), falling back to OPTIONS when GET_PARAMETER is unsupported. Two consecutive failures close the connection (which auto-reconnect then recovers).

### Auto-reconnect

On unexpected disconnect the session reconnects with exponential backoff and **fully restores media**: handshake (incl. auth) → replay all SETUPs → PLAY. `MaxReconnectAttempts = 0` retries forever. User-initiated `DisconnectAsync`/`DisposeAsync` never triggers reconnect.

### RTCP

Receiver Reports are sent every 5 s per track (loss/jitter/LSR/DLSR) — many cameras tear down sessions without client RTCP. Sender Reports are parsed and surfaced via `SenderReportReceived` (RTP↔NTP mapping for DVR wall-clock timing).

### Backpressure & consumer isolation

`DataReceived` runs on a dedicated pump thread behind a bounded queue (`ReceiveQueueCapacity`, default 4096, drop-oldest). Slow consumers can't stall the TCP window; throwing handlers can't kill the receive loop. Monitor `PacketsDropped`.

### Error model

Typed exceptions: `RTSPAuthenticationException` / `RTSPProtocolException` / `RTSPTimeoutException` / `RTSPConnectionException` (pending requests fail fast on disconnect). `ConnectTimeoutMs` (default 10 s) bounds TCP connects.

### Dispose semantics

`DisposeAsync()` = graceful (TEARDOWN, 2 s cap) — recommended. `Dispose()` = immediate close, no network I/O, never blocks or deadlocks.

### Frame integrity

FU reassembly validates RTP sequence continuity (gaps drop the whole NAL instead of emitting corrupt data); pooled buffers grow on demand (no 64 KB keyframe truncation); SPS/PPS/VPS from SDP are injected automatically.
