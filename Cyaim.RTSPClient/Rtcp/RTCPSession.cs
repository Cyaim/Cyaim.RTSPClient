using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Cyaim.RTSPClient.Rtcp
{
    /// <summary>
    /// RTCP会话管理
    /// 定期发送Sender Report，接收和处理Receiver Report
    /// </summary>
    public class RTCPSession : IDisposable
    {
        private readonly Func<byte[], byte, CancellationToken, Task> _sendRtpData;
        private Timer? _timer;
        private int _packetCount;
        private long _octetCount;
        private readonly Dictionary<int, uint> _trackSsrcMap = new Dictionary<int, uint>();
        private CancellationTokenSource? _cts;

        public RTCPSession(Func<byte[], byte, CancellationToken, Task> sendRtpData)
        {
            _sendRtpData = sendRtpData;
        }

        /// <summary>
        /// 注册track的SSRC
        /// </summary>
        public void RegisterTrack(int trackId, uint ssrc)
        {
            _trackSsrcMap[trackId] = ssrc;
        }

        /// <summary>
        /// 记录已发送的RTP包
        /// </summary>
        public void RecordPacketSent(uint octets)
        {
            Interlocked.Increment(ref _packetCount);
            // 注意: 这里简化处理，实际应该用Interlocked.Add
            _octetCount += octets;
        }

        /// <summary>
        /// 启动定期SR发送
        /// </summary>
        public void Start(int intervalMs = 5000)
        {
            _cts = new CancellationTokenSource();
            _timer = new Timer(async _ => await SendSenderReportsAsync(), null, intervalMs, intervalMs);
        }

        /// <summary>
        /// 发送Sender Report
        /// </summary>
        private async Task SendSenderReportsAsync()
        {
            if (_cts?.IsCancellationRequested == true)
                return;

            var cts = _cts;
            if (cts is null)
                return;

            foreach (var kvp in _trackSsrcMap)
            {
                try
                {
                    await SendSenderReportAsync(kvp.Key, kvp.Value, cts.Token);
                }
                catch
                {
                    // 发送失败不影响其他track
                }
            }
        }

        /// <summary>
        /// 发送单个track的SR
        /// </summary>
        private async Task SendSenderReportAsync(int trackId, uint ssrc, CancellationToken ct)
        {
            var sr = new SenderReport
            {
                Ssrc = ssrc,
                NtpTimestamp = SenderReport.GetNtpTimestamp(),
                RtpTimestamp = (uint)(DateTime.UtcNow.Ticks & 0xFFFFFFFF),
                SenderPacketCount = (uint)_packetCount,
                SenderOctetCount = (uint)_octetCount
            };

            byte[] data = sr.Serialize();

            // RTCP使用RTP端口+1 (奇数端口)
            // 对于TCP interleaved，channel = trackId * 2 + 1
            byte channel = (byte)(trackId * 2 + 1);

            await _sendRtpData(data, channel, ct);
        }

        /// <summary>
        /// 处理接收到的RTCP包
        /// </summary>
        public void ProcessReceivedPacket(byte[] data)
        {
            try
            {
                var packet = RTCPPacket.Parse(data);
                if (packet is ReceiverReport rr)
                {
                    // 处理Receiver Report
                    OnReceiverReport?.Invoke(this, rr);
                }
            }
            catch
            {
                // 解析失败忽略
            }
        }

        /// <summary>
        /// 接收到Receiver Report事件
        /// </summary>
        public event EventHandler<ReceiverReport>? OnReceiverReport;

        /// <summary>
        /// 停止发送
        /// </summary>
        public void Stop()
        {
            _timer?.Dispose();
            _cts?.Cancel();
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}
