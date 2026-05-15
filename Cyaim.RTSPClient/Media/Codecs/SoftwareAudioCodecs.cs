using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Cyaim.RTSPClient.Media.Codecs
{
    #region G.711 编解码器

    internal sealed class G711Decoder : IAudioDecoder
    {
        private readonly AudioCodec _codec;
        public string Name => $"G.711 {(_codec == AudioCodec.PCMA ? "A-law" : "μ-law")}";
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
            var encoded = input.Data.Span;
            var pcm = new byte[encoded.Length * 2];
            for (int i = 0; i < encoded.Length; i++)
            {
                short sample = _codec == AudioCodec.PCMA ? AlawToLinear(encoded[i]) : UlawToLinear(encoded[i]);
                pcm[i * 2] = (byte)(sample & 0xFF);
                pcm[i * 2 + 1] = (byte)(sample >> 8);
            }
            return Task.FromResult(new AudioFrame { Data = pcm, SampleRate = input.SampleRate, Channels = input.Channels, BitsPerSample = 16, Timestamp = input.Timestamp });
        }

        public async IAsyncEnumerable<AudioFrame> DecodeStreamAsync(IAsyncEnumerable<EncodedAudioFrame> input, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in input.WithCancellation(ct)) yield return await DecodeAsync(frame, ct);
        }

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => State = ProcessorState.Disposed;

        private static short AlawToLinear(byte a)
        {
            a ^= 0x55; int t = (a & 0x7F) << 4; int seg = (a & 0x70) >> 4;
            switch (seg) { case 0: t += 8; break; case 1: t += 0x108; break; default: t += 0x108; t <<= seg - 1; break; }
            return (short)((a & 0x80) != 0 ? t : -t);
        }

        private static short UlawToLinear(byte u)
        {
            u = (byte)~u; int t = ((u & 0x0F) << 3) + 0x84; t <<= (u & 0x70) >> 4;
            return (short)((u & 0x80) != 0 ? (0x84 - t) : (t - 0x84));
        }
    }

    internal sealed class G711Encoder : IAudioEncoder
    {
        private readonly AudioCodec _codec;
        public string Name => $"G.711 {(_codec == AudioCodec.PCMA ? "A-law" : "μ-law")}";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State { get; private set; } = ProcessorState.Idle;
        public AudioCodec[] SupportedCodecs => new[] { _codec };

        public G711Encoder(AudioCodec codec) => _codec = codec;

        public Task InitializeAsync(AudioEncoderConfig config, CancellationToken ct = default) { State = ProcessorState.Ready; return Task.CompletedTask; }

        public Task<EncodedAudioFrame> EncodeAsync(AudioFrame input, CancellationToken ct = default)
        {
            var pcm = input.Data.Span;
            var encoded = new byte[pcm.Length / 2];
            for (int i = 0; i < encoded.Length; i++)
            {
                short sample = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
                encoded[i] = _codec == AudioCodec.PCMA ? LinearToAlaw(sample) : LinearToUlaw(sample);
            }
            return Task.FromResult(new EncodedAudioFrame { Data = encoded, Codec = _codec, SampleRate = input.SampleRate, Channels = input.Channels, Timestamp = input.Timestamp, Duration = input.Duration });
        }

        public async IAsyncEnumerable<EncodedAudioFrame> EncodeStreamAsync(IAsyncEnumerable<AudioFrame> input, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in input.WithCancellation(ct)) yield return await EncodeAsync(frame, ct);
        }

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => State = ProcessorState.Disposed;

        private static byte LinearToAlaw(short s)
        {
            int mask = s >> 8 & 0x80; if (mask != 0) s = (short)-s; if (s > 32635) s = 32635;
            int seg = 0; if (s >= 256) { for (int i = 7; i > 0; i--) { if (s >= (256 << i)) { seg = i + 1; break; } } }
            byte aval = (byte)(seg << 4); if (seg >= 2) aval |= (byte)((s >> (seg + 3)) & 0x0F); else aval |= (byte)((s >> 4) & 0x0F);
            return (byte)(aval ^ (byte)(mask ^ 0xD5));
        }

        private static byte LinearToUlaw(short s)
        {
            const int BIAS = 0x84, CLIP = 32635; int sign = 0;
            if (s < 0) { sign = 0x80; s = (short)-s; } if (s > CLIP) s = CLIP; s += BIAS;
            int seg = 0; for (int i = 7; i > 0; i--) { if (s >= (256 << i)) { seg = i + 1; break; } }
            byte uval = (byte)(seg << 4 | ((s >> (seg + 3)) & 0x0F)); return (byte)((uval | sign) ^ 0xFF);
        }
    }

    #endregion

    /// <summary>
    /// 软件音频解码器工厂
    /// </summary>
    internal sealed class SoftwareAudioDecoderFactory : IAudioDecoderFactory
    {
        public string Name => "Software";
        public int Priority => 0;
        public bool IsHardwareAccelerated => false;
        public AudioCodec[] SupportedCodecs => new[]
        {
            AudioCodec.PCMA, AudioCodec.PCMU, AudioCodec.G722,
            AudioCodec.G726, AudioCodec.G728, AudioCodec.G729,
            AudioCodec.AAC, AudioCodec.AMR, AudioCodec.AMR_WB,
            AudioCodec.OPUS, AudioCodec.SPEEX
        };

        public bool CanCreate(AudioCodec codec, bool preferHardware = true)
            => Array.Exists(SupportedCodecs, c => c == codec);

        public IAudioDecoder Create(AudioCodec codec)
        {
            return codec switch
            {
                AudioCodec.PCMA or AudioCodec.PCMU => new G711Decoder(codec),
                AudioCodec.G722 => new G722DecoderWrapper(),
                AudioCodec.G726 => new G726DecoderWrapper(),
                AudioCodec.G729 => new G729DecoderWrapper(),
                AudioCodec.AAC => new AacDecoderWrapper(),
                AudioCodec.AMR or AudioCodec.AMR_WB => new StubDecoder($"AMR {codec}"),
                AudioCodec.OPUS => new StubDecoder("Opus"),
                AudioCodec.SPEEX => new StubDecoder("Speex"),
                _ => throw new NotSupportedException($"No decoder for {codec}")
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
            => Array.Exists(SupportedCodecs, c => c == codec);

        public IAudioEncoder Create(AudioCodec codec)
        {
            return codec switch
            {
                AudioCodec.PCMA or AudioCodec.PCMU => new G711Encoder(codec),
                AudioCodec.G722 => new G722EncoderWrapper(),
                AudioCodec.G726 => new G726EncoderWrapper(),
                AudioCodec.G729 => new G729EncoderWrapper(),
                AudioCodec.AAC => new AacEncoderWrapper(),
                AudioCodec.AMR or AudioCodec.AMR_WB => new StubEncoder($"AMR {codec}"),
                AudioCodec.OPUS => new StubEncoder("Opus"),
                AudioCodec.SPEEX => new StubEncoder("Speex"),
                _ => throw new NotSupportedException($"No encoder for {codec}")
            };
        }
    }

    #region G.722 包装器

    internal sealed class G722DecoderWrapper : IAudioDecoder
    {
        private readonly G722Codec _codec = new();
        public string Name => _codec.Name;
        public bool IsHardwareAccelerated => false;
        public ProcessorState State => _codec.State;
        public AudioCodec[] SupportedCodecs => new[] { AudioCodec.G722 };

        public Task InitializeAsync(AudioDecoderConfig config, CancellationToken ct = default)
            => _codec.InitializeAsync(ct);

        public Task<AudioFrame> DecodeAsync(EncodedAudioFrame input, CancellationToken ct = default)
        {
            var pcm = _codec.Decode(input.Data.Span);
            return Task.FromResult(new AudioFrame
            {
                Data = pcm,
                SampleRate = 16000,
                Channels = 1,
                BitsPerSample = 16,
                Timestamp = input.Timestamp
            });
        }

        public async IAsyncEnumerable<AudioFrame> DecodeStreamAsync(
            IAsyncEnumerable<EncodedAudioFrame> inputStream,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in inputStream.WithCancellation(ct))
                yield return await DecodeAsync(frame, ct);
        }

        public Task FlushAsync(CancellationToken ct = default) { _codec.Reset(); return Task.CompletedTask; }
        public void Dispose() => _codec.Dispose();
    }

    internal sealed class G722EncoderWrapper : IAudioEncoder
    {
        private readonly G722Codec _codec = new();
        public string Name => _codec.Name;
        public bool IsHardwareAccelerated => false;
        public ProcessorState State => _codec.State;
        public AudioCodec[] SupportedCodecs => new[] { AudioCodec.G722 };

        public Task InitializeAsync(AudioEncoderConfig config, CancellationToken ct = default)
            => _codec.InitializeAsync(ct);

        public Task<EncodedAudioFrame> EncodeAsync(AudioFrame input, CancellationToken ct = default)
        {
            var encoded = _codec.Encode(input.Data.Span);
            return Task.FromResult(new EncodedAudioFrame
            {
                Data = encoded,
                Codec = AudioCodec.G722,
                SampleRate = 16000,
                Channels = 1,
                Timestamp = input.Timestamp,
                Duration = input.Duration
            });
        }

        public async IAsyncEnumerable<EncodedAudioFrame> EncodeStreamAsync(
            IAsyncEnumerable<AudioFrame> inputStream,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in inputStream.WithCancellation(ct))
                yield return await EncodeAsync(frame, ct);
        }

        public Task FlushAsync(CancellationToken ct = default) { _codec.Reset(); return Task.CompletedTask; }
        public void Dispose() => _codec.Dispose();
    }

    #endregion

    #region G.726 包装器

    internal sealed class G726DecoderWrapper : IAudioDecoder
    {
        private readonly G726Codec _codec = new();
        public string Name => _codec.Name;
        public bool IsHardwareAccelerated => false;
        public ProcessorState State => _codec.State;
        public AudioCodec[] SupportedCodecs => new[] { AudioCodec.G726 };

        public Task InitializeAsync(AudioDecoderConfig config, CancellationToken ct = default)
            => _codec.InitializeAsync(ct);

        public Task<AudioFrame> DecodeAsync(EncodedAudioFrame input, CancellationToken ct = default)
        {
            var pcm = _codec.Decode(input.Data.Span);
            return Task.FromResult(new AudioFrame
            {
                Data = pcm,
                SampleRate = 8000,
                Channels = 1,
                BitsPerSample = 16,
                Timestamp = input.Timestamp
            });
        }

        public async IAsyncEnumerable<AudioFrame> DecodeStreamAsync(
            IAsyncEnumerable<EncodedAudioFrame> inputStream,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in inputStream.WithCancellation(ct))
                yield return await DecodeAsync(frame, ct);
        }

        public Task FlushAsync(CancellationToken ct = default) { _codec.Reset(); return Task.CompletedTask; }
        public void Dispose() => _codec.Dispose();
    }

    internal sealed class G726EncoderWrapper : IAudioEncoder
    {
        private readonly G726Codec _codec = new();
        public string Name => _codec.Name;
        public bool IsHardwareAccelerated => false;
        public ProcessorState State => _codec.State;
        public AudioCodec[] SupportedCodecs => new[] { AudioCodec.G726 };

        public Task InitializeAsync(AudioEncoderConfig config, CancellationToken ct = default)
            => _codec.InitializeAsync(ct);

        public Task<EncodedAudioFrame> EncodeAsync(AudioFrame input, CancellationToken ct = default)
        {
            var encoded = _codec.Encode(input.Data.Span);
            return Task.FromResult(new EncodedAudioFrame
            {
                Data = encoded,
                Codec = AudioCodec.G726,
                SampleRate = 8000,
                Channels = 1,
                Timestamp = input.Timestamp,
                Duration = input.Duration
            });
        }

        public async IAsyncEnumerable<EncodedAudioFrame> EncodeStreamAsync(
            IAsyncEnumerable<AudioFrame> inputStream,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in inputStream.WithCancellation(ct))
                yield return await EncodeAsync(frame, ct);
        }

        public Task FlushAsync(CancellationToken ct = default) { _codec.Reset(); return Task.CompletedTask; }
        public void Dispose() => _codec.Dispose();
    }

    #endregion

    #region G.729 包装器

    internal sealed class G729DecoderWrapper : IAudioDecoder
    {
        private readonly G729Codec _codec = new();
        public string Name => _codec.Name;
        public bool IsHardwareAccelerated => false;
        public ProcessorState State => _codec.State;
        public AudioCodec[] SupportedCodecs => new[] { AudioCodec.G729 };

        public Task InitializeAsync(AudioDecoderConfig config, CancellationToken ct = default)
            => _codec.InitializeAsync(ct);

        public Task<AudioFrame> DecodeAsync(EncodedAudioFrame input, CancellationToken ct = default)
        {
            var pcm = _codec.Decode(input.Data.Span);
            return Task.FromResult(new AudioFrame
            {
                Data = pcm,
                SampleRate = 8000,
                Channels = 1,
                BitsPerSample = 16,
                Timestamp = input.Timestamp
            });
        }

        public async IAsyncEnumerable<AudioFrame> DecodeStreamAsync(
            IAsyncEnumerable<EncodedAudioFrame> inputStream,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in inputStream.WithCancellation(ct))
                yield return await DecodeAsync(frame, ct);
        }

        public Task FlushAsync(CancellationToken ct = default) { _codec.Reset(); return Task.CompletedTask; }
        public void Dispose() => _codec.Dispose();
    }

    internal sealed class G729EncoderWrapper : IAudioEncoder
    {
        private readonly G729Codec _codec = new();
        public string Name => _codec.Name;
        public bool IsHardwareAccelerated => false;
        public ProcessorState State => _codec.State;
        public AudioCodec[] SupportedCodecs => new[] { AudioCodec.G729 };

        public Task InitializeAsync(AudioEncoderConfig config, CancellationToken ct = default)
            => _codec.InitializeAsync(ct);

        public Task<EncodedAudioFrame> EncodeAsync(AudioFrame input, CancellationToken ct = default)
        {
            var encoded = _codec.Encode(input.Data.Span);
            return Task.FromResult(new EncodedAudioFrame
            {
                Data = encoded,
                Codec = AudioCodec.G729,
                SampleRate = 8000,
                Channels = 1,
                Timestamp = input.Timestamp,
                Duration = 10000
            });
        }

        public async IAsyncEnumerable<EncodedAudioFrame> EncodeStreamAsync(
            IAsyncEnumerable<AudioFrame> inputStream,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in inputStream.WithCancellation(ct))
                yield return await EncodeAsync(frame, ct);
        }

        public Task FlushAsync(CancellationToken ct = default) { _codec.Reset(); return Task.CompletedTask; }
        public void Dispose() => _codec.Dispose();
    }

    #endregion

    #region AAC 包装器

    internal sealed class AacDecoderWrapper : IAudioDecoder
    {
        private readonly AacDecoderImpl _impl = new();
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
                yield return await _impl.DecodeAsync(frame, ct);
        }

        public Task FlushAsync(CancellationToken ct = default) => _impl.FlushAsync(ct);
        public void Dispose() => _impl.Dispose();
    }

    internal sealed class AacEncoderWrapper : IAudioEncoder
    {
        private readonly AacEncoderImpl _impl = new();
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
                yield return await _impl.EncodeAsync(frame, ct);
        }

        public Task FlushAsync(CancellationToken ct = default) => _impl.FlushAsync(ct);
        public void Dispose() => _impl.Dispose();
    }

    #endregion

    #region 存根 (需要外部实现)

    internal sealed class StubDecoder : IAudioDecoder
    {
        public string Name { get; }
        public bool IsHardwareAccelerated => false;
        public ProcessorState State { get; private set; } = ProcessorState.Idle;
        public AudioCodec[] SupportedCodecs => Array.Empty<AudioCodec>();

        public StubDecoder(string name) => Name = name;

        public Task InitializeAsync(AudioDecoderConfig config, CancellationToken ct = default)
        {
            State = ProcessorState.Ready;
            return Task.CompletedTask;
        }

        public Task<AudioFrame> DecodeAsync(EncodedAudioFrame input, CancellationToken ct = default)
            => throw new NotImplementedException($"{Name} decoder not implemented. Install the appropriate codec plugin.");

        public async IAsyncEnumerable<AudioFrame> DecodeStreamAsync(
            IAsyncEnumerable<EncodedAudioFrame> inputStream,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in inputStream.WithCancellation(ct))
                yield return await DecodeAsync(frame, ct);
        }

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => State = ProcessorState.Disposed;
    }

    internal sealed class StubEncoder : IAudioEncoder
    {
        public string Name { get; }
        public bool IsHardwareAccelerated => false;
        public ProcessorState State { get; private set; } = ProcessorState.Idle;
        public AudioCodec[] SupportedCodecs => Array.Empty<AudioCodec>();

        public StubEncoder(string name) => Name = name;

        public Task InitializeAsync(AudioEncoderConfig config, CancellationToken ct = default)
        {
            State = ProcessorState.Ready;
            return Task.CompletedTask;
        }

        public Task<EncodedAudioFrame> EncodeAsync(AudioFrame input, CancellationToken ct = default)
            => throw new NotImplementedException($"{Name} encoder not implemented. Install the appropriate codec plugin.");

        public async IAsyncEnumerable<EncodedAudioFrame> EncodeStreamAsync(
            IAsyncEnumerable<AudioFrame> inputStream,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in inputStream.WithCancellation(ct))
                yield return await EncodeAsync(frame, ct);
        }

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => State = ProcessorState.Disposed;
    }

    #endregion
}
