# Verification Plan / 验证计划与覆盖矩阵

[中文](#中文) | [English](#english)

---

## 中文

本页记录库的验证覆盖现状：哪些能力已被自动化测试或实机验证，哪些**尚未覆盖**、原因与补齐路径。发版前请对照本页评估风险。

### 已覆盖（自动化测试，40 项）

运行方式：`dotnet test Cyaim.RTSPClient.Tests`。FFmpeg 相关测试（`Category=FFmpeg`）需要 FFmpeg 7.x 共享库（`FFMPEG_PATH`），环境缺失时自动跳过——**发版前必须在有 FFmpeg 的机器上跑通全量**。

| 领域 | 覆盖内容 |
|------|----------|
| 协议 | Digest RFC 2617/2069 标准向量；请求体/UTF-8 长度/CRLF 防注入；畸形 CSeq 容错 |
| RTP | FU-A 大帧重组（200KB）/丢包缺口丢弃/参数集注入；AAC 多 AU/时间戳；乱序重排与序列号回绕 |
| G.711 | SIMD 与标量全量等价（65536 输入）；ITU 已知值；编解码回环容差；服务端/客户端一致性 |
| 性能守护 | RTP 解析每包分配 <32B（零拷贝回归即红）；BenchmarkDotNet 基准（手动） |
| 服务端 | 广播器多订阅者不抢包；H264TestStream 合规性 + 服务端打包×客户端解包交叉验证 |
| 集成 | 进程内服务器：TCP 一键拉流、UDP 单播、服务器重启自动重连恢复媒体 |
| FFmpeg 视频 | H.264 软件（libopenh264）编解码 roundtrip + 亮度校验；QSV 硬件解码（NV12 下载）；H.265/VP8/VP9 roundtrip；B 帧多帧重排+时间戳单调；视频 ExtraData；RtspVideoDecoderBridge（25 AU→25 帧）；服务器测试流真实解码 |
| FFmpeg 音频 | AAC 编解码 roundtrip（FLTP 协商/FIFO 分帧/ASC extradata/RMS 信号校验）；工厂能力动态探测 |

已实机验证但依赖本机硬件（CI 上自动跳过）：QSV 硬件编解码、1080p/4K 编解码（在 FFmpegProbe 实验中验证）。

### 未覆盖项与补齐计划

| # | 未覆盖项 | 原因 | 补齐路径 | 优先级 |
|---|----------|------|----------|--------|
| 1 | NVDEC/NVENC（CUDA）硬件编解码 | 开发机无 NVIDIA GPU | 在带 NVIDIA GPU 的机器设 `FFMPEG_PATH` 跑 `Category=FFmpeg` 测试；代码路径与 QSV 同构（系统内存帧模式），风险中低 | 高（NVIDIA 是客户环境主流） |
| 2 | VAAPI（Linux）/ VideoToolbox（macOS）/ AMF（AMD） | 无对应操作系统/GPU 环境 | 对应平台跑 FFmpeg 测试；VAAPI 建议在带 iGPU 的 Linux CI runner 上补自动化 | 中 |
| 3 | H.265 带外参数集（sprop-vps/sps/pps → ExtraData）实流验证 | 无真实 HEVC 相机码流；H264TestStream 无 HEVC 等价物 | 用 `ffmpeg -c:v hevc_qsv`（或真实相机）生成 HEVC Annex-B 样本入库做解码回归；或对接真实 H.265 相机验证 SDP 参数集注入链路 | 中 |
| 4 | RTSP→FFmpeg 端到端集成（真实拉流 + Bridge 解码渲染） | 集成测试止步于 MediaFrame 序列模拟 | 在集成测试中拼接：进程内服务器 → RTSPSession.StartAsync → GetMediaFrameReader → RtspVideoDecoderBridge → 断言解出像素帧 | 高（打通全链路的最后验证） |
| 5 | UDP 丢包/乱序场景下的重排缓冲实测 | 本机回环不丢包 | 用 clumsy/tc netem 注入丢包乱序，观察 RtpReorderBuffer.PacketsLost 与画面连续性；或在测试中直接对 UDP socket 层做故障注入封装 | 中 |
| 6 | 多路并发压测（100+ 路）与长稳（24h+） | 单机资源与时间成本 | 压测程序：N 路并发 StartAsync 拉流统计 PacketsDropped/内存曲线；长稳跑通宵观察句柄/内存 | 高（1000+ 客户场景的核心风险） |
| 7 | Opus/AMR/Speex/Vorbis/MP3 编解码 roundtrip | 仅 AAC 做了全链路（其余走同一基类路径） | 参数化现有 AAC roundtrip 测试扩展到 Opus/MP3（FFmpeg 内建必有）；AMR/Speex 依赖构建，跳过逻辑已就绪 | 低 |
| 8 | ONVIF 回传通道（backchannel）实机 | 无支持回传的设备 | 对接支持 ONVIF Profile T 的设备验证 SendAudioAsync/G.711 背传 | 低 |
| 9 | 真实品牌相机互操作矩阵（海康/大华/Axis） | 无实机 | 每接入一个品牌跑一遍标准清单：Digest qop、Content-Base、SETUP 轨道、长连接保活、断电重连 | 高（随客户现场逐步积累） |

### 维护约定

- 新增能力必须附带测试；修 bug 先写复现测试再修
- 上表每补齐一项，移入"已覆盖"并注明验证环境与日期
- 发版检查单：全量测试（含 FFmpeg 机器）→ Benchmarks 无回归 → 本页未覆盖项风险评估

---

## English

This page tracks verification coverage: what is covered by the 40 automated tests (FFmpeg tests require FFmpeg 7.x via `FFMPEG_PATH` and skip when absent — always run on an FFmpeg-equipped machine before release), what remains uncovered, why, and how to close each gap.

Uncovered items (see table above for details): NVDEC/NVENC, VAAPI/VideoToolbox/AMF (no matching hardware/OS); H.265 out-of-band parameter sets with real HEVC streams; full RTSP→Bridge→decode integration test; UDP loss/reorder fault injection; 100+ stream concurrency & 24h soak; Opus/MP3 roundtrips; ONVIF backchannel devices; real-camera interop matrix (Hikvision/Dahua/Axis).

Maintenance: every new capability ships with tests; bugs get a failing test first; move items to "covered" with environment/date when closed; pre-release checklist = full tests on FFmpeg machine + benchmark comparison + risk review of this page.
