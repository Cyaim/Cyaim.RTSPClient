namespace Cyaim.RTSPServer.Media;

/// <summary>
/// 合规的最小 H.264 测试流生成器（Baseline Profile, 320x240, 彩条图案）。
///
/// - IDR 帧使用 I_PCM 宏块：无需熵编码残差，任何标准解码器都能解码
/// - P 帧使用全跳过宏块（mb_skip_run 覆盖整帧），画面保持不变
/// - 所有 NAL 均做 emulation prevention（0x000000/01/02/03 → 插入 0x03）
///
/// 旧实现的 SPS 将 profile_idc/level_idc 写成了指数哥伦布编码（应为固定 8 位），
/// 且切片头缺少 dec_ref_pic_marking / slice_qp_delta 等字段，
/// ffmpeg/VLC 报 "sps_id 0 out of range" 无法解码。
/// </summary>
public static class H264TestStream
{
    /// <summary>宏块列数（320/16）</summary>
    public const int WidthMbs = 20;

    /// <summary>宏块行数（240/16）</summary>
    public const int HeightMbs = 15;

    private const int TotalMbs = WidthMbs * HeightMbs;

    private static readonly Lazy<byte[]> _sps = new(BuildSps);
    private static readonly Lazy<byte[]> _pps = new(BuildPps);
    private static readonly Lazy<byte[]> _idrFrame = new(BuildIdrFrame);

    /// <summary>SPS NAL（不含起始码）</summary>
    public static byte[] Sps => _sps.Value;

    /// <summary>PPS NAL（不含起始码）</summary>
    public static byte[] Pps => _pps.Value;

    /// <summary>IDR 帧 NAL（I_PCM 彩条，约 115KB，内容固定可缓存复用）</summary>
    public static byte[] IdrFrame => _idrFrame.Value;

    /// <summary>
    /// 生成 P 帧 NAL（全跳过宏块，画面不变）
    /// </summary>
    /// <param name="frameNum">帧号（参考帧计数，SPS 中 log2_max_frame_num=4，调用方按 16 取模）</param>
    public static byte[] BuildPFrame(int frameNum)
    {
        var w = new BitWriter();

        // NAL header: nal_ref_idc=2, type=1 (non-IDR slice)
        w.WriteBits(0, 1);
        w.WriteBits(2, 2);
        w.WriteBits(1, 5);

        // Slice header
        w.WriteExpGolomb(0);                    // first_mb_in_slice
        w.WriteExpGolomb(5);                    // slice_type = 5 (P, all slices)
        w.WriteExpGolomb(0);                    // pic_parameter_set_id
        w.WriteBits(frameNum & 0x0F, 4);        // frame_num (4 bits)
        // pic_order_cnt_type=2 → 无 POC 字段
        w.WriteBit(false);                      // num_ref_idx_active_override_flag
        w.WriteBit(false);                      // ref_pic_list_modification_flag_l0
        w.WriteBit(false);                      // adaptive_ref_pic_marking_mode_flag (dec_ref_pic_marking)
        w.WriteSignedExpGolomb(0);              // slice_qp_delta

        // Slice data: 全部宏块跳过
        w.WriteExpGolomb((uint)TotalMbs);       // mb_skip_run = 300

        WriteRbspTrailingBits(w);
        return AddEmulationPrevention(w.ToArray());
    }

    private static byte[] BuildSps()
    {
        var w = new BitWriter();

        // NAL header: nal_ref_idc=3, type=7 (SPS)
        w.WriteBits(0, 1);
        w.WriteBits(3, 2);
        w.WriteBits(7, 5);

        w.WriteBits(66, 8);        // profile_idc = 66 (Baseline)，固定 8 位
        w.WriteBit(true);          // constraint_set0_flag
        w.WriteBit(true);          // constraint_set1_flag
        w.WriteBit(false);         // constraint_set2_flag
        w.WriteBit(false);         // constraint_set3_flag
        w.WriteBits(0, 4);         // constraint_set4/5 + reserved_zero_2bits
        w.WriteBits(20, 8);        // level_idc = 20 (2.0)，固定 8 位

        w.WriteExpGolomb(0);       // seq_parameter_set_id
        w.WriteExpGolomb(0);       // log2_max_frame_num_minus4 → frame_num 4 位
        w.WriteExpGolomb(2);       // pic_order_cnt_type = 2（输出顺序=解码顺序，切片头无 POC）
        w.WriteExpGolomb(1);       // max_num_ref_frames = 1
        w.WriteBit(false);         // gaps_in_frame_num_value_allowed_flag
        w.WriteExpGolomb(WidthMbs - 1);   // pic_width_in_mbs_minus1
        w.WriteExpGolomb(HeightMbs - 1);  // pic_height_in_map_units_minus1
        w.WriteBit(true);          // frame_mbs_only_flag
        w.WriteBit(true);          // direct_8x8_inference_flag
        w.WriteBit(false);         // frame_cropping_flag
        w.WriteBit(false);         // vui_parameters_present_flag

        WriteRbspTrailingBits(w);
        return AddEmulationPrevention(w.ToArray());
    }

    private static byte[] BuildPps()
    {
        var w = new BitWriter();

        // NAL header: nal_ref_idc=3, type=8 (PPS)
        w.WriteBits(0, 1);
        w.WriteBits(3, 2);
        w.WriteBits(8, 5);

        w.WriteExpGolomb(0);       // pic_parameter_set_id
        w.WriteExpGolomb(0);       // seq_parameter_set_id
        w.WriteBit(false);         // entropy_coding_mode_flag (CAVLC)
        w.WriteBit(false);         // bottom_field_pic_order_in_frame_present_flag
        w.WriteExpGolomb(0);       // num_slice_groups_minus1
        w.WriteExpGolomb(0);       // num_ref_idx_l0_default_active_minus1
        w.WriteExpGolomb(0);       // num_ref_idx_l1_default_active_minus1
        w.WriteBit(false);         // weighted_pred_flag
        w.WriteBits(0, 2);         // weighted_bipred_idc
        w.WriteSignedExpGolomb(0); // pic_init_qp_minus26
        w.WriteSignedExpGolomb(0); // pic_init_qs_minus26
        w.WriteSignedExpGolomb(0); // chroma_qp_index_offset
        w.WriteBit(false);         // deblocking_filter_control_present_flag
        w.WriteBit(false);         // constrained_intra_pred_flag
        w.WriteBit(false);         // redundant_pic_cnt_present_flag

        WriteRbspTrailingBits(w);
        return AddEmulationPrevention(w.ToArray());
    }

    private static byte[] BuildIdrFrame()
    {
        // 75% 彩条的 YCbCr 值（白/黄/青/绿/品红/红/蓝），全部 > 3，避免仿真字节
        ReadOnlySpan<(byte y, byte cb, byte cr)> bars =
        [
            (180, 128, 128), // 白
            (162, 44, 142),  // 黄
            (131, 156, 44),  // 青
            (112, 72, 58),   // 绿
            (84, 184, 198),  // 品红
            (65, 100, 212),  // 红
            (35, 212, 114),  // 蓝
        ];

        var w = new BitWriter();

        // NAL header: nal_ref_idc=3, type=5 (IDR slice)
        w.WriteBits(0, 1);
        w.WriteBits(3, 2);
        w.WriteBits(5, 5);

        // Slice header
        w.WriteExpGolomb(0);       // first_mb_in_slice
        w.WriteExpGolomb(7);       // slice_type = 7 (I, all slices)
        w.WriteExpGolomb(0);       // pic_parameter_set_id
        w.WriteBits(0, 4);         // frame_num（IDR 必须为 0）
        w.WriteExpGolomb(0);       // idr_pic_id
        // pic_order_cnt_type=2 → 无 POC 字段
        w.WriteBit(false);         // no_output_of_prior_pics_flag (dec_ref_pic_marking)
        w.WriteBit(false);         // long_term_reference_flag
        w.WriteSignedExpGolomb(0); // slice_qp_delta

        // Slice data: 300 个 I_PCM 宏块
        for (int mb = 0; mb < TotalMbs; mb++)
        {
            int mbX = mb % WidthMbs;
            var (y, cb, cr) = bars[mbX * bars.Length / WidthMbs];

            w.WriteExpGolomb(25);  // mb_type = 25 (I_PCM)
            w.AlignToByte();       // pcm_alignment_zero_bit(s)

            for (int i = 0; i < 256; i++) w.WriteBits(y, 8);   // pcm_sample_luma
            for (int i = 0; i < 64; i++) w.WriteBits(cb, 8);   // pcm_sample_chroma (Cb)
            for (int i = 0; i < 64; i++) w.WriteBits(cr, 8);   // pcm_sample_chroma (Cr)
        }

        WriteRbspTrailingBits(w);
        return AddEmulationPrevention(w.ToArray());
    }

    /// <summary>
    /// RBSP 结尾：stop bit(1) + 补零对齐
    /// </summary>
    private static void WriteRbspTrailingBits(BitWriter w)
    {
        w.WriteBit(true);
        w.AlignToByte();
        w.Flush();
    }

    /// <summary>
    /// 插入 emulation prevention 字节：0x000000/01/02/03 → 0x000003xx
    /// </summary>
    private static byte[] AddEmulationPrevention(byte[] rbsp)
    {
        var result = new List<byte>(rbsp.Length + 16);
        int zeroCount = 0;

        foreach (byte b in rbsp)
        {
            if (zeroCount == 2 && b <= 3)
            {
                result.Add(0x03);
                zeroCount = 0;
            }

            result.Add(b);
            zeroCount = b == 0 ? zeroCount + 1 : 0;
        }

        return result.ToArray();
    }

    /// <summary>
    /// 将 NAL 打包为 RTP 包（单包或 FU-A 分片，PT=96）。
    /// I_PCM 关键帧超过 100KB，必须分片（interleaved 帧长上限 65535）。
    /// </summary>
    /// <returns>包数据与对应序列号的列表，序列号自 startSeq 递增</returns>
    public static List<(byte[] data, ushort seq)> Packetize(byte[] nalData, ushort startSeq, uint timestamp, bool isMarker)
    {
        const int rtpHeaderSize = 12;
        const int maxPayloadSize = 1400;
        var packets = new List<(byte[] data, ushort seq)>();
        ushort seq = startSeq;

        if (nalData.Length <= maxPayloadSize)
        {
            var packet = new byte[rtpHeaderSize + nalData.Length];
            WriteRtpHeader(packet, seq, timestamp, isMarker);
            Array.Copy(nalData, 0, packet, rtpHeaderSize, nalData.Length);
            packets.Add((packet, seq));
        }
        else
        {
            byte nalHeader = nalData[0];
            byte fuIndicator = (byte)((nalHeader & 0xE0) | 28); // F+NRI 保留, type=28 (FU-A)
            byte nalType = (byte)(nalHeader & 0x1F);

            int offset = 1;
            int remaining = nalData.Length - 1;
            bool firstFragment = true;

            while (remaining > 0)
            {
                int chunkSize = Math.Min(remaining, maxPayloadSize - 2);
                bool lastFragment = remaining <= maxPayloadSize - 2;

                byte fuHeader = nalType;
                if (firstFragment) fuHeader |= 0x80;
                if (lastFragment) fuHeader |= 0x40;

                var packet = new byte[rtpHeaderSize + 2 + chunkSize];
                WriteRtpHeader(packet, seq, timestamp, lastFragment && isMarker);
                packet[rtpHeaderSize] = fuIndicator;
                packet[rtpHeaderSize + 1] = fuHeader;
                Array.Copy(nalData, offset, packet, rtpHeaderSize + 2, chunkSize);

                packets.Add((packet, seq));
                seq++;
                offset += chunkSize;
                remaining -= chunkSize;
                firstFragment = false;
            }
        }

        return packets;
    }

    private static void WriteRtpHeader(byte[] packet, ushort sequenceNumber, uint timestamp, bool isMarker)
    {
        packet[0] = 0x80;
        packet[1] = (byte)((isMarker ? 0x80 : 0x00) | 96);
        packet[2] = (byte)(sequenceNumber >> 8);
        packet[3] = (byte)(sequenceNumber & 0xFF);
        packet[4] = (byte)(timestamp >> 24);
        packet[5] = (byte)((timestamp >> 16) & 0xFF);
        packet[6] = (byte)((timestamp >> 8) & 0xFF);
        packet[7] = (byte)(timestamp & 0xFF);
        packet[8] = 0x12;
        packet[9] = 0x34;
        packet[10] = 0x56;
        packet[11] = 0x78;
    }
}
