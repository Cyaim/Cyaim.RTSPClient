using Cyaim.RTSPClient.Events;
using Cyaim.RTSPClient.Exceptions;
using Cyaim.RTSPClient.Rtcp;
using Cyaim.RTSPClient.Rtp;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Cyaim.RTSPClient
{
    /// <summary>
    /// RTSPSession 可靠性支撑：
    /// - RTP 包分发泵（有界队列 + 消费者异常隔离，慢消费者不再阻塞 TCP 读取）
    /// - 自动 keep-alive（按服务器 Session timeout 调度）
    /// - RTCP：发送 Receiver Report、解析 Sender Report（提供 RTP↔墙钟映射）
    /// - 自动重连（恢复 SETUP/PLAY 与通道映射）
    /// </summary>
    public partial class RTSPSession
    {
        #region RTP 包分发泵

        private Channel<RTPPacket>? _packetChannel;
        private Task? _pumpTask;
        private long _packetsDropped;

        private Channel<RTPPacket> StartPacketPump(CancellationToken ct)
        {
            var channel = Channel.CreateBounded<RTPPacket>(
                new BoundedChannelOptions(Math.Max(64, ReceiveQueueCapacity))
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = true
                },
                _ => Interlocked.Increment(ref _packetsDropped));

            _packetChannel = channel;
            _pumpTask = Task.Run(() => PumpLoopAsync(channel.Reader, ct));
            return channel;
        }

        private void EnqueuePacket(in RTPPacket packet)
        {
            var channel = _packetChannel;
            if (channel == null)
            {
                // 泵未启动（理论上不会发生），退化为同步分发
                InvokeDataReceived(packet);
                return;
            }

            channel.Writer.TryWrite(packet);
        }

        private async Task PumpLoopAsync(ChannelReader<RTPPacket> reader, CancellationToken ct)
        {
            try
            {
                while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var packet))
                    {
                        InvokeDataReceived(packet);
                        RouteToTrackChannel(in packet);  // GetRtpReader/GetMediaFrameReader 订阅的轨道通道
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ChannelClosedException) { }
        }

        private void InvokeDataReceived(in RTPPacket packet)
        {
            try
            {
                DataReceived?.Invoke(this, new RtpDataReceivedEventArgs(packet));
            }
            catch (Exception ex)
            {
                // 消费者异常隔离：不允许业务代码异常杀死接收循环
                LastException = ex;
            }
        }

        private void CompletePacketPump()
        {
            _packetChannel?.Writer.TryComplete();
        }

        #endregion

        #region 自动 Keep-Alive

        private CancellationTokenSource? _keepAliveCts;
        private Task? _keepAliveTask;

        /// <summary>
        /// PLAY 成功后调用：按 Session timeout 的一半（最少 5 秒，未知时 30 秒）自动发送保活
        /// </summary>
        private void StartKeepAlive()
        {
            if (!AutoKeepAlive)
                return;
            if (_keepAliveTask is { IsCompleted: false })
                return;

            _keepAliveCts?.Dispose();
            _keepAliveCts = new CancellationTokenSource();
            var ct = _keepAliveCts.Token;
            _keepAliveTask = Task.Run(() => KeepAliveLoopAsync(ct));
        }

        private void StopKeepAlive()
        {
            try { _keepAliveCts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private async Task KeepAliveLoopAsync(CancellationToken ct)
        {
            int consecutiveFailures = 0;

            // PAUSE 状态下会话依然需要保活，只有断开/停止才退出
            while (!ct.IsCancellationRequested &&
                   (State == RTSPConnectionState.Playing || State == RTSPConnectionState.Paused))
            {
                int intervalSeconds = Timeout > 5 ? Math.Max(5, Timeout / 2) : 30;
                try
                {
                    await Task.Delay(intervalSeconds * 1000, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (ct.IsCancellationRequested ||
                    (State != RTSPConnectionState.Playing && State != RTSPConnectionState.Paused))
                    break;

                bool ok = await SendKeepAliveAsync(ct).ConfigureAwait(false);
                consecutiveFailures = ok ? 0 : consecutiveFailures + 1;

                if (consecutiveFailures >= 2)
                {
                    // 连续两次保活失败视为连接已死：主动关闭让接收循环退出
                    //（若启用 AutoReconnect 会随后自动恢复会话）
                    OnError(new RTSPConnectionException("Keep-alive failed twice, closing connection"));
                    try { _tcpStream?.Close(); } catch { }
                    break;
                }
            }
        }

        #endregion

        #region 自动重连

        /// <summary>
        /// 自动重连开始时触发，参数为当前尝试次数（从 1 开始）
        /// </summary>
        public event EventHandler<int>? Reconnecting;

        /// <summary>
        /// 自动重连成功（媒体已恢复 PLAY）后触发
        /// </summary>
        public event EventHandler? Reconnected;

        private int _reconnectGate;

        private async Task AutoReconnectLoopAsync()
        {
            // 防止并发进入重连
            if (Interlocked.CompareExchange(ref _reconnectGate, 1, 0) != 0)
                return;

            try
            {
                int attempt = 0;
                int delay = Math.Max(100, ReconnectDelayMs);

                while (!_userDisconnect && _disposed == 0 &&
                       (MaxReconnectAttempts <= 0 || attempt < MaxReconnectAttempts))
                {
                    attempt++;
                    Reconnecting?.Invoke(this, attempt);

                    try
                    {
                        await Task.Delay(delay).ConfigureAwait(false);
                    }
                    catch { break; }

                    delay = Math.Min(delay * 2, 30000);

                    if (_userDisconnect || _disposed != 0)
                        break;

                    try
                    {
                        if (await RestoreSessionAsync(CancellationToken.None).ConfigureAwait(false))
                        {
                            Reconnected?.Invoke(this, EventArgs.Empty);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        LastException = ex;
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _reconnectGate, 0);
            }
        }

        /// <summary>
        /// 完整恢复会话：重连 → 握手（自动认证）→ 重放 SETUP → PLAY
        /// </summary>
        private async Task<bool> RestoreSessionAsync(CancellationToken ct)
        {
            CleanupConnection();
            CleanupUdpTransports();
            _channelMap.Clear();
            _rtpTrackers.Clear();
            SessionId = null;

            await ConnectInternalAsync(ct).ConfigureAwait(false);

            var options = await OptionsAsync(ct).ConfigureAwait(false);
            if (options.StatusCode != "200")
                return false;

            var describe = await DescribeAsync(ct: ct).ConfigureAwait(false);
            if (describe.StatusCode != "200")
                return false;

            (string channelUri, string transport)[] setups;
            lock (_setupHistory)
            {
                setups = _setupHistory.ToArray();
            }

            foreach (var (channelUri, transport) in setups)
            {
                // UDP 轨需要重新绑定本地端口对，不能原样重放旧的 client_port
                RTSPResponse setup = transport.IndexOf("interleaved", StringComparison.OrdinalIgnoreCase) >= 0
                    ? await SetupInternalAsync(channelUri, transport, recordHistory: false, useBackchannel: false, ct).ConfigureAwait(false)
                    : await SetupUdpAsync(channelUri, ct).ConfigureAwait(false);
                if (setup.StatusCode != "200")
                    return false;
            }

            var play = await PlayAsync(_playRange, ct: ct).ConfigureAwait(false);
            return play.StatusCode == "200";
        }

        /// <summary>
        /// 静默清理底层连接（不发送 TEARDOWN，不等待）
        /// </summary>
        private void CleanupConnection()
        {
            try { _receiveCts?.Cancel(); } catch { }
            try { _tcpStream?.Dispose(); } catch { }
            try { _client?.Dispose(); } catch { }
            _tcpStream = null;
            _client = null;
        }

        #endregion

        #region RTCP（RR 发送 / SR 解析）

        /// <summary>
        /// 收到 RTCP Sender Report 时触发。SR 提供 RTP 时间戳↔NTP 墙钟的映射，
        /// DVR 录制可据此把媒体帧定位到真实时间并做音视频对时。
        /// </summary>
        public event EventHandler<RtcpSenderReportEventArgs>? SenderReportReceived;

        private readonly ConcurrentDictionary<byte, RtpReceptionTracker> _rtpTrackers = new();
        private CancellationTokenSource? _rtcpCts;
        private Task? _rtcpTask;

        /// <summary>
        /// 每个 RTP 通道的接收统计（RFC 3550 A.1/A.8）
        /// </summary>
        private sealed class RtpReceptionTracker
        {
            public int TrackId;
            public byte RtcpChannel;  // interleaved 通道号；UDP 轨为 trackId*2+1（仅作键使用）
            public int ClockRate = 90000;
            public uint LocalSsrc = (uint)Guid.NewGuid().GetHashCode();

            public bool Initialized;
            public uint RemoteSsrc;
            public ushort MaxSeq;
            public uint Cycles;
            public uint BaseSeq;
            public long Received;
            public long ExpectedPrior;
            public long ReceivedPrior;
            public double Jitter;
            public long LastTransit;

            // 最近一次 SR 信息（用于 RR 的 LSR/DLSR 和墙钟映射）
            public uint LastSrNtpMiddle;
            public long LastSrArrivalTicks;

            public void Update(ushort seq, uint rtpTimestamp)
            {
                if (!Initialized)
                {
                    Initialized = true;
                    BaseSeq = seq;
                    MaxSeq = seq;
                }
                else
                {
                    // 序列号回绕检测
                    ushort delta = (ushort)(seq - MaxSeq);
                    if (delta < 0x8000)
                    {
                        if (seq < MaxSeq)
                            Cycles++;
                        MaxSeq = seq;
                    }
                }

                Received++;

                // RFC 3550 A.8 抖动估计（以 RTP 时钟为单位）
                long arrivalRtp = (long)(Stopwatch.GetTimestamp() * (double)ClockRate / Stopwatch.Frequency);
                long transit = arrivalRtp - rtpTimestamp;
                if (LastTransit != 0)
                {
                    long d = Math.Abs(transit - LastTransit);
                    Jitter += (d - Jitter) / 16.0;
                }
                LastTransit = transit;
            }
        }

        /// <summary>
        /// SETUP 成功后为轨道注册 RTCP 统计上下文
        /// </summary>
        private void RegisterRtpTracker(byte rtpChannel, byte rtcpChannel, int trackId, int clockRate)
        {
            _rtpTrackers[rtpChannel] = new RtpReceptionTracker
            {
                TrackId = trackId,
                RtcpChannel = rtcpChannel,
                ClockRate = clockRate > 0 ? clockRate : 90000
            };
        }

        private void UpdateReceptionStats(byte channel, in RTPPacket packet)
        {
            if (_rtpTrackers.TryGetValue(channel, out var tracker))
            {
                tracker.RemoteSsrc = packet.Ssrc;
                tracker.Update(packet.SequenceNumber, packet.Timestamp);
            }
        }

        /// <summary>
        /// 处理 RTCP interleaved 帧：解析复合包中的 Sender Report
        /// </summary>
        private void HandleRtcpFrame(byte rtcpChannel, int trackId, byte[] data)
        {
            int pos = 0;
            while (pos + 8 <= data.Length)
            {
                byte packetType = data[pos + 1];
                int lengthWords = (data[pos + 2] << 8) | data[pos + 3];
                int packetLength = (lengthWords + 1) * 4;
                if (packetLength <= 0 || pos + packetLength > data.Length)
                    break;

                if (packetType == 200 && packetLength >= 28)
                {
                    // Sender Report
                    ulong ntp = 0;
                    for (int i = 0; i < 8; i++)
                        ntp = (ntp << 8) | data[pos + 8 + i];
                    uint rtpTs = (uint)((data[pos + 16] << 24) | (data[pos + 17] << 16) | (data[pos + 18] << 8) | data[pos + 19]);

                    // 记录到对应轨道的 tracker（LSR = NTP 中间 32 位）
                    foreach (var tracker in _rtpTrackers.Values)
                    {
                        if (tracker.RtcpChannel == rtcpChannel)
                        {
                            tracker.LastSrNtpMiddle = (uint)((ntp >> 16) & 0xFFFFFFFF);
                            tracker.LastSrArrivalTicks = Stopwatch.GetTimestamp();
                            break;
                        }
                    }

                    try
                    {
                        SenderReportReceived?.Invoke(this, new RtcpSenderReportEventArgs(
                            trackId, NtpToUtc(ntp), rtpTs));
                    }
                    catch (Exception ex)
                    {
                        LastException = ex;
                    }
                }

                pos += packetLength;
            }
        }

        private static DateTime NtpToUtc(ulong ntp)
        {
            uint seconds = (uint)(ntp >> 32);
            uint fraction = (uint)(ntp & 0xFFFFFFFF);
            double ms = fraction * 1000.0 / 4294967296.0;
            return new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                .AddSeconds(seconds)
                .AddMilliseconds(ms);
        }

        /// <summary>
        /// PLAY 成功后调用：每 5 秒对每个轨道发送一次 Receiver Report
        /// </summary>
        private void StartRtcp()
        {
            if (!EnableRtcp)
                return;
            if (_rtcpTask is { IsCompleted: false })
                return;

            _rtcpCts?.Dispose();
            _rtcpCts = new CancellationTokenSource();
            var ct = _rtcpCts.Token;
            _rtcpTask = Task.Run(() => RtcpLoopAsync(ct));
        }

        private void StopRtcp()
        {
            try { _rtcpCts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private async Task RtcpLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && State == RTSPConnectionState.Playing)
            {
                try
                {
                    await Task.Delay(5000, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                foreach (var tracker in _rtpTrackers.Values)
                {
                    if (!tracker.Initialized)
                        continue;

                    try
                    {
                        byte[] rr = BuildReceiverReport(tracker);

                        // UDP 轨走 UDP socket，interleaved 轨走 TCP $ 帧
                        if (!await TrySendRtcpOverUdpAsync(tracker.TrackId, rr, ct).ConfigureAwait(false))
                        {
                            await SendInterleavedAsync(tracker.RtcpChannel, rr, ct).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        LastException = ex;
                        return; // 发送失败通常意味着连接已断，交给接收循环处理
                    }
                }
            }
        }

        private static byte[] BuildReceiverReport(RtpReceptionTracker t)
        {
            uint extended = (t.Cycles << 16) | t.MaxSeq;
            long expected = extended - t.BaseSeq + 1;
            long lost = Math.Max(0, expected - t.Received);
            if (lost > 0x7FFFFF) lost = 0x7FFFFF;

            long expectedInterval = expected - t.ExpectedPrior;
            long receivedInterval = t.Received - t.ReceivedPrior;
            t.ExpectedPrior = expected;
            t.ReceivedPrior = t.Received;
            long lostInterval = expectedInterval - receivedInterval;
            byte fractionLost = expectedInterval <= 0 || lostInterval <= 0
                ? (byte)0
                : (byte)Math.Min(255, lostInterval * 256 / expectedInterval);

            uint dlsr = 0;
            if (t.LastSrArrivalTicks != 0)
            {
                double secondsSinceSr = (Stopwatch.GetTimestamp() - t.LastSrArrivalTicks) / (double)Stopwatch.Frequency;
                dlsr = (uint)(secondsSinceSr * 65536);
            }

            var rr = new ReceiverReport { Ssrc = t.LocalSsrc };
            rr.Reports.Add(new ReceptionReport
            {
                Ssrc = t.RemoteSsrc,
                FractionLost = fractionLost,
                CumulativeLost = (uint)lost,
                ExtendedHighestSequence = extended,
                Jitter = (uint)t.Jitter,
                LastSrTimestamp = t.LastSrNtpMiddle,
                DelaySinceLastSr = dlsr
            });

            return rr.Serialize();
        }

        /// <summary>
        /// 发送一帧 TCP interleaved 数据（持发送锁，避免与 RTSP 请求交错）
        /// </summary>
        private async Task SendInterleavedAsync(byte channel, byte[] payload, CancellationToken ct)
        {
            var stream = _tcpStream;
            if (stream == null)
                throw new RTSPConnectionException("Not connected");

            byte[] frame = new byte[4 + payload.Length];
            frame[0] = 0x24;
            frame[1] = channel;
            frame[2] = (byte)(payload.Length >> 8);
            frame[3] = (byte)(payload.Length & 0xFF);
            Array.Copy(payload, 0, frame, 4, payload.Length);

            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(frame, 0, frame.Length, ct).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        #endregion
    }

    /// <summary>
    /// RTCP Sender Report 事件参数：提供 RTP 时间戳与 NTP 墙钟的对应关系
    /// </summary>
    public class RtcpSenderReportEventArgs : EventArgs
    {
        public RtcpSenderReportEventArgs(int trackId, DateTime ntpTimeUtc, uint rtpTimestamp)
        {
            TrackId = trackId;
            NtpTimeUtc = ntpTimeUtc;
            RtpTimestamp = rtpTimestamp;
        }

        /// <summary>轨道 ID</summary>
        public int TrackId { get; }

        /// <summary>SR 中的 NTP 时间（UTC）</summary>
        public DateTime NtpTimeUtc { get; }

        /// <summary>与 NtpTimeUtc 同一时刻的 RTP 时间戳</summary>
        public uint RtpTimestamp { get; }
    }
}
