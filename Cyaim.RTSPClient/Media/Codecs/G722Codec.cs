using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Cyaim.RTSPClient.Media.Codecs
{
    /// <summary>
    /// G.722 宽带语音编解码器 (ITU-T G.722)
    /// 16kHz 采样率, 子带 ADPCM
    /// </summary>
    public sealed class G722Codec : IMediaProcessor
    {
        private ProcessorState _state = ProcessorState.Idle;
        private readonly G722Band _high = new();
        private readonly G722Band _low = new();

        public string Name => "G.722";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State => _state;

        public Task InitializeAsync(CancellationToken ct = default)
        {
            _high.Reset();
            _low.Reset();
            _state = ProcessorState.Ready;
            return Task.CompletedTask;
        }

        /// <summary>解码 G.726 -> PCM 16-bit</summary>
        public byte[] Decode(ReadOnlySpan<byte> encoded)
        {
            var pcm = new byte[encoded.Length * 4]; // 2 samples/frame, 16-bit each
            int idx = 0;

            for (int i = 0; i < encoded.Length; i++)
            {
                int b = encoded[i];
                int highSamp = DecodeBand(_high, (b >> 6) & 0x03, 2);
                int lowSamp = DecodeBand(_low, b & 0x3F, 6);
                int sample = Math.Clamp(highSamp + lowSamp, -32768, 32767);

                if (idx + 1 < pcm.Length)
                {
                    pcm[idx++] = (byte)(sample & 0xFF);
                    pcm[idx++] = (byte)((sample >> 8) & 0xFF);
                }
            }

            return pcm;
        }

        /// <summary>编码 PCM 16-bit -> G.722</summary>
        public byte[] Encode(ReadOnlySpan<byte> pcm)
        {
            int sampleCount = pcm.Length / 2;
            var encoded = new byte[(sampleCount + 1) / 2];
            int idx = 0;

            for (int i = 0; i < encoded.Length; i++)
            {
                int s1 = 0, s2 = 0;
                if (idx + 1 < pcm.Length) s1 = (short)(pcm[idx] | (pcm[idx + 1] << 8));
                idx += 2;
                if (idx + 1 < pcm.Length) s2 = (short)(pcm[idx] | (pcm[idx + 1] << 8));
                idx += 2;

                int highBits = EncodeBand(_high, s1, 2);
                int lowBits = EncodeBand(_low, s1, 6);
                encoded[i] = (byte)((highBits << 6) | (lowBits & 0x3F));
            }

            return encoded;
        }

        public void Reset() { _high.Reset(); _low.Reset(); }
        public void Dispose() => _state = ProcessorState.Disposed;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int DecodeBand(G722Band band, int code, int bits)
        {
            int step = Steps[band.StepIdx];
            int delta = (code * step) >> (bits - 1);
            int sample = Math.Clamp(band.Prev + delta, -32768, 32767);
            band.Prev = sample;
            band.StepIdx = Math.Clamp(band.StepIdx + Adj[code & 0x03], 0, 31);
            return sample;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int EncodeBand(G722Band band, int sample, int bits)
        {
            int diff = sample - band.Prev;
            int step = Steps[band.StepIdx];
            int code = Math.Clamp((diff << (bits - 1)) / step, 0, (1 << bits) - 1);
            int delta = (code * step) >> (bits - 1);
            band.Prev = Math.Clamp(band.Prev + delta, -32768, 32767);
            band.StepIdx = Math.Clamp(band.StepIdx + Adj[code & 0x03], 0, 31);
            return code;
        }

        private static readonly int[] Steps = { 16,17,19,21,23,25,28,31,34,37,41,45,50,55,60,66,
            73,80,88,97,107,118,130,143,157,173,190,209,230,253,279,307 };
        private static readonly int[] Adj = { -1, -1, -1, 1 };

        private class G722Band { public int Prev, StepIdx; public void Reset() { Prev = 0; StepIdx = 0; } }
    }
}
