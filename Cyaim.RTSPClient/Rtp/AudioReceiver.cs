using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Cyaim.RTSPClient.Rtp
{
    /// <summary>
    /// 音频接收器
    /// </summary>
    public class AudioReceiver : IDisposable
    {
        private readonly ChannelReader<RTPPacket> _rtpReader;
        private readonly IRTPDepacketizer _depacketizer;
        private Channel<MediaFrame>? _frameChannel;
        private CancellationTokenSource? _cts;
        private Task? _receiveTask;

        public event EventHandler<AudioFrameEventArgs>? FrameReceived;
        public bool IsReceiving { get; private set; }
        public long FrameCount { get; private set; }
        public AudioCodec Codec { get; }
        public int SampleRate { get; }
        public int Channels { get; }

        public AudioReceiver(ChannelReader<RTPPacket> rtpReader, IRTPDepacketizer depacketizer,
            AudioCodec codec, int sampleRate, int channels)
        {
            _rtpReader = rtpReader;
            _depacketizer = depacketizer ?? throw new ArgumentNullException(nameof(depacketizer));
            Codec = codec;
            SampleRate = sampleRate;
            Channels = channels;
        }

        public static AudioReceiver CreateG711AReceiver(ChannelReader<RTPPacket> rtpReader,
            int sampleRate = 8000, int channels = 1)
            => new AudioReceiver(rtpReader, new AudioDepacketizer(AudioCodec.PCMA, sampleRate),
                AudioCodec.PCMA, sampleRate, channels);

        public static AudioReceiver CreateG711UReceiver(ChannelReader<RTPPacket> rtpReader,
            int sampleRate = 8000, int channels = 1)
            => new AudioReceiver(rtpReader, new AudioDepacketizer(AudioCodec.PCMU, sampleRate),
                AudioCodec.PCMU, sampleRate, channels);

        public static AudioReceiver CreateAACReceiver(ChannelReader<RTPPacket> rtpReader,
            int sampleRate = 44100, int channels = 2)
            => new AudioReceiver(rtpReader, new AACDepacketizer(sampleRate, channels),
                AudioCodec.AAC, sampleRate, channels);

        public void StartReceiving(int bufferSize = 2048)
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
                while (!ct.IsCancellationRequested && await _rtpReader.WaitToReadAsync(ct))
                {
                    while (_rtpReader.TryRead(out var packet))
                    {
                        foreach (var frame in _depacketizer.Feed(packet))
                        {
                            FrameCount++;
                            await _frameChannel!.Writer.WriteAsync(frame, ct).ConfigureAwait(false);

                            // 事件处理器异常不允许终止接收循环
                            try { FrameReceived?.Invoke(this, new AudioFrameEventArgs(frame)); }
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

    public class AudioFrameEventArgs : EventArgs
    {
        public MediaFrame Frame { get; }
        public AudioFrameEventArgs(MediaFrame frame) => Frame = frame;
    }
}
