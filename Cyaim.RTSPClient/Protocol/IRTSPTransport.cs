using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Cyaim.RTSPClient.Protocol
{
    /// <summary>
    /// Abstracts the underlying transport (TCP interleaved vs UDP).
    /// </summary>
    [Obsolete("RTSPSession 未使用此传输抽象（直接管理 TcpClient）。此类型将在后续版本移除。")]
    public interface IRTSPTransport : IDisposable
    {
        /// <summary>Gets the transport mode.</summary>
        TransportMode Mode { get; }

        /// <summary>Gets whether the transport is currently connected.</summary>
        bool IsConnected { get; }

        /// <summary>
        /// Send raw RTSP request bytes over the transport.
        /// </summary>
        /// <param name="data">The raw bytes to send.</param>
        /// <param name="ct">Cancellation token.</param>
        Task SendRequestAsync(byte[] data, CancellationToken ct);

        /// <summary>
        /// Send RTP data framed for the underlying transport (interleaved channel or UDP port).
        /// </summary>
        /// <param name="data">The RTP payload bytes.</param>
        /// <param name="channelId">The RTP channel identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        Task SendRtpAsync(byte[] data, byte channelId, CancellationToken ct);

        /// <summary>
        /// Starts the receive loop that reads incoming data from the transport
        /// and writes parsed RTP packets into the provided channel writer.
        /// </summary>
        /// <param name="writer">Channel writer to push received RTP packets into.</param>
        /// <param name="ct">Cancellation token to stop the receive loop.</param>
        Task StartReceiveLoopAsync(ChannelWriter<(byte[] Data, byte Channel)> writer, CancellationToken ct);

        /// <summary>
        /// Open the transport connection (connect TCP or bind UDP).
        /// </summary>
        /// <param name="uri">The target RTSP URI.</param>
        /// <param name="ct">Cancellation token.</param>
        Task OpenAsync(Uri uri, CancellationToken ct);

        /// <summary>
        /// Close the transport connection and release resources.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        Task CloseAsync(CancellationToken ct);
    }
}
