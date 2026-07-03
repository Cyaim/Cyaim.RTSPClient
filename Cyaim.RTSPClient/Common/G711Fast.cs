using System;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif

namespace Cyaim.RTSPClient.Common
{
    /// <summary>
    /// 高性能 G.711 A-law/μ-law 编解码（ITU-T G.711 标准）。
    ///
    /// - 解码：256 项查表，单次内存读取每样本，全平台
    /// - 编码：x86 AVX2 下向量化（每次处理 16 个样本），其余平台走无分支查表标量路径
    ///
    /// 注意：旧的 G711Encoder.LinearToAlaw 在 [256,511] 区间将段位错误编码为 0（应为 1），
    /// 本实现按标准输出。
    /// </summary>
    public static class G711Fast
    {
        private const int AlawClip = 32635;
        private const int MulawClip = 32635;
        private const int MulawBias = 0x84;

        // ===== 解码查表 =====
        private static readonly short[] AlawDecodeTable = BuildAlawDecodeTable();
        private static readonly short[] MulawDecodeTable = BuildMulawDecodeTable();

        // 编码段查表：索引 = abs >> 8（A-law）/ biased >> 7 截断到 8 位（μ-law）
        private static readonly byte[] SegmentTable = BuildSegmentTable();

        #region 解码

        /// <summary>
        /// A-law → 16-bit PCM
        /// </summary>
        public static void DecodeALaw(ReadOnlySpan<byte> alaw, Span<short> pcm)
        {
            if (pcm.Length < alaw.Length)
                throw new ArgumentException("PCM buffer too small", nameof(pcm));

            var table = AlawDecodeTable;
            for (int i = 0; i < alaw.Length; i++)
            {
                pcm[i] = table[alaw[i]];
            }
        }

        /// <summary>
        /// μ-law → 16-bit PCM
        /// </summary>
        public static void DecodeMuLaw(ReadOnlySpan<byte> mulaw, Span<short> pcm)
        {
            if (pcm.Length < mulaw.Length)
                throw new ArgumentException("PCM buffer too small", nameof(pcm));

            var table = MulawDecodeTable;
            for (int i = 0; i < mulaw.Length; i++)
            {
                pcm[i] = table[mulaw[i]];
            }
        }

        /// <summary>
        /// 解码单个 A-law 样本
        /// </summary>
        public static short ALawToLinear(byte alaw) => AlawDecodeTable[alaw];

        /// <summary>
        /// 解码单个 μ-law 样本
        /// </summary>
        public static short MuLawToLinear(byte mulaw) => MulawDecodeTable[mulaw];

        #endregion

        #region 编码

        /// <summary>
        /// 16-bit PCM → A-law（AVX2 可用时向量化）
        /// </summary>
        public static void EncodeALaw(ReadOnlySpan<short> pcm, Span<byte> alaw)
        {
            if (alaw.Length < pcm.Length)
                throw new ArgumentException("Output buffer too small", nameof(alaw));

            int i = 0;
#if NET8_0_OR_GREATER
            if (Avx2.IsSupported && pcm.Length >= 16)
            {
                i = EncodeALawAvx2(pcm, alaw);
            }
#endif
            for (; i < pcm.Length; i++)
            {
                alaw[i] = LinearToALaw(pcm[i]);
            }
        }

        /// <summary>
        /// 16-bit PCM → μ-law（AVX2 可用时向量化）
        /// </summary>
        public static void EncodeMuLaw(ReadOnlySpan<short> pcm, Span<byte> mulaw)
        {
            if (mulaw.Length < pcm.Length)
                throw new ArgumentException("Output buffer too small", nameof(mulaw));

            int i = 0;
#if NET8_0_OR_GREATER
            if (Avx2.IsSupported && pcm.Length >= 16)
            {
                i = EncodeMuLawAvx2(pcm, mulaw);
            }
#endif
            for (; i < pcm.Length; i++)
            {
                mulaw[i] = LinearToMuLaw(pcm[i]);
            }
        }

        /// <summary>
        /// 编码单个样本为 A-law（标准 ITU-T G.711，无分支段查表）
        /// </summary>
        public static byte LinearToALaw(short pcm)
        {
            int sign = (pcm >> 8) & 0x80;               // 0x80 = 负数
            int abs = pcm < 0 ? (pcm == short.MinValue ? 32767 : -pcm) : pcm;
            if (abs > AlawClip) abs = AlawClip;

            int seg = SegmentTable[abs >> 8];
            int shift = seg == 0 ? 4 : seg + 3;
            int aval = (seg << 4) | ((abs >> shift) & 0x0F);
            return (byte)(aval ^ (sign ^ 0x55) ^ 0x80); // 等价于 aval ^ (正:0xD5 / 负:0x55)
        }

        /// <summary>
        /// 编码单个样本为 μ-law（标准 ITU-T G.711）
        /// </summary>
        public static byte LinearToMuLaw(short pcm)
        {
            int sign = pcm < 0 ? 0x80 : 0x00;
            int abs = pcm < 0 ? (pcm == short.MinValue ? 32767 : -pcm) : pcm;
            if (abs > MulawClip) abs = MulawClip;
            int biased = abs + MulawBias;

            // 段号：biased ∈ [2^(seg+8), 2^(seg+9)) → seg；biased < 256 → 0
            int seg = SegmentTable[biased >> 8];
            int mantissa = (biased >> (seg + 3)) & 0x0F;
            return (byte)(~(sign | (seg << 4) | mantissa));
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// AVX2 A-law 编码：每次迭代 16 个样本。返回已处理的样本数。
        /// </summary>
        private static unsafe int EncodeALawAvx2(ReadOnlySpan<short> pcm, Span<byte> output)
        {
            int vectorized = pcm.Length & ~15;
            var clip = Vector256.Create((ushort)AlawClip);
            var xorPos = Vector256.Create((short)0xD5);
            var signBit = Vector256.Create(unchecked((short)0x0080));

            fixed (short* pSrc = pcm)
            fixed (byte* pDst = output)
            {
                for (int i = 0; i < vectorized; i += 16)
                {
                    var v = Avx.LoadVector256(pSrc + i);

                    // sign = 全 1（负）/ 全 0（正）；abs 并按无符号 clamp 到 32635
                    var sign = Avx2.ShiftRightArithmetic(v, 15);
                    var abs = Avx2.Subtract(Avx2.Xor(v, sign), sign);
                    abs = Avx2.Min(abs.AsUInt16(), clip).AsInt16();

                    // seg = Σ (abs >= 256<<i), i=0..6（compare 结果为 -1，累加后取负）
                    var seg = Vector256<short>.Zero;
                    for (int t = 0; t < 7; t++)
                    {
                        var threshold = Vector256.Create((short)((256 << t) - 1));
                        seg = Avx2.Subtract(seg, Avx2.CompareGreaterThan(abs, threshold));
                    }

                    // shift = max(seg + 3, 4)
                    var shift = Avx2.Max(Avx2.Add(seg, Vector256.Create((short)3)), Vector256.Create((short)4));

                    // 16 位通道无逐元素变量移位，加宽到 32 位
                    var absLo = Avx2.ConvertToVector256Int32(abs.GetLower());
                    var absHi = Avx2.ConvertToVector256Int32(abs.GetUpper());
                    var shiftLo = Avx2.ConvertToVector256Int32(shift.GetLower());
                    var shiftHi = Avx2.ConvertToVector256Int32(shift.GetUpper());
                    var mantLo = Avx2.And(Avx2.ShiftRightLogicalVariable(absLo, shiftLo.AsUInt32()), Vector256.Create(0x0F));
                    var mantHi = Avx2.And(Avx2.ShiftRightLogicalVariable(absHi, shiftHi.AsUInt32()), Vector256.Create(0x0F));

                    // 打包回 16 位（PackSigned 按 128 位通道交织，Permute4x64 恢复顺序）
                    var mant = Avx2.Permute4x64(
                        Avx2.PackSignedSaturate(mantLo, mantHi).AsInt64(), 0b11011000).AsInt16();

                    // aval = seg<<4 | mant，再异或 0xD5（正）/ 0x55（负）
                    var aval = Avx2.Or(Avx2.ShiftLeftLogical(seg, 4), mant);
                    var xorMask = Avx2.Xor(xorPos, Avx2.And(sign, signBit));
                    var result = Avx2.Xor(aval, xorMask);

                    // 16×short(0..255) → 16×byte
                    var packed = Avx2.Permute4x64(
                        Avx2.PackUnsignedSaturate(result, result).AsInt64(), 0b11011000).AsByte();
                    Sse2.Store(pDst + i, packed.GetLower());
                }
            }

            return vectorized;
        }

        /// <summary>
        /// AVX2 μ-law 编码：每次迭代 16 个样本。返回已处理的样本数。
        /// </summary>
        private static unsafe int EncodeMuLawAvx2(ReadOnlySpan<short> pcm, Span<byte> output)
        {
            int vectorized = pcm.Length & ~15;
            var clip = Vector256.Create((ushort)MulawClip);
            var bias = Vector256.Create((short)MulawBias);

            fixed (short* pSrc = pcm)
            fixed (byte* pDst = output)
            {
                for (int i = 0; i < vectorized; i += 16)
                {
                    var v = Avx.LoadVector256(pSrc + i);

                    var sign = Avx2.ShiftRightArithmetic(v, 15);
                    var abs = Avx2.Subtract(Avx2.Xor(v, sign), sign);
                    abs = Avx2.Min(abs.AsUInt16(), clip).AsInt16();
                    var biased = Avx2.Add(abs, bias); // ≤ 32767，无溢出

                    // seg = Σ (biased >= 256<<i), i=0..6（与标量 SegmentTable[biased>>8] 等价）
                    var seg = Vector256<short>.Zero;
                    for (int t = 0; t < 7; t++)
                    {
                        var threshold = Vector256.Create((short)((256 << t) - 1));
                        seg = Avx2.Subtract(seg, Avx2.CompareGreaterThan(biased, threshold));
                    }

                    var shift = Avx2.Add(seg, Vector256.Create((short)3));

                    var biasedLo = Avx2.ConvertToVector256Int32(biased.GetLower());
                    var biasedHi = Avx2.ConvertToVector256Int32(biased.GetUpper());
                    var shiftLo = Avx2.ConvertToVector256Int32(shift.GetLower());
                    var shiftHi = Avx2.ConvertToVector256Int32(shift.GetUpper());
                    var mantLo = Avx2.And(Avx2.ShiftRightLogicalVariable(biasedLo, shiftLo.AsUInt32()), Vector256.Create(0x0F));
                    var mantHi = Avx2.And(Avx2.ShiftRightLogicalVariable(biasedHi, shiftHi.AsUInt32()), Vector256.Create(0x0F));
                    var mant = Avx2.Permute4x64(
                        Avx2.PackSignedSaturate(mantLo, mantHi).AsInt64(), 0b11011000).AsInt16();

                    // uval = ~(sign8 | seg<<4 | mant) —— sign8 = 0x80（负）
                    var sign8 = Avx2.And(sign, Vector256.Create(unchecked((short)0x0080)));
                    var uval = Avx2.Or(Avx2.Or(sign8, Avx2.ShiftLeftLogical(seg, 4)), mant);
                    var result = Avx2.And(Avx2.Xor(uval, Vector256.Create((short)-1)), Vector256.Create((short)0xFF));

                    var packed = Avx2.Permute4x64(
                        Avx2.PackUnsignedSaturate(result, result).AsInt64(), 0b11011000).AsByte();
                    Sse2.Store(pDst + i, packed.GetLower());
                }
            }

            return vectorized;
        }
#endif

        #endregion

        #region 表构建

        private static byte[] BuildSegmentTable()
        {
            // 索引 x → floor(log2(x)) + 1，x=0 → 0（即最高有效位位置 + 1）
            var table = new byte[256];
            for (int i = 1; i < 256; i++)
            {
                int seg = 0;
                int v = i;
                while (v > 0)
                {
                    seg++;
                    v >>= 1;
                }
                table[i] = (byte)seg;
            }
            return table;
        }

        private static short[] BuildAlawDecodeTable()
        {
            var table = new short[256];
            for (int i = 0; i < 256; i++)
            {
                int a = i ^ 0x55;
                int seg = (a & 0x70) >> 4;
                int mantissa = a & 0x0F;

                int value = seg == 0
                    ? (mantissa << 4) + 8
                    : ((mantissa << 4) + 0x108) << (seg - 1);

                table[i] = (short)((a & 0x80) != 0 ? value : -value);
            }
            return table;
        }

        private static short[] BuildMulawDecodeTable()
        {
            var table = new short[256];
            for (int i = 0; i < 256; i++)
            {
                int u = ~i & 0xFF;
                int seg = (u & 0x70) >> 4;
                int mantissa = u & 0x0F;
                int value = (((mantissa << 3) + MulawBias) << seg) - MulawBias;

                table[i] = (short)((u & 0x80) != 0 ? -value : value);
            }
            return table;
        }

        #endregion
    }
}
