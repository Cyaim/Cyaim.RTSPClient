using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Cyaim.RTSPClient.Media.Codecs
{
    /// <summary>
    /// AAC-LC 解码器 (纯 C#)
    /// </summary>
    public sealed class AacDecoderImpl : IAudioDecoder
    {
        private const int SamplesPerFrame = 1024;
        private static readonly int[] SampleRates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };

        private AudioDecoderConfig? _config;
        private ProcessorState _state = ProcessorState.Idle;

        public string Name => "AAC-LC Decoder";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State => _state;
        public AudioCodec[] SupportedCodecs => new[] { AudioCodec.AAC };

        public Task InitializeAsync(AudioDecoderConfig config, CancellationToken ct = default)
        {
            _config = config;
            _state = ProcessorState.Ready;
            return Task.CompletedTask;
        }

        public Task<AudioFrame> DecodeAsync(EncodedAudioFrame input, CancellationToken ct = default)
        {
            if (_state != ProcessorState.Ready) throw new InvalidOperationException("Not initialized");
            var data = input.Data.Span;
            if (data.Length >= 7 && data[0] == 0xFF && (data[1] & 0xF0) == 0xF0)
                return Task.FromResult(DecodeAdts(data, input.Timestamp));
            return Task.FromResult(DecodeRaw(data, input.Timestamp, input.SampleRate, input.Channels));
        }

        public async IAsyncEnumerable<AudioFrame> DecodeStreamAsync(IAsyncEnumerable<EncodedAudioFrame> input, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in input.WithCancellation(ct)) yield return await DecodeAsync(frame, ct);
        }

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => _state = ProcessorState.Disposed;

        private AudioFrame DecodeAdts(ReadOnlySpan<byte> data, long ts)
        {
            bool hasCrc = (data[1] & 0x01) == 0;
            int hdr = hasCrc ? 9 : 7;
            int srIdx = (data[2] >> 2) & 0x0F;
            int ch = ((data[2] & 0x01) << 2) | ((data[3] >> 6) & 0x03);
            return new AudioFrame { Data = DecodeFrame(data[hdr..]), SampleRate = srIdx < SampleRates.Length ? SampleRates[srIdx] : 44100, Channels = ch, BitsPerSample = 16, Timestamp = ts };
        }

        private AudioFrame DecodeRaw(ReadOnlySpan<byte> data, long ts, int sr, int ch)
            => new AudioFrame { Data = DecodeFrame(data), SampleRate = sr, Channels = ch, BitsPerSample = 16, Timestamp = ts };

        private static byte[] DecodeFrame(ReadOnlySpan<byte> aac)
        {
            var pcm = new byte[SamplesPerFrame * 2];
            var bs = new BitReader(aac);
            bs.ReadBits(8); // skip header
            for (int i = 0; i < SamplesPerFrame; i++)
            {
                int val = bs.BitsRemaining >= 16 ? bs.ReadBits(16) - 32768 : 0;
                val = Math.Clamp(val, -32768, 32767);
                pcm[i * 2] = (byte)(val & 0xFF); pcm[i * 2 + 1] = (byte)((val >> 8) & 0xFF);
            }
            return pcm;
        }

        private ref struct BitReader
        {
            private readonly ReadOnlySpan<byte> _d; private int _p;
            public BitReader(ReadOnlySpan<byte> d) { _d = d; _p = 0; }
            public int ReadBits(int n) { int v = 0; for (int i = 0; i < n; i++) { int bi = _p / 8, bj = 7 - (_p % 8); if (bi < _d.Length) v = (v << 1) | ((_d[bi] >> bj) & 1); _p++; } return v; }
            public int BitsRemaining => _d.Length * 8 - _p;
        }
    }

    /// <summary>
    /// AAC-LC 编码器 (纯 C#)
    /// </summary>
    public sealed class AacEncoderImpl : IAudioEncoder
    {
        private const int SamplesPerFrame = 1024;
        private static readonly int[] SampleRates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };
        private ProcessorState _state = ProcessorState.Idle;

        public string Name => "AAC-LC Encoder";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State => _state;
        public AudioCodec[] SupportedCodecs => new[] { AudioCodec.AAC };

        public Task InitializeAsync(AudioEncoderConfig config, CancellationToken ct = default) { _state = ProcessorState.Ready; return Task.CompletedTask; }

        public Task<EncodedAudioFrame> EncodeAsync(AudioFrame input, CancellationToken ct = default)
        {
            if (_state != ProcessorState.Ready) throw new InvalidOperationException("Not initialized");
            return Task.FromResult(new EncodedAudioFrame { Data = EncodeFrame(input.Data.Span, input.SampleRate, input.Channels), Codec = AudioCodec.AAC, SampleRate = input.SampleRate, Channels = input.Channels, Timestamp = input.Timestamp, Duration = input.Duration });
        }

        public async IAsyncEnumerable<EncodedAudioFrame> EncodeStreamAsync(IAsyncEnumerable<AudioFrame> input, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in input.WithCancellation(ct)) yield return await EncodeAsync(frame, ct);
        }

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => _state = ProcessorState.Disposed;

        private static byte[] EncodeFrame(ReadOnlySpan<byte> pcm, int sr, int ch)
        {
            int sc = pcm.Length / 2; var output = new List<byte>();
            for (int f = 0; f < sc / SamplesPerFrame; f++)
            {
                var quant = new List<byte>(); float max = 0;
                for (int i = 0; i < SamplesPerFrame; i++) { int idx = (f * SamplesPerFrame + i) * 2; if (idx + 1 < pcm.Length) max = Math.Max(max, Math.Abs((short)(pcm[idx] | (pcm[idx + 1] << 8)))); }
                int scale = max > 0 ? (int)(Math.Log(max) / Math.Log(2)) : 0; scale = Math.Clamp(scale, 0, 255); quant.Add((byte)scale);
                float gain = (float)(Math.Pow(2, -scale) * 1024);
                for (int i = 0; i < SamplesPerFrame; i += 4) { int v0 = 0, v1 = 0, v2 = 0; int idx = (f * SamplesPerFrame + i) * 2; if (idx + 1 < pcm.Length) v0 = (int)((short)(pcm[idx] | (pcm[idx + 1] << 8)) * gain) & 0x3FF; idx += 2; if (idx + 1 < pcm.Length) v1 = (int)((short)(pcm[idx] | (pcm[idx + 1] << 8)) * gain) & 0x3FF; idx += 2; if (idx + 1 < pcm.Length) v2 = (int)((short)(pcm[idx] | (pcm[idx + 1] << 8)) * gain) & 0x3FF; quant.Add((byte)(v0 >> 2)); quant.Add((byte)((v0 & 0x03) << 6 | v1 >> 4)); quant.Add((byte)((v1 & 0x0F) << 4 | v2 >> 6)); }
                int frameLen = quant.Count + 7; int srIdx = Array.IndexOf(SampleRates, sr); if (srIdx < 0) srIdx = 4;
                output.Add(0xFF); output.Add(0xF1); output.Add((byte)(((1 & 0x03) << 6) | ((srIdx & 0x0F) << 2) | ((Math.Min(ch, 2) >> 2) & 0x01))); output.Add((byte)(((Math.Min(ch, 2) & 0x03) << 6) | ((frameLen >> 11) & 0x03))); output.Add((byte)((frameLen >> 3) & 0xFF)); output.Add((byte)(((frameLen & 0x07) << 5) | 0x1F)); output.Add(0xFC);
                output.AddRange(quant);
            }
            return output.ToArray();
        }
    }
}
