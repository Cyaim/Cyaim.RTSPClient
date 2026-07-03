# Changelog

## 3.0.0 (2026-07-03)

面向 1000+ 客户服务端集成场景的可靠性/性能大修。升级指南见 [docs/Migration-v3.md](docs/Migration-v3.md)。

### 可靠性（客户端）

- **修复**：响应缓存无限增长导致的内存泄漏（长期运行 OOM）
- **修复**：`NetworkStream` 并发写缺锁（背传音频 + 心跳并发写坏 TCP 流）
- **修复**：`GET_PARAMETER`/`SET_PARAMETER`/`ANNOUNCE` 声明了 Content-Length 却不发送内容体（服务器等 body 卡死）；Content-Length 按 UTF-8 字节数计算
- **修复**：单条畸形响应（非数字 CSeq 等）不再终止整个会话
- **修复**：断连时在途请求立即失败（不再干等 10 秒超时）
- **修复**：`ConnectAsync` 支持超时与取消（`ConnectTimeoutMs`，默认 10 秒）
- **修复**：`Dispose` 不再同步阻塞最长 15 秒/死锁；新增 `IAsyncDisposable`（`DisposeAsync` 优雅关闭）
- **新增**：自动 keep-alive（按 `Session timeout/2` 调度 GET_PARAMETER，退回 OPTIONS，`AutoKeepAlive` 默认开启）
- **新增**：自动重连（重放 SETUP/PLAY 完整恢复媒体流，指数退避，`Reconnecting`/`Reconnected` 事件）
- **新增**：`DataReceived` 独立泵线程 + 有界队列（慢消费者不阻塞收流、异常隔离、`PacketsDropped` 观测）

### 协议兼容（客户端）

- **新增**：Digest qop=auth（cnonce/nc）、SHA-256（RFC 7616）、opaque、algorithm；Basic 回退；401 全局自动重试——现代海康/大华/Axis 固件不再永远 401
- **新增**：`Content-Base`/`Content-Location` 控制 URI 解析；`a=control:*`；SETUP URI 拼接修正
- **新增**：静态载荷类型（PCMU/PCMA/G722 等）无 rtpmap 时自动合成编码信息
- **修复**：响应头大小写不敏感查找；SDP 畸形 base64 参数集容错

### 媒体路径（客户端）

- **修复**：FU-A/FU 重组缓冲池化并按需增长——高分辨率大关键帧不再被 64KB 上限静默丢弃（长时间绿屏/花屏根因）
- **修复**：FU 重组按 RTP 序列号检测缺口，丢包时丢弃整个 NAL 而非上抛损坏数据
- **新增**：SDP 参数集自动注入（H.264 sprop-parameter-sets / H.265 sprop-vps/sps/pps）
- **新增**：RTCP 接线——自动发送 Receiver Report（防被服务器踢线）；解析 Sender Report 并经 `SenderReportReceived` 提供 RTP↔NTP 墙钟映射（DVR 对时）
- **修复**：AAC 多 AU 步长（indexDeltaLength）、跨包 AU 分片重组、交织流 AU 序号/时间戳
- **新增**：`MediaFrame.IsAccessUnitEnd`（RTP marker 透出，录制/封装可直接判帧边界）
- **修复**：背传 G.711 RTP 时间戳改为标准采样计数（原为 Unix 秒，严格接收端丢弃）
- **新增**：H.265 DONL/DOND（sprop-max-don-diff > 0）

### 性能（客户端）

- **零拷贝**：`RTPPacket.PayloadSegment` 切片原始包（每包 3 分配 2 拷贝 → 1 分配），全部解包器已切换
- **SIMD**：G.711 编码 AVX2 向量化（16 样本/迭代，实测 ~14×），解码 256 项查表；头部扫描向量化 `Span.IndexOf`
- **修正**：G.711 A-law 编码 [256,511] 区间段位错误（对齐 ITU-T 标准）

### 易用性（客户端）

- **新增**：`RTSPSession(RTSPSessionConfig)` 配置化构造 + `StartAsync()` 一键拉流 + `GetRtpReader`/`GetMediaFrameReader`（解包管线开箱即用）
- **新增**：真正的 UDP 单播拉流（`TransportMode.UdpUnicast`/`SetupUdpAsync`，端口对协商、NAT 打洞、乱序重排缓冲）
- **变更**：`RTSPSession` 实现 `IRTSPSession`；类型化异常（`RTSPAuthenticationException` 等）
- **清理**：删除 internal 死代码（`KeepAliveManager`/`RTSPStateMachine`）；`[Obsolete]` 标记从未接线的传输/认证类型
- **文档**：README 与 docs 全部示例重写为可编译；新增 Reliability/Performance/Migration-v3 指南

### 服务端（Cyaim.RTSPServer）

- **修复**：音频分发架构（按订阅者广播，多客户端/多轨不再互相抢包）——"拉流只有视频没有声音"根因
- **修复**：MP4 mp4a/esds 规范解析，SDP `config=` 使用文件真实 AudioSpecificConfig；真实帧率（mdhd/stts）
- **修复**：AAC RTP 补 RFC 3640 AU-header；SETUP 解析 trackID；测试图案 H.264 合规化（I_PCM）
- **修复**：对未启动的流 PLAY 返回 455（此前返回 200 但永远无媒体）
- **优化**：会话级单发送任务、ArrayPool 发送路径、字节级请求解析

## 2.0.0

历史版本（初始 RTSP 客户端/服务端实现）。
