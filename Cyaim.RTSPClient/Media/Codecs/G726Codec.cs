using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Cyaim.RTSPClient.Media.Codecs
{
    /// <summary>
    /// G.726 ADPCM 语音编解码器 (ITU-T G.726)
    /// 8kHz 采样率, 支持 16/24/32/40 kbps
    /// </summary>
    public sealed class G726Codec : IMediaProcessor
    {
        public enum BitRate { Rate16 = 2, Rate24 = 3, Rate32 = 4, Rate40 = 5 }

        private ProcessorState _state = ProcessorState.Idle;
        private readonly int _bits;
        private readonly G726State _enc = new();
        private readonly G726State _dec = new();

        public string Name => $"G.726-{_bits * 8}";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State => _state;

        public G726Codec(BitRate rate = BitRate.Rate32) => _bits = (int)rate;

        public Task InitializeAsync(CancellationToken ct = default)
        {
            _enc.Reset(); _dec.Reset(); _state = ProcessorState.Ready;
            return Task.CompletedTask;
        }

        /// <summary>解码 G.726 -> PCM 16-bit</summary>
        public byte[] Decode(ReadOnlySpan<byte> encoded)
        {
            int spb = 8 / _bits;
            var pcm = new byte[encoded.Length * spb * 2];
            int idx = 0, mask = (1 << _bits) - 1;

            for (int i = 0; i < encoded.Length; i++)
            {
                int b = encoded[i];
                for (int j = spb - 1; j >= 0; j--)
                {
                    int code = (b >> (j * _bits)) & mask;
                    int s = DecSample(_dec, code, mask);
                    if (idx + 1 < pcm.Length) { pcm[idx++] = (byte)(s & 0xFF); pcm[idx++] = (byte)((s >> 8) & 0xFF); }
                }
            }
            return pcm;
        }

        /// <summary>编码 PCM 16-bit -> G.726</summary>
        public byte[] Encode(ReadOnlySpan<byte> pcm)
        {
            int spb = 8 / _bits, sc = pcm.Length / 2;
            var enc = new byte[(sc + spb - 1) / spb];
            int idx = 0, mask = (1 << _bits) - 1;

            for (int i = 0; i < enc.Length; i++)
            {
                int b = 0;
                for (int j = spb - 1; j >= 0; j--)
                {
                    int s = 0;
                    if (idx + 1 < pcm.Length) { s = (short)(pcm[idx] | (pcm[idx + 1] << 8)); idx += 2; }
                    int code = EncSample(_enc, s, mask);
                    b |= (code & mask) << (j * _bits);
                }
                enc[i] = (byte)b;
            }
            return enc;
        }

        public void Reset() { _enc.Reset(); _dec.Reset(); }
        public void Dispose() => _state = ProcessorState.Disposed;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int DecSample(G726State s, int code, int mask)
        {
            int step = Steps[s.Idx];
            int delta = ((code << 1) + 1) * step >> ((int)(Math.Log(mask + 1) / Math.Log(2)) + 1);
            int sample = Math.Clamp(s.Pred + delta, -32768, 32767);
            s.Pred = sample;
            s.Idx = Math.Clamp(s.Idx + Adj[code], 0, Steps.Length - 1);
            return sample;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int EncSample(G726State s, int sample, int mask)
        {
            int diff = sample - s.Pred, step = Steps[s.Idx];
            int bits = (int)(Math.Log(mask + 1) / Math.Log(2));
            int code = Math.Clamp(((diff << bits) / step + 1) >> 1, 0, mask);
            int delta = ((code << 1) + 1) * step >> (bits + 1);
            s.Pred = Math.Clamp(s.Pred + delta, -32768, 32767);
            s.Idx = Math.Clamp(s.Idx + Adj[code], 0, Steps.Length - 1);
            return code;
        }

        private static readonly int[] Steps = { 16,17,19,21,23,25,28,31,34,37,41,45,50,55,60,66,
            73,80,88,97,107,118,130,143,157,173,190,209,230,253,279,307,
            337,371,408,449,494,544,598,658,724,796,876,963,1060,1166,1282,1411,
            1552,1707,1878,2066,2272,2499,2749,3024,3327,3660,4026,4428,4871,5358,5894,6484 };
        private static readonly int[] Adj = { -1,-1,-1,-1,1,1,1,1,-1,-1,-1,-1,1,1,1,1,
            -1,-1,-1,-1,1,1,1,1,-1,-1,-1,-1,1,1,1,1 };

        private class G726State { public int Pred, Idx; public void Reset() { Pred = 0; Idx = 0; } }
    }
}
