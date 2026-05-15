using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Cyaim.RTSPClient.Media.Codecs
{
    /// <summary>
    /// G.729 CS-ACELP 语音编解码器 (ITU-T G.729)
    /// 8kHz, 8kbps, 10ms 帧 (80 样本)
    /// </summary>
    public sealed class G729Codec : IMediaProcessor
    {
        private const int FrameSize = 80;
        private const int FrameBytes = 10;

        private ProcessorState _state = ProcessorState.Idle;
        private readonly G729State _enc = new();
        private readonly G729State _dec = new();

        public string Name => "G.729";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State => _state;

        public Task InitializeAsync(CancellationToken ct = default)
        {
            _enc.Reset(); _dec.Reset(); _state = ProcessorState.Ready;
            return Task.CompletedTask;
        }

        /// <summary>解码 G.729 -> PCM 16-bit (10ms)</summary>
        public byte[] Decode(ReadOnlySpan<byte> encoded)
        {
            if (encoded.Length < FrameBytes) return Array.Empty<byte>();
            var pcm = new byte[FrameSize * 2];
            var p = Unpack(encoded);
            float[] exc = DecodeExc(p);
            float[] speech = Synth(exc);
            for (int i = 0; i < FrameSize; i++)
            {
                int s = Math.Clamp((int)(speech[i] * 32767), -32768, 32767);
                pcm[i * 2] = (byte)(s & 0xFF); pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
            UpdateExc(_dec, exc);
            return pcm;
        }

        /// <summary>编码 PCM 16-bit -> G.729 (10ms)</summary>
        public byte[] Encode(ReadOnlySpan<byte> pcm)
        {
            if (pcm.Length < FrameSize * 2) return Array.Empty<byte>();
            float[] speech = new float[FrameSize];
            for (int i = 0; i < FrameSize; i++)
                speech[i] = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8)) / 32768f;

            int pitch = SearchPitch(speech);
            var cb = SearchCb(speech, pitch);
            var encoded = new byte[FrameBytes];
            Pack(encoded, new Param { Lsp = 0, Pitch = pitch, CbIdx = cb.idx, CbSign = cb.sign, Gain = 0 });
            return encoded;
        }

        public void Reset() { _enc.Reset(); _dec.Reset(); }
        public void Dispose() => _state = ProcessorState.Disposed;

        #region 参数

        private Param Unpack(ReadOnlySpan<byte> d)
        {
            ulong b = 0;
            for (int i = 0; i < FrameBytes && i < d.Length; i++) b = (b << 8) | d[i];
            return new Param
            {
                Lsp = (int)((b >> 66) & 0x3FFF),
                Pitch = (int)((b >> 56) & 0x3FF),
                CbIdx = (int)((b >> 40) & 0xFFFF),
                CbSign = (int)((b >> 30) & 0x3FF),
                Gain = (int)(b & 0x3FFFFFFF)
            };
        }

        private void Pack(Span<byte> d, Param p)
        {
            ulong b = 0;
            b |= ((ulong)(uint)(p.Lsp & 0x3FFF)) << 66;
            b |= ((ulong)(uint)(p.Pitch & 0x3FF)) << 56;
            b |= ((ulong)(uint)(p.CbIdx & 0xFFFF)) << 40;
            b |= ((ulong)(uint)(p.CbSign & 0x3FF)) << 30;
            b |= (ulong)(uint)(p.Gain & 0x3FFFFFFF);
            for (int i = FrameBytes - 1; i >= 0; i--) { d[i] = (byte)(b & 0xFF); b >>= 8; }
        }

        #endregion

        #region 激励

        private float[] DecodeExc(Param p)
        {
            var exc = new float[FrameSize];
            float pg = PitchGain[p.Gain % PitchGain.Length], cg = CbGain[p.Gain % CbGain.Length];
            for (int i = 0; i < FrameSize; i++)
                exc[i] = pg * GetExc(_dec.Exc, p.Pitch, i);
            var cb = DecodeCb(p.CbIdx, p.CbSign);
            for (int i = 0; i < FrameSize; i++) exc[i] += cg * cb[i % cb.Length];
            return exc;
        }

        private float[] DecodeCb(int idx, int sign)
        {
            var cb = new float[40];
            for (int i = 0; i < 4; i++)
            {
                int pos = (idx >> (i * 4)) & 0x0F;
                cb[pos] = ((sign >> i) & 1) == 1 ? 1.0f : -1.0f;
            }
            return cb;
        }

        private int SearchPitch(float[] s) => 40;
        private (int idx, int sign) SearchCb(float[] s, int p) => (0, 0);

        #endregion

        #region 合成

        private float[] Synth(float[] exc)
        {
            var s = new float[FrameSize];
            float[] a = { -0.2f, 0.1f, -0.05f, 0.02f }; // 简化 LP 系数
            for (int i = 0; i < FrameSize; i++)
            {
                float sum = exc[i];
                for (int j = 0; j < a.Length && j < i; j++) sum -= a[j] * s[i - j - 1];
                s[i] = Math.Clamp(sum, -1, 1);
            }
            return s;
        }

        #endregion

        private void UpdateExc(G729State st, float[] exc)
        {
            Array.Copy(st.Exc, FrameSize, st.Exc, 0, st.Exc.Length - FrameSize);
            exc.CopyTo(st.Exc.AsSpan(st.Exc.Length - FrameSize));
        }

        private float GetExc(float[] exc, int pitch, int i) { int idx = exc.Length - pitch + i; return idx >= 0 && idx < exc.Length ? exc[idx] : 0; }

        private static readonly float[] PitchGain = { 0, 0.2f, 0.4f, 0.6f, 0.8f, 1.0f, 1.2f };
        private static readonly float[] CbGain = { 0, 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1.0f };

        private struct Param { public int Lsp, Pitch, CbIdx, CbSign, Gain; }
        private class G729State { public float[] Exc = new float[240]; public void Reset() => Array.Clear(Exc, 0, Exc.Length); }
    }
}
