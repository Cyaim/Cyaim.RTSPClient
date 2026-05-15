using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Cyaim.RTSPClient.Media.Codecs
{
    /// <summary>
    /// 软件音频解码器工厂
    /// </summary>
    internal sealed class SoftwareAudioDecoderFactory : IAudioDecoderFactory
    {
        public string Name => "Software";
        public int Priority => 0;
        public bool IsHardwareAccelerated => false;
        public AudioCodec[] SupportedCodecs =>
        [
            AudioCodec.PCMA, AudioCodec.PCMU, AudioCodec.G722,
            AudioCodec.G726, AudioCodec.G728, AudioCodec.G729,
            AudioCodec.AAC, AudioCodec.AMR, AudioCodec.AMR_WB,
            AudioCodec.OPUS, AudioCodec.SPEEX
        ];

        public bool CanCreate(AudioCodec codec, bool preferHardware = true)
        {
            return Array.Exists(SupportedCodecs, c => c == codec);
        }

        public IAudioDecoder Create(AudioCodec codec)
        {
            return codec switch
            {
                AudioCodec.PCMA or AudioCodec.PCMU => new G711Decoder(codec),
                AudioCodec.G722 => new G722Decoder(),
                AudioCodec.G726 => new G726Decoder(),
                AudioCodec.G729 => new G729Decoder(),
                AudioCodec.AAC => new AacDecoder(),
                AudioCodec.AMR or AudioCodec.AMR_WB => new AmrDecoder(codec),
                AudioCodec.OPUS => new OpusDecoder(),
                AudioCodec.SPEEX => new SpeexDecoder(),
                _ => throw new NotSupportedException($"Software decoder not available for {codec}")
            };
        }
    }

    /// <summary>
    /// 软件音频编码器工厂
    /// </summary>
    internal sealed class SoftwareAudioEncoderFactory : IAudioEncoderFactory
    {
        public string Name => "Software";
        public int Priority => 0;
        public bool IsHardwareAccelerated => false;
        public AudioCodec[] SupportedCodecs => new[]
        {
            AudioCodec.PCMA, AudioCodec.PCMU, AudioCodec.G722,
            AudioCodec.G726, AudioCodec.G729,
            AudioCodec.AAC, AudioCodec.AMR, AudioCodec.AMR_WB,
            AudioCodec.OPUS, AudioCodec.SPEEX
        };

        public bool CanCreate(AudioCodec codec, bool preferHardware = true)
        {
            return Array.Exists(SupportedCodecs, c => c == codec);
        }

        public IAudioEncoder Create(AudioCodec codec)
        {
            return codec switch
            {
                AudioCodec.PCMA or AudioCodec.PCMU => new G711Encoder(codec),
                AudioCodec.G722 => new G722Encoder(),
                AudioCodec.G726 => new G726Encoder(),
                AudioCodec.G729 => new G729Encoder(),
                AudioCodec.AAC => new AacEncoder(),
                AudioCodec.AMR or AudioCodec.AMR_WB => new AmrEncoder(codec),
                AudioCodec.OPUS => new OpusEncoder(),
                AudioCodec.SPEEX => new SpeexEncoder(),
                _ => throw new NotSupportedException($"Software encoder not available for {codec}")
            };
        }
    }

    #region G.711 解码器/编码器

    internal sealed class G711Decoder : IAudioDecoder
    {
        private readonly AudioCodec _codec;
        public string Name => $"G.711 {(_codec == AudioCodec.PCMA ? "A-law" : "μ-law")} Decoder";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State { get; private set; } = ProcessorState.Idle;
        public AudioCodec[] SupportedCodecs => new[] { _codec };

        public G711Decoder(AudioCodec codec) => _codec = codec;

        public Task InitializeAsync(AudioDecoderConfig config, CancellationToken ct = default)
        {
            State = ProcessorState.Ready;
            return Task.CompletedTask;
        }

        public Task<AudioFrame> DecodeAsync(EncodedAudioFrame input, CancellationToken ct = default)
        {
            if (State != ProcessorState.Ready)
                throw new InvalidOperationException("Decoder not initialized");

            // G.711 解码：8-bit 压缩 -> 16-bit PCM
            var encoded = input.Data.Span;
            var pcm = new byte[encoded.Length * 2];

            for (int i = 0; i < encoded.Length; i++)
            {
                short sample = _codec == AudioCodec.PCMA
                    ? AlawToLinear(encoded[i])
                    : UlawToLinear(encoded[i]);
                pcm[i * 2] = (byte)(sample & 0xFF);
                pcm[i * 2 + 1] = (byte)(sample >> 8);
            }

            return Task.FromResult(new AudioFrame
            {
                Data = pcm,
                SampleRate = input.SampleRate,
                Channels = input.Channels,
                BitsPerSample = 16,
                Timestamp = input.Timestamp
            });
        }

        public async IAsyncEnumerable<AudioFrame> DecodeStreamAsync(
            IAsyncEnumerable<EncodedAudioFrame> inputStream,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in inputStream.WithCancellation(ct))
            {
                yield return await DecodeAsync(frame, ct);
            }
        }

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => State = ProcessorState.Disposed;

        // A-law to 16-bit linear
        private static short AlawToLinear(byte a)
        {
            a ^= 0x55;
            int t = (a & 0x7F) << 4;
            int seg = (a & 0x70) >> 4;
            switch (seg)
            {
                case 0: t += 8; break;
                case 1: t += 0x108; break;
                default: t += 0x108; t <<= seg - 1; break;
            }
            return (short)((a & 0x80) != 0 ? t : -t);
        }

        // μ-law to 16-bit linear
        private static short UlawToLinear(byte u)
        {
            u = (byte)~u;
            int t = ((u & 0x0F) << 3) + 0x84;
            t <<= (u & 0x70) >> 4;
            return (short)((u & 0x80) != 0 ? (0x84 - t) : (t - 0x84));
        }
    }

    internal sealed class G711Encoder : IAudioEncoder
    {
        private readonly AudioCodec _codec;
        public string Name => $"G.711 {(_codec == AudioCodec.PCMA ? "A-law" : "μ-law")} Encoder";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State { get; private set; } = ProcessorState.Idle;
        public AudioCodec[] SupportedCodecs => new[] { _codec };

        public G711Encoder(AudioCodec codec) => _codec = codec;

        public Task InitializeAsync(AudioEncoderConfig config, CancellationToken ct = default)
        {
            State = ProcessorState.Ready;
            return Task.CompletedTask;
        }

        public Task<EncodedAudioFrame> EncodeAsync(AudioFrame input, CancellationToken ct = default)
        {
            if (State != ProcessorState.Ready)
                throw new InvalidOperationException("Encoder not initialized");

            // G.711 编码：16-bit PCM -> 8-bit 压缩
            var pcm = input.Data.Span;
            var encoded = new byte[pcm.Length / 2];

            for (int i = 0; i < encoded.Length; i++)
            {
                short sample = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
                encoded[i] = _codec == AudioCodec.PCMA
                    ? LinearToAlaw(sample)
                    : LinearToUlaw(sample);
            }

            return Task.FromResult(new EncodedAudioFrame
            {
                Data = encoded,
                Codec = _codec,
                SampleRate = input.SampleRate,
                Channels = input.Channels,
                Timestamp = input.Timestamp,
                Duration = input.Duration
            });
        }

        public async IAsyncEnumerable<EncodedAudioFrame> EncodeStreamAsync(
            IAsyncEnumerable<AudioFrame> inputStream,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in inputStream.WithCancellation(ct))
            {
                yield return await EncodeAsync(frame, ct);
            }
        }

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => State = ProcessorState.Disposed;

        private static byte LinearToAlaw(short sample)
        {
            int mask = sample >> 8 & 0x80;
            if (mask != 0) sample = (short)-sample;
            if (sample > 32635) sample = 32635;

            int seg = 0;
            if (sample >= 256)
            {
                for (int i = 7; i > 0; i--)
                {
                    if (sample >= (256 << i)) { seg = i + 1; break; }
                }
            }

            byte aval = (byte)(seg << 4);
            if (seg >= 2) aval |= (byte)((sample >> (seg + 3)) & 0x0F);
            else aval |= (byte)((sample >> 4) & 0x0F);

            return (byte)(aval ^ (byte)(mask ^ 0xD5));
        }

        private static byte LinearToUlaw(short sample)
        {
            const int BIAS = 0x84;
            const int CLIP = 32635;

            int sign = 0;
            if (sample < 0) { sign = 0x80; sample = (short)-sample; }
            if (sample > CLIP) sample = CLIP;
            sample += BIAS;

            int seg = 0;
            for (int i = 7; i > 0; i--)
            {
                if (sample >= (256 << i)) { seg = i + 1; break; }
            }

            byte uval = (byte)(seg << 4 | ((sample >> (seg + 3)) & 0x0F));
            return (byte)((uval | sign) ^ 0xFF);
        }
    }

    #endregion

    #region 其他编解码器存根

    internal sealed class G722Decoder : IAudioDecoder
    {
        public string Name => "G.722 Decoder";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State { get; private set; } = ProcessorState.Idle;
        public AudioCodec[] SupportedCodecs => new[] { AudioCodec.G722 };
        public Task InitializeAsync(AudioDecoderConfig config, CancellationToken ct = default) { State = ProcessorState.Ready; return Task.CompletedTask; }
        public Task<AudioFrame> DecodeAsync(EncodedAudioFrame input, CancellationToken ct = default) => throw new NotImplementedException("G.722 decoder not implemented");
        public IAsyncEnumerable<AudioFrame> DecodeStreamAsync(IAsyncEnumerable<EncodedAudioFrame> inputStream, CancellationToken ct = default) => throw new NotImplementedException();
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => State = ProcessorState.Disposed;
    }

    internal sealed class G722Encoder : IAudioEncoder
    {
        public string Name => "G.722 Encoder";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State { get; private set; } = ProcessorState.Idle;
        public AudioCodec[] SupportedCodecs => new[] { AudioCodec.G722 };
        public Task InitializeAsync(AudioEncoderConfig config, CancellationToken ct = default) { State = ProcessorState.Ready; return Task.CompletedTask; }
        public Task<EncodedAudioFrame> EncodeAsync(AudioFrame input, CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<EncodedAudioFrame> EncodeStreamAsync(IAsyncEnumerable<AudioFrame> inputStream, CancellationToken ct = default) => throw new NotImplementedException();
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => State = ProcessorState.Disposed;
    }

    internal sealed class G726Decoder : IAudioDecoder
    {
        public string Name => "G.726 Decoder";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State { get; private set; } = ProcessorState.Idle;
        public AudioCodec[] SupportedCodecs => new[] { AudioCodec.G726 };
        public Task InitializeAsync(AudioDecoderConfig config, CancellationToken ct = default) { State = ProcessorState.Ready; return Task.CompletedTask; }
        public Task<AudioFrame> DecodeAsync(EncodedAudioFrame input, CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<AudioFrame> DecodeStreamAsync(IAsyncEnumerable<EncodedAudioFrame> inputStream, CancellationToken ct = default) => throw new NotImplementedException();
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => State = ProcessorState.Disposed;
    }

    internal sealed class G726Encoder : IAudioEncoder
    {
        public string Name => "G.726 Encoder";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State { get; private set; } = ProcessorState.Idle;
        public AudioCodec[] SupportedCodecs => new[] { AudioCodec.G726 };
        public Task InitializeAsync(AudioEncoderConfig config, CancellationToken ct = default) { State = ProcessorState.Ready; return Task.CompletedTask; }
        public Task<EncodedAudioFrame> EncodeAsync(AudioFrame input, CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<EncodedAudioFrame> EncodeStreamAsync(IAsyncEnumerable<AudioFrame> inputStream, CancellationToken ct = default) => throw new NotImplementedException();
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => State = ProcessorState.Disposed;
    }

    internal sealed class G729Decoder : IAudioDecoder
    {
        public string Name => "G.729 Decoder";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State { get; private set; } = ProcessorState.Idle;
        public AudioCodec[] SupportedCodecs => new[] { AudioCodec.G729 };
        public Task InitializeAsync(AudioDecoderConfig config, CancellationToken ct = default) { State = ProcessorState.Ready; return Task.CompletedTask; }
        public Task<AudioFrame> DecodeAsync(EncodedAudioFrame input, CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<AudioFrame> DecodeStreamAsync(IAsyncEnumerable<EncodedAudioFrame> inputStream, CancellationToken ct = default) => throw new NotImplementedException();
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => State = ProcessorState.Disposed;
    }

    internal sealed class G729Encoder : IAudioEncoder
    {
        public string Name => "G.729 Encoder";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State { get; private set; } = ProcessorState.Idle;
        public AudioCodec[] SupportedCodecs => new[] { AudioCodec.G729 };
        public Task InitializeAsync(AudioEncoderConfig config, CancellationToken ct = default) { State = ProcessorState.Ready; return Task.CompletedTask; }
        public Task<EncodedAudioFrame> EncodeAsync(AudioFrame input, CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<EncodedAudioFrame> EncodeStreamAsync(IAsyncEnumerable<AudioFrame> inputStream, CancellationToken ct = default) => throw new NotImplementedException();
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => State = ProcessorState.Disposed;
    }

    internal sealed class AacDecoder : IAudioDecoder
    {
        private readonly AACDecoderImpl _impl = new();

        public string Name => _impl.Name;
        public bool IsHardwareAccelerated => false;
        public ProcessorState State => _impl.State;
        public AudioCodec[] SupportedCodecs => _impl.SupportedCodecs;

        public Task InitializeAsync(AudioDecoderConfig config, CancellationToken ct = default)
            => _impl.InitializeAsync(config, ct);

        public Task<AudioFrame> DecodeAsync(EncodedAudioFrame input, CancellationToken ct = default)
            => _impl.DecodeAsync(input, ct);

        public async IAsyncEnumerable<AudioFrame> DecodeStreamAsync(
            IAsyncEnumerable<EncodedAudioFrame> inputStream,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in inputStream.WithCancellation(ct))
            {
                yield return await _impl.DecodeAsync(frame, ct);
            }
        }

        public Task FlushAsync(CancellationToken ct = default)
            => _impl.FlushAsync(ct);

        public void Dispose() => _impl.Dispose();
    }

    internal sealed class AacEncoder : IAudioEncoder
    {
        private readonly AACEncoderImpl _impl = new();

        public string Name => _impl.Name;
        public bool IsHardwareAccelerated => false;
        public ProcessorState State => _impl.State;
        public AudioCodec[] SupportedCodecs => _impl.SupportedCodecs;

        public Task InitializeAsync(AudioEncoderConfig config, CancellationToken ct = default)
            => _impl.InitializeAsync(config, ct);

        public Task<EncodedAudioFrame> EncodeAsync(AudioFrame input, CancellationToken ct = default)
            => _impl.EncodeAsync(input, ct);

        public async IAsyncEnumerable<EncodedAudioFrame> EncodeStreamAsync(
            IAsyncEnumerable<AudioFrame> inputStream,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in inputStream.WithCancellation(ct))
            {
                yield return await _impl.EncodeAsync(frame, ct);
            }
        }

        public Task FlushAsync(CancellationToken ct = default)
            => _impl.FlushAsync(ct);

        public void Dispose() => _impl.Dispose();
    }

    internal sealed class AmrDecoder : IAudioDecoder
    {
        private readonly AudioCodec _codec;
        public string Name => $"AMR {(_codec == AudioCodec.AMR_WB ? "WB" : "NB")} Decoder";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State { get; private set; } = ProcessorState.Idle;
        public AudioCodec[] SupportedCodecs => new[] { _codec };
        public AmrDecoder(AudioCodec codec) => _codec = codec;
        public Task InitializeAsync(AudioDecoderConfig config, CancellationToken ct = default) { State = ProcessorState.Ready; return Task.CompletedTask; }
        public Task<AudioFrame> DecodeAsync(EncodedAudioFrame input, CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<AudioFrame> DecodeStreamAsync(IAsyncEnumerable<EncodedAudioFrame> inputStream, CancellationToken ct = default) => throw new NotImplementedException();
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => State = ProcessorState.Disposed;
    }

    internal sealed class AmrEncoder : IAudioEncoder
    {
        private readonly AudioCodec _codec;
        public string Name => $"AMR {(_codec == AudioCodec.AMR_WB ? "WB" : "NB")} Encoder";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State { get; private set; } = ProcessorState.Idle;
        public AudioCodec[] SupportedCodecs => new[] { _codec };
        public AmrEncoder(AudioCodec codec) => _codec = codec;
        public Task InitializeAsync(AudioEncoderConfig config, CancellationToken ct = default) { State = ProcessorState.Ready; return Task.CompletedTask; }
        public Task<EncodedAudioFrame> EncodeAsync(AudioFrame input, CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<EncodedAudioFrame> EncodeStreamAsync(IAsyncEnumerable<AudioFrame> inputStream, CancellationToken ct = default) => throw new NotImplementedException();
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => State = ProcessorState.Disposed;
    }

    internal sealed class OpusDecoder : IAudioDecoder
    {
        public string Name => "Opus Decoder";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State { get; private set; } = ProcessorState.Idle;
        public AudioCodec[] SupportedCodecs => new[] { AudioCodec.OPUS };
        public Task InitializeAsync(AudioDecoderConfig config, CancellationToken ct = default) { State = ProcessorState.Ready; return Task.CompletedTask; }
        public Task<AudioFrame> DecodeAsync(EncodedAudioFrame input, CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<AudioFrame> DecodeStreamAsync(IAsyncEnumerable<EncodedAudioFrame> inputStream, CancellationToken ct = default) => throw new NotImplementedException();
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => State = ProcessorState.Disposed;
    }

    internal sealed class OpusEncoder : IAudioEncoder
    {
        public string Name => "Opus Encoder";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State { get; private set; } = ProcessorState.Idle;
        public AudioCodec[] SupportedCodecs => new[] { AudioCodec.OPUS };
        public Task InitializeAsync(AudioEncoderConfig config, CancellationToken ct = default) { State = ProcessorState.Ready; return Task.CompletedTask; }
        public Task<EncodedAudioFrame> EncodeAsync(AudioFrame input, CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<EncodedAudioFrame> EncodeStreamAsync(IAsyncEnumerable<AudioFrame> inputStream, CancellationToken ct = default) => throw new NotImplementedException();
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => State = ProcessorState.Disposed;
    }

    internal sealed class SpeexDecoder : IAudioDecoder
    {
        public string Name => "Speex Decoder";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State { get; private set; } = ProcessorState.Idle;
        public AudioCodec[] SupportedCodecs => new[] { AudioCodec.SPEEX };
        public Task InitializeAsync(AudioDecoderConfig config, CancellationToken ct = default) { State = ProcessorState.Ready; return Task.CompletedTask; }
        public Task<AudioFrame> DecodeAsync(EncodedAudioFrame input, CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<AudioFrame> DecodeStreamAsync(IAsyncEnumerable<EncodedAudioFrame> inputStream, CancellationToken ct = default) => throw new NotImplementedException();
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => State = ProcessorState.Disposed;
    }

    internal sealed class SpeexEncoder : IAudioEncoder
    {
        public string Name => "Speex Encoder";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State { get; private set; } = ProcessorState.Idle;
        public AudioCodec[] SupportedCodecs => new[] { AudioCodec.SPEEX };
        public Task InitializeAsync(AudioEncoderConfig config, CancellationToken ct = default) { State = ProcessorState.Ready; return Task.CompletedTask; }
        public Task<EncodedAudioFrame> EncodeAsync(AudioFrame input, CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<EncodedAudioFrame> EncodeStreamAsync(IAsyncEnumerable<AudioFrame> inputStream, CancellationToken ct = default) => throw new NotImplementedException();
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => State = ProcessorState.Disposed;
    }

    #endregion
}
