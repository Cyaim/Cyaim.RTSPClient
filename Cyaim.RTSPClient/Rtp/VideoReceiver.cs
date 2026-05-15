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

        public static VideoReceiver CreateH265Receiver(ChannelReader<RTPPacket> rtpReader)
            => new VideoReceiver(rtpReader, new H265Depacketizer());

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
            _receiveTask?.Wait(TimeSpan.FromSeconds(5));
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
                while (!ct.IsCancellationRequested && await _rtpReader.WaitToReadAsync(ct))
                {
                    while (_rtpReader.TryRead(out var packet))
                    {
                        foreach (var frame in _depacketizer.Feed(packet))
                        {
                            FrameCount++;
                            if (frame.IsKeyFrame) KeyFrameCount++;
                            await _frameChannel!.Writer.WriteAsync(frame, ct);
                            var args = new VideoFrameEventArgs(frame);
                            FrameReceived?.Invoke(this, args);
                            if (frame.IsKeyFrame) KeyFrameReceived?.Invoke(this, args);
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
