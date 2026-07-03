using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Cyaim.RTSPClient.Rtp
{
    /// <summary>
    /// 视频接收器
    /// </summary>
    public class VideoReceiver : IDisposable
    {
        private readonly ChannelReader<RTPPacket> _rtpReader;
        private readonly IRTPDepacketizer _depacketizer;
        private Channel<MediaFrame>? _frameChannel;
        private CancellationTokenSource? _cts;
        private Task? _receiveTask;

        public event EventHandler<VideoFrameEventArgs>? FrameReceived;
        public event EventHandler<VideoFrameEventArgs>? KeyFrameReceived;
        public bool IsReceiving { get; private set; }
        public long FrameCount { get; private set; }
        public long KeyFrameCount { get; private set; }

        public VideoReceiver(ChannelReader<RTPPacket> rtpReader, IRTPDepacketizer depacketizer)
        {
            _rtpReader = rtpReader;
            _depacketizer = depacketizer ?? throw new ArgumentNullException(nameof(depacketizer));
        }

        public static VideoReceiver CreateH264Receiver(ChannelReader<RTPPacket> rtpReader)
            => new VideoReceiver(rtpReader, new H264Depacketizer());

        /// <summary>
        /// 从 SDP 编码信息创建 H.264 接收器（自动注入 sprop-parameter-sets 中的 SPS/PPS）
        /// </summary>
        public static VideoReceiver CreateH264Receiver(ChannelReader<RTPPacket> rtpReader, Media.CodecInfo? codecInfo)
            => new VideoReceiver(rtpReader, H264Depacketizer.CreateFromCodecInfo(codecInfo));

        public static VideoReceiver CreateH265Receiver(ChannelReader<RTPPacket> rtpReader)
            => new VideoReceiver(rtpReader, new H265Depacketizer());

        /// <summary>
        /// 从 SDP 编码信息创建 H.265 接收器（自动注入 sprop-vps/sps/pps）
        /// </summary>
        public static VideoReceiver CreateH265Receiver(ChannelReader<RTPPacket> rtpReader, Media.CodecInfo? codecInfo)
            => new VideoReceiver(rtpReader, H265Depacketizer.CreateFromCodecInfo(codecInfo));

        public void StartReceiving(int bufferSize = 1024)
        {
            if (IsReceiving) return;
            _cts = new CancellationTokenSource();
            _frameChannel = Channel.CreateBounded<MediaFrame>(new BoundedChannelOptions(bufferSize)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            });
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            IsReceiving = true;
        }

        public void StopReceiving()
        {
            if (!IsReceiving) return;
            _cts?.Cancel();
            try { _receiveTask?.Wait(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { }
            _frameChannel?.Writer.TryComplete();
            IsReceiving = false;
        }

        /// <summary>
        /// 异步停止接收（不阻塞调用线程）
        /// </summary>
        public async Task StopReceivingAsync()
        {
            if (!IsReceiving) return;
            _cts?.Cancel();
            var task = _receiveTask;
            if (task != null)
            {
                try { await Task.WhenAny(task, Task.Delay(5000)).ConfigureAwait(false); }
                catch { }
            }
            _frameChannel?.Writer.TryComplete();
            IsReceiving = false;
        }

        public ChannelReader<MediaFrame> GetFrameReader()
        {
            return _frameChannel?.Reader ?? throw new InvalidOperationException("Call StartReceiving() first");
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && await _rtpReader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (_rtpReader.TryRead(out var packet))
                    {
                        foreach (var frame in _depacketizer.Feed(packet))
                        {
                            FrameCount++;
                            if (frame.IsKeyFrame) KeyFrameCount++;
                            await _frameChannel!.Writer.WriteAsync(frame, ct).ConfigureAwait(false);

                            // 事件处理器异常不允许终止接收循环
                            try
                            {
                                var args = new VideoFrameEventArgs(frame);
                                FrameReceived?.Invoke(this, args);
                                if (frame.IsKeyFrame) KeyFrameReceived?.Invoke(this, args);
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ChannelClosedException) { }
        }

        public void Dispose()
        {
            StopReceiving();
            _cts?.Dispose();
        }
    }

    public class VideoFrameEventArgs : EventArgs
    {
        public MediaFrame Frame { get; }
        public VideoFrameEventArgs(MediaFrame frame) => Frame = frame;
    }
}
