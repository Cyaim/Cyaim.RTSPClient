using System;

namespace Cyaim.RTSPClient.Rtp
{
    /// <summary>
    /// Represents a parsed RTP (Real-time Transport Protocol) packet per RFC 3550.
    /// This is a value type for zero-allocation parsing on the hot path.
    /// </summary>
    /// <remarks>
    /// <see cref="Raw"/> and <see cref="Payload"/> reference the same underlying byte array
    /// when parsed from <see cref="RTPPacketParser.Parse"/>. Callers must not mutate these arrays.
    /// </remarks>
    public readonly record struct RTPPacket
    {
        /// <summary>
        /// Constructs an <see cref="RTPPacket"/> with all fields.
        /// </summary>
        /// <param name="version">RTP version (must be 2 per RFC 3550).</param>
        /// <param name="padding">Whether padding bytes are present at the end of the payload.</param>
        /// <param name="extension">Whether a header extension follows the fixed header.</param>
        /// <param name="csrcCount">Number of CSRC identifiers (0-15).</param>
        /// <param name="marker">Marker bit, interpretation defined by the payload type profile.</param>
        /// <param name="payloadType">Payload type identifier (0-127).</param>
        /// <param name="sequenceNumber">Packet sequence number, incremented by one for each packet sent.</param>
        /// <param name="timestamp">Sampling instant of the first octet in the payload.</param>
        /// <param name="ssrc">Synchronization source identifier.</param>
        /// <param name="csrc">Contributing source identifiers (0-15 items).</param>
        /// <param name="payload">The media payload data after the RTP header.</param>
        /// <param name="trackId">Track identifier derived from the interleaved channel or SDP.</param>
        /// <param name="streamType">The stream type (Video, Audio, etc.).</param>
        /// <param name="raw">The original wire bytes for debugging.</param>
        public RTPPacket(
            byte version,
            bool padding,
            bool extension,
            byte csrcCount,
            bool marker,
            byte payloadType,
            ushort sequenceNumber,
            uint timestamp,
            uint ssrc,
            uint[] csrc,
            byte[] payload,
            int trackId,
            StreamType streamType,
            byte[] raw)
            : this(version, padding, extension, csrcCount, marker, payloadType, sequenceNumber, timestamp,
                   ssrc, csrc, new ArraySegment<byte>(payload ?? Array.Empty<byte>()), trackId, streamType, raw)
        {
        }

        /// <summary>
        /// 零拷贝构造：载荷以 <see cref="ArraySegment{T}"/> 形式直接切片原始包数据，
        /// 热路径上避免为每个包再分配/拷贝一次载荷数组。
        /// </summary>
        public RTPPacket(
            byte version,
            bool padding,
            bool extension,
            byte csrcCount,
            bool marker,
            byte payloadType,
            ushort sequenceNumber,
            uint timestamp,
            uint ssrc,
            uint[] csrc,
            ArraySegment<byte> payloadSegment,
            int trackId,
            StreamType streamType,
            byte[] raw)
        {
            Version = version;
            Padding = padding;
            Extension = extension;
            CsrcCount = csrcCount;
            Marker = marker;
            PayloadType = payloadType;
            SequenceNumber = sequenceNumber;
            Timestamp = timestamp;
            Ssrc = ssrc;
            Csrc = csrc ?? Array.Empty<uint>();
            PayloadSegment = payloadSegment;
            TrackId = trackId;
            StreamType = streamType;
            Raw = raw ?? Array.Empty<byte>();
        }

        /// <summary>
        /// RTP version. Must be 2 per RFC 3550 section 5.1.
        /// </summary>
        public byte Version { get; }

        /// <summary>
        /// If set, the packet contains padding octets at the end that are not part of the payload.
        /// The last octet of the padding contains a count of how many padding octets should be ignored.
        /// </summary>
        public bool Padding { get; }

        /// <summary>
        /// If set, the fixed header is followed by exactly one header extension.
        /// </summary>
        public bool Extension { get; }

        /// <summary>
        /// The number of CSRC identifiers that follow the fixed header (0-15).
        /// </summary>
        public byte CsrcCount { get; }

        /// <summary>
        /// The interpretation of the marker bit is defined by a profile specification.
        /// For video: marks the end of a frame boundary.
        /// For audio: marks the beginning of a talkspurt.
        /// </summary>
        public bool Marker { get; }

        /// <summary>
        /// Identifies the format of the RTP payload and determines its interpretation by the application.
        /// </summary>
        public byte PayloadType { get; }

        /// <summary>
        /// The sequence number is incremented by one for each RTP data packet sent and may be used
        /// by the receiver to detect packet loss and to restore packet sequence.
        /// </summary>
        public ushort SequenceNumber { get; }

        /// <summary>
        /// The timestamp reflects the sampling instant of the first octet in the RTP data packet.
        /// The sampling instant MUST be derived from a clock that increments monotonically and linearly
        /// in time to allow synchronization and jitter calculations.
        /// </summary>
        public uint Timestamp { get; }

        /// <summary>
        /// The SSRC field identifies the synchronization source.
        /// This identifier is chosen randomly with the intent that no two synchronization sources
        /// within the same RTP session will have the same SSRC.
        /// </summary>
        public uint Ssrc { get; }

        /// <summary>
        /// The list of CSRC identifiers (0-15 items) contributing to the payload in this packet.
        /// May be empty if <see cref="CsrcCount"/> is 0.
        /// </summary>
        public uint[] Csrc { get; }

        /// <summary>
        /// The media payload as a zero-copy slice into <see cref="Raw"/>.
        /// This is the preferred accessor on the hot path — no allocation, no copy.
        /// Does not include padding bytes if <see cref="Padding"/> is set.
        /// </summary>
        public ArraySegment<byte> PayloadSegment { get; }

        /// <summary>
        /// The media payload data extracted from the RTP packet (after header, extensions, and CSRC list).
        /// Does not include padding bytes if <see cref="Padding"/> is set.
        /// NOTE: when the packet was parsed zero-copy, accessing this property materializes a copy —
        /// prefer <see cref="PayloadSegment"/> in performance-sensitive code.
        /// </summary>
        public byte[] Payload
        {
            get
            {
                var segment = PayloadSegment;
                if (segment.Array == null || segment.Count == 0)
                    return Array.Empty<byte>();
                if (segment.Offset == 0 && segment.Count == segment.Array.Length)
                    return segment.Array;

                var copy = new byte[segment.Count];
                Buffer.BlockCopy(segment.Array, segment.Offset, copy, 0, segment.Count);
                return copy;
            }
        }

        /// <summary>
        /// Track identifier derived from the interleaved TCP channel or SDP media description.
        /// </summary>
        public int TrackId { get; }

        /// <summary>
        /// The stream type (Video, Audio, Application, Text) for this RTP packet.
        /// </summary>
        public StreamType StreamType { get; }

        /// <summary>
        /// The original raw wire bytes of the complete RTP packet for debugging and diagnostic purposes.
        /// </summary>
        public byte[] Raw { get; }

        /// <summary>
        /// Returns a human-readable summary of this RTP packet.
        /// </summary>
        public override string ToString()
        {
            return $"RTPPacket(V={Version}, PT={PayloadType}, Seq={SequenceNumber}, " +
                   $"TS={Timestamp}, SSRC=0x{Ssrc:X8}, M={Marker}, " +
                   $"PayloadLen={PayloadSegment.Count}, TrackId={TrackId}, " +
                   $"Type={StreamType})";
        }
    }
}
