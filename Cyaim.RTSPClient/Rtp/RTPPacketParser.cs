using System;
using Cyaim.RTSPClient.Exceptions;

namespace Cyaim.RTSPClient.Rtp
{
    /// <summary>
    /// Static parser for RTP packets per RFC 3550 section 5.1.
    /// Supports both raw UDP-style packets and TCP interleaved framing.
    /// </summary>
    public static class RTPPacketParser
    {
        /// <summary>
        /// Minimum RTP header size in bytes (fixed header with no CSRC entries).
        /// </summary>
        private const int MinHeaderSize = 12;

        /// <summary>
        /// The interleaved framing magic byte '$' (0x24) used in RTP-over-TCP.
        /// </summary>
        private const byte InterleavedMagic = 0x24;

        /// <summary>
        /// Interleaved header size: magic(1) + channel(1) + length(2) = 4 bytes.
        /// </summary>
        private const int InterleavedHeaderSize = 4;

        /// <summary>
        /// Parses a raw RTP packet from the given byte array.
        /// </summary>
        /// <param name="data">The raw RTP packet bytes (including the header).</param>
        /// <param name="trackId">
        /// Track identifier to assign to the parsed packet, typically derived from the SDP or interleaved channel.
        /// </param>
        /// <param name="streamType">The stream type (Video, Audio, etc.).</param>
        /// <returns>A parsed <see cref="RTPPacket"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="data"/> is null.</exception>
        /// <exception cref="RTPParseException">
        /// The data is too short, contains an invalid RTP version, or is otherwise malformed.
        /// </exception>
        public static RTPPacket Parse(byte[] data, int trackId, StreamType streamType)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (data.Length < MinHeaderSize)
                throw new RTPParseException(
                    $"RTP packet too short: expected at least {MinHeaderSize} bytes, got {data.Length}.");

            // Byte 0: V(2) | P(1) | X(1) | CC(4)
            byte b0 = data[0];
            byte version = (byte)((b0 >> 6) & 0x03);
            bool padding = (b0 & 0x20) != 0;
            bool extension = (b0 & 0x10) != 0;
            byte csrcCount = (byte)(b0 & 0x0F);

            if (version != 2)
                throw new RTPParseException(
                    $"Invalid RTP version: expected 2, got {version}.");

            // Byte 1: M(1) | PT(7)
            byte b1 = data[1];
            bool marker = (b1 & 0x80) != 0;
            byte payloadType = (byte)(b1 & 0x7F);

            // Bytes 2-3: Sequence Number (big-endian)
            ushort sequenceNumber = (ushort)((data[2] << 8) | data[3]);

            // Bytes 4-7: Timestamp (big-endian)
            uint timestamp = (uint)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]);

            // Bytes 8-11: SSRC (big-endian)
            uint ssrc = (uint)((data[8] << 24) | (data[9] << 16) | (data[10] << 8) | data[11]);

            // CSRC list: csrcCount * 4 bytes starting at offset 12
            int headerEnd = MinHeaderSize + (csrcCount * 4);

            if (data.Length < headerEnd)
                throw new RTPParseException(
                    $"RTP packet too short for {csrcCount} CSRC entries: need {headerEnd} bytes, got {data.Length}.");

            uint[] csrc;
            if (csrcCount > 0)
            {
                csrc = new uint[csrcCount];
                int csrcOffset = MinHeaderSize;
                for (int i = 0; i < csrcCount; i++)
                {
                    csrc[i] = (uint)((data[csrcOffset] << 24) | (data[csrcOffset + 1] << 16) |
                                     (data[csrcOffset + 2] << 8) | data[csrcOffset + 3]);
                    csrcOffset += 4;
                }
            }
            else
            {
                csrc = Array.Empty<uint>();
            }

            // Extension header (if X bit is set): RFC 3550 section 5.3.1
            // 2 bytes: defined by profile | 2 bytes: extension length (in 32-bit words)
            int payloadOffset = headerEnd;
            if (extension)
            {
                if (data.Length < payloadOffset + 4)
                    throw new RTPParseException(
                        $"RTP packet too short for extension header: need at least {payloadOffset + 4} bytes, got {data.Length}.");

                // Bytes 0-1 of extension: profile-defined identifier (skip)
                // Bytes 2-3 of extension: length in 32-bit words
                int extensionLengthWords = (data[payloadOffset + 2] << 8) | data[payloadOffset + 3];
                int extensionByteLength = extensionLengthWords * 4;
                payloadOffset += 4 + extensionByteLength;

                if (data.Length < payloadOffset)
                    throw new RTPParseException(
                        $"RTP packet too short for extension data: expected {extensionByteLength} extension bytes, " +
                        $"need {payloadOffset} total, got {data.Length}.");
            }

            // Payload: everything from payloadOffset to end (minus padding if P bit is set)
            int payloadLength = data.Length - payloadOffset;

            if (padding)
            {
                if (payloadLength <= 0)
                    throw new RTPParseException(
                        "RTP packet has padding bit set but no payload to contain padding length.");

                // The last byte of the payload contains the number of padding octets
                int paddingLength = data[data.Length - 1];
                if (paddingLength <= 0 || paddingLength > payloadLength)
                    throw new RTPParseException(
                        $"Invalid RTP padding length: {paddingLength} (payload size: {payloadLength}).");

                payloadLength -= paddingLength;
            }

            if (payloadLength < 0)
                payloadLength = 0;

            // Extract payload into a new array
            byte[] payload;
            if (payloadLength > 0)
            {
                payload = new byte[payloadLength];
                Array.Copy(data, payloadOffset, payload, 0, payloadLength);
            }
            else
            {
                payload = Array.Empty<byte>();
            }

            return new RTPPacket(
                version: version,
                padding: padding,
                extension: extension,
                csrcCount: csrcCount,
                marker: marker,
                payloadType: payloadType,
                sequenceNumber: sequenceNumber,
                timestamp: timestamp,
                ssrc: ssrc,
                csrc: csrc,
                payload: payload,
                trackId: trackId,
                streamType: streamType,
                raw: data);
        }

        /// <summary>
        /// Attempts to parse an RTP packet from a TCP interleaved frame.
        /// The interleaved framing uses a 4-byte header: '$' (0x24), channel (1 byte), length (2 bytes big-endian).
        /// </summary>
        /// <param name="buffer">The receive buffer containing interleaved data.</param>
        /// <param name="offset">The starting offset within <paramref name="buffer"/>.</param>
        /// <param name="available">The number of bytes available in <paramref name="buffer"/> from <paramref name="offset"/>.</param>
        /// <param name="packet">When this method returns true, the parsed <see cref="RTPPacket"/>.</param>
        /// <param name="bytesConsumed">
        /// When this method returns true, the total number of bytes consumed from the buffer
        /// (including the 4-byte interleaved header). When false, 0.
        /// </param>
        /// <returns>
        /// <c>true</c> if a complete interleaved RTP frame was successfully parsed;
        /// <c>false</c> if the buffer does not start with the '$' magic byte or does not contain enough data.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="offset"/> or <paramref name="available"/> is negative, or exceeds the buffer bounds.
        /// </exception>
        public static bool TryParseInterleaved(
            byte[] buffer,
            int offset,
            int available,
            out RTPPacket packet,
            out int bytesConsumed)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || offset > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (available < 0 || offset + available > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(available));

            packet = default;
            bytesConsumed = 0;

            // Need at least 4 bytes for the interleaved header
            if (available < InterleavedHeaderSize)
                return false;

            // Check magic byte '$'
            if (buffer[offset] != InterleavedMagic)
                return false;

            byte channel = buffer[offset + 1];
            int dataLength = (buffer[offset + 2] << 8) | buffer[offset + 3];

            // Check if we have the complete frame
            int totalLength = InterleavedHeaderSize + dataLength;
            if (available < totalLength)
                return false;

            // Derive track ID and stream type from channel
            // Channel 0,2,4,... = video; 1,3,5,... = audio (convention)
            int trackId = channel / 2;
            StreamType streamType = (channel % 2 == 0) ? StreamType.Video : StreamType.Audio;

            // Parse the RTP payload within the interleaved frame
            byte[] rtpData = new byte[dataLength];
            Array.Copy(buffer, offset + InterleavedHeaderSize, rtpData, 0, dataLength);

            packet = Parse(rtpData, trackId, streamType);
            bytesConsumed = totalLength;
            return true;
        }
    }
}
