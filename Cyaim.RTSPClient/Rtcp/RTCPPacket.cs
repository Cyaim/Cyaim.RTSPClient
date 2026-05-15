using System;

namespace Cyaim.RTSPClient.Rtcp
{
    /// <summary>
    /// RTCP包基类 (RFC 3550)
    /// </summary>
    public abstract class RTCPPacket
    {
        /// <summary>
        /// RTCP包类型
        /// </summary>
        public byte PacketType { get; protected set; }

        /// <summary>
        /// SSRC/CSRC标识
        /// </summary>
        public uint Ssrc { get; set; }

        /// <summary>
        /// 序列化为字节数组
        /// </summary>
        public abstract byte[] Serialize();

        /// <summary>
        /// 从字节数组解析RTCP包
        /// </summary>
        public static RTCPPacket? Parse(byte[] data)
        {
            if (data is null || data.Length < 8)
                throw new ArgumentException("RTCP packet too short");

            byte version = (byte)((data[0] >> 6) & 0x03);
            if (version != 2)
                throw new ArgumentException($"Unsupported RTCP version: {version}");

            byte packetType = data[1];

            switch (packetType)
            {
                case 200:
                    return SenderReport.Parse(data);
                case 201:
                    return ReceiverReport.Parse(data);
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// RTCP Sender Report (PT=200)
    /// </summary>
    public class SenderReport : RTCPPacket
    {
        public SenderReport()
        {
            PacketType = 200;
        }

        /// <summary>
        /// NTP时间戳 (64-bit)
        /// </summary>
        public long NtpTimestamp { get; set; }

        /// <summary>
        /// RTP时间戳
        /// </summary>
        public uint RtpTimestamp { get; set; }

        /// <summary>
        /// 发送的包总数
        /// </summary>
        public uint SenderPacketCount { get; set; }

        /// <summary>
        /// 发送的字节总数
        /// </summary>
        public uint SenderOctetCount { get; set; }

        public override byte[] Serialize()
        {
            // SR格式: V=2, P=0, RC=0, PT=200, Length=6
            // + SSRC + NTP timestamp (64) + RTP timestamp (32) + packet count (32) + octet count (32)
            byte[] data = new byte[28];

            // Header
            data[0] = 0x80; // V=2, P=0, RC=0
            data[1] = 200;  // PT = SR
            data[2] = 0; data[3] = 6; // Length in 32-bit words minus 1

            // SSRC
            data[4] = (byte)(Ssrc >> 24);
            data[5] = (byte)(Ssrc >> 16);
            data[6] = (byte)(Ssrc >> 8);
            data[7] = (byte)Ssrc;

            // NTP timestamp (64-bit)
            for (int i = 0; i < 8; i++)
                data[8 + i] = (byte)(NtpTimestamp >> (56 - i * 8));

            // RTP timestamp
            data[16] = (byte)(RtpTimestamp >> 24);
            data[17] = (byte)(RtpTimestamp >> 16);
            data[18] = (byte)(RtpTimestamp >> 8);
            data[19] = (byte)RtpTimestamp;

            // Sender packet count
            data[20] = (byte)(SenderPacketCount >> 24);
            data[21] = (byte)(SenderPacketCount >> 16);
            data[22] = (byte)(SenderPacketCount >> 8);
            data[23] = (byte)SenderPacketCount;

            // Octet count
            data[24] = (byte)(SenderOctetCount >> 24);
            data[25] = (byte)(SenderOctetCount >> 16);
            data[26] = (byte)(SenderOctetCount >> 8);
            data[27] = (byte)SenderOctetCount;

            return data;
        }

        public static new SenderReport Parse(byte[] data)
        {
            var sr = new SenderReport();
            sr.Ssrc = (uint)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]);

            // Parse NTP timestamp
            sr.NtpTimestamp = 0;
            for (int i = 0; i < 8; i++)
                sr.NtpTimestamp = (sr.NtpTimestamp << 8) | data[8 + i];

            sr.RtpTimestamp = (uint)((data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19]);
            sr.SenderPacketCount = (uint)((data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23]);
            sr.SenderOctetCount = (uint)((data[24] << 24) | (data[25] << 16) | (data[26] << 8) | data[27]);

            return sr;
        }

        /// <summary>
        /// 获取NTP时间戳
        /// </summary>
        public static long GetNtpTimestamp()
        {
            DateTime ntpEpoch = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            TimeSpan span = DateTime.UtcNow - ntpEpoch;
            long seconds = (long)span.TotalSeconds;
            long fraction = (long)((span.TotalSeconds - seconds) * (1L << 32));
            return (seconds << 32) | fraction;
        }
    }

    /// <summary>
    /// RTCP Receiver Report (PT=201)
    /// </summary>
    public class ReceiverReport : RTCPPacket
    {
        public ReceiverReport()
        {
            PacketType = 201;
            Reports = new System.Collections.Generic.List<ReceptionReport>();
        }

        /// <summary>
        /// 接收报告列表
        /// </summary>
        public System.Collections.Generic.List<ReceptionReport> Reports { get; set; }

        public override byte[] Serialize()
        {
            int reportCount = Math.Min(Reports.Count, 31); // Max 31 reports
            int length = 1 + (reportCount * 6); // 1 word for SSRC + 6 words per report
            byte[] data = new byte[4 + 4 + (length * 4)];

            // Header
            data[0] = (byte)(0x80 | reportCount); // V=2, P=0, RC=reportCount
            data[1] = 201; // PT = RR
            data[2] = (byte)((length >> 8) & 0xFF);
            data[3] = (byte)(length & 0xFF);

            // SSRC
            data[4] = (byte)(Ssrc >> 24);
            data[5] = (byte)(Ssrc >> 16);
            data[6] = (byte)(Ssrc >> 8);
            data[7] = (byte)Ssrc;

            // Reports
            int offset = 8;
            for (int i = 0; i < reportCount; i++)
            {
                var report = Reports[i];
                byte[] reportData = report.Serialize();
                Array.Copy(reportData, 0, data, offset, reportData.Length);
                offset += reportData.Length;
            }

            return data;
        }

        public static new ReceiverReport Parse(byte[] data)
        {
            var rr = new ReceiverReport();
            rr.Ssrc = (uint)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]);

            int reportCount = data[0] & 0x1F;
            int offset = 8;

            for (int i = 0; i < reportCount && offset + 24 <= data.Length; i++)
            {
                var report = ReceptionReport.Parse(data, offset);
                rr.Reports.Add(report);
                offset += 24;
            }

            return rr;
        }
    }

    /// <summary>
    /// RTCP接收报告块
    /// </summary>
    public class ReceptionReport
    {
        /// <summary>
        /// 源SSRC
        /// </summary>
        public uint Ssrc { get; set; }

        /// <summary>
        /// 丢包率 (8-bit)
        /// </summary>
        public byte FractionLost { get; set; }

        /// <summary>
        /// 累计丢包数 (24-bit)
        /// </summary>
        public uint CumulativeLost { get; set; }

        /// <summary>
        /// 扩展最高序列号
        /// </summary>
        public uint ExtendedHighestSequence { get; set; }

        /// <summary>
        /// 到达间隔抖动
        /// </summary>
        public uint Jitter { get; set; }

        /// <summary>
        /// 最后SR时间戳
        /// </summary>
        public uint LastSrTimestamp { get; set; }

        /// <summary>
        /// 最后SR延迟
        /// </summary>
        public uint DelaySinceLastSr { get; set; }

        public byte[] Serialize()
        {
            byte[] data = new byte[24];

            data[0] = (byte)(Ssrc >> 24);
            data[1] = (byte)(Ssrc >> 16);
            data[2] = (byte)(Ssrc >> 8);
            data[3] = (byte)Ssrc;

            data[4] = FractionLost;
            data[5] = (byte)((CumulativeLost >> 16) & 0xFF);
            data[6] = (byte)((CumulativeLost >> 8) & 0xFF);
            data[7] = (byte)(CumulativeLost & 0xFF);

            data[8] = (byte)(ExtendedHighestSequence >> 24);
            data[9] = (byte)(ExtendedHighestSequence >> 16);
            data[10] = (byte)(ExtendedHighestSequence >> 8);
            data[11] = (byte)ExtendedHighestSequence;

            data[12] = (byte)(Jitter >> 24);
            data[13] = (byte)(Jitter >> 16);
            data[14] = (byte)(Jitter >> 8);
            data[15] = (byte)Jitter;

            data[16] = (byte)(LastSrTimestamp >> 24);
            data[17] = (byte)(LastSrTimestamp >> 16);
            data[18] = (byte)(LastSrTimestamp >> 8);
            data[19] = (byte)LastSrTimestamp;

            data[20] = (byte)(DelaySinceLastSr >> 24);
            data[21] = (byte)(DelaySinceLastSr >> 16);
            data[22] = (byte)(DelaySinceLastSr >> 8);
            data[23] = (byte)DelaySinceLastSr;

            return data;
        }

        public static ReceptionReport Parse(byte[] data, int offset)
        {
            return new ReceptionReport
            {
                Ssrc = (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]),
                FractionLost = data[offset + 4],
                CumulativeLost = (uint)((data[offset + 5] << 16) | (data[offset + 6] << 8) | data[offset + 7]),
                ExtendedHighestSequence = (uint)((data[offset + 8] << 24) | (data[offset + 9] << 16) | (data[offset + 10] << 8) | data[offset + 11]),
                Jitter = (uint)((data[offset + 12] << 24) | (data[offset + 13] << 16) | (data[offset + 14] << 8) | data[offset + 15]),
                LastSrTimestamp = (uint)((data[offset + 16] << 24) | (data[offset + 17] << 16) | (data[offset + 18] << 8) | data[offset + 19]),
                DelaySinceLastSr = (uint)((data[offset + 20] << 24) | (data[offset + 21] << 16) | (data[offset + 22] << 8) | data[offset + 23])
            };
        }
    }
}
