using System;

namespace Cyaim.RTSPClient
{

    /// <summary>
    /// RTP Header (legacy struct - see Rtp/RTPPacket for new implementation)
    /// https://datatracker.ietf.org/doc/html/rfc3550#section-5.1
    /// </summary>
    public struct RTPHeader
    {
        /// <summary>
        /// Version (2 bits)
        /// </summary>
        public byte[] Version { get; set; }

        /// <summary>
        /// Padding flag
        /// </summary>
        public byte Padding { get; set; }

        /// <summary>
        /// Extension flag
        /// </summary>
        public byte Extension { get; set; }

        /// <summary>
        /// CSRC count (CC): 4 bits
        /// </summary>
        public byte[] CSRCCount { get; set; }

        /// <summary>
        /// Marker bit
        /// </summary>
        public byte Maker { get; set; }

        /// <summary>
        /// Payload type (7 bits)
        /// </summary>
        public byte[] PayloadType { get; set; }

        /// <summary>
        /// Sequence number (16 bits)
        /// </summary>
        public byte[] SequenceNumber { get; set; }

        /// <summary>
        /// Timestamp (32 bits)
        /// </summary>
        public byte[] Timestamp { get; set; }

        /// <summary>
        /// SSRC (32 bits)
        /// </summary>
        public byte[] SSRC { get; set; }

        /// <summary>
        /// CSRC list (0-15 items, 32 bits each)
        /// </summary>
        public byte[] CSRC { get; set; }
    }

}
