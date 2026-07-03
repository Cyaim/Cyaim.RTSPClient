# Migration Guide v2 → v3 / 升级指南

[中文](#中文) | [English](#english)

---

## 中文

v3.0 是一次可靠性/性能大修。绝大多数 v2 代码无需修改即可编译运行，且自动获得修复；以下是需要注意的行为与 API 变化。

### 无需改代码、自动生效的修复

- 内存泄漏修复（响应缓存无限增长）、发送并发锁、断连快速失败
- Keep-alive 自动发送（此前需要自己起定时器调 `SendKeepAliveAsync`；如果你有自己的保活循环，可保留或设 `AutoKeepAlive = false`）
- RTCP RR 自动发送（部分相机不再莫名断流）
- 401 自动重试，新增 qop=auth / SHA-256 / opaque / Basic 支持（旧版对现代海康/大华/Axis 固件会永远 401）
- FU 分片：大关键帧不再被静默丢弃（旧版 64KB 上限 → 高分辨率长时间绿屏）；丢包时不再上抛损坏 NAL
- `GET_PARAMETER`/`SET_PARAMETER`/`ANNOUNCE` 的内容体现在真正发送（旧版只发 Content-Length 不发 body）

### 行为变化（Behavioral changes）

| 项 | v2 | v3 |
|----|----|----|
| `Dispose()` | 同步发 TEARDOWN 并等待（最长 ~15 秒，可能死锁） | 立即关闭，不发 TEARDOWN；优雅关闭请用 `await DisposeAsync()` |
| `DataReceived` 线程 | 网络接收线程同步触发（慢处理阻塞收流，异常杀死会话） | 独立泵线程 + 有界队列（异常被隔离；慢处理丢最旧包，见 `PacketsDropped`） |
| 响应超时异常 | `TimeoutException` | `RTSPTimeoutException`（派生自 `RTSPException`） |
| `LoginDigestAsync` 失败 | 返回 401 响应或抛裸 `Exception` | 抛 `RTSPAuthenticationException` / `RTSPProtocolException` |
| 畸形响应 | 终止整个会话 | 丢弃该条消息，会话继续 |
| 背传音频 RTP 时间戳 | Unix 秒（严格接收端丢弃） | 标准采样计数 |

### API 变化（Breaking changes）

- `RTSPRequest.Content`：`List<string>?` → `string?`（旧属性从未被发送过，实际无人依赖）
- `RTPPacket`：新增 `PayloadSegment`；`Payload` 变为计算属性（零拷贝解析时访问会复制，热路径请改用 `PayloadSegment`）
- `MediaFrame` 构造函数新增可选参数 `isAccessUnitEnd`（源码兼容；二进制引用需重编译）
- `IRTSPSession` 接口与实现对齐（`Uri?`/`SessionId?` 可空标注、事件可空），`RTSPSession` 现已实现该接口
- 已删除（internal 死代码）：`KeepAliveManager`、`RTSPStateMachine`
- 已标记 `[Obsolete]`（从未被会话使用，将在后续版本移除）：`IRTSPTransport`、`RTSPTcpTransport`、`RTSPUdpTransport`、`IRTSPAuthenticator`、`DigestAuthenticator`
- G.711 软件编码器修正了 [256,511] 振幅区间的段位错误（输出与 ITU-T 标准一致，与 v2 输出在该区间不同——v2 是错的）

### 推荐迁移到新 API

```csharp
// v2 手动六步 → v3 一键
var config = new RTSPSessionConfig { Url = "...", Username = "...", Password = "...", AutoReconnect = true };
await using var session = new RTSPSession(config);
await session.StartAsync();
var frames = session.GetMediaFrameReader(0);   // 解包后的完整 NAL，自动注入 SPS/PPS
```

UDP 拉流现在真正可用：`TransportMode.UdpUnicast`（含乱序重排）。

---

## English

v3.0 is a reliability/performance overhaul. Most v2 code compiles unchanged and picks up the fixes automatically.

**Behavioral changes**: `Dispose()` no longer sends TEARDOWN nor blocks (use `DisposeAsync()` for graceful shutdown); `DataReceived` fires on a dedicated pump thread behind a bounded drop-oldest queue; response timeout now throws `RTSPTimeoutException`; malformed responses no longer kill the session; keep-alive and RTCP RR are automatic.

**Breaking**: `RTSPRequest.Content` is now `string?`; `RTPPacket.Payload` is a computed property (prefer `PayloadSegment` on hot paths); `MediaFrame` ctor gained an optional parameter; `IRTSPSession` nullability aligned (and `RTSPSession` now implements it); `KeepAliveManager`/`RTSPStateMachine` removed; transports/`DigestAuthenticator` marked `[Obsolete]`; the G.711 encoder's segment bug for amplitudes [256,511] is fixed to match ITU-T.

**Recommended**: switch to `new RTSPSession(RTSPSessionConfig)` + `StartAsync()` + `GetMediaFrameReader(trackId)`. UDP unicast now actually works (`TransportMode.UdpUnicast`, with reordering).
