using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Cyaim.RTSPClient.Exceptions;

namespace Cyaim.RTSPClient.Protocol
{
    /// <summary>
    /// TCP interleaved transport. RTSP and RTP share the same TCP connection.
    /// RTSP requests are sent as text, RTP data is framed with $ + channel + length.
    /// </summary>
    public class RTSPTcpTransport : IRTSPTransport
    {
        private TcpClient? _client;
        private NetworkStream? _stream;
        private readonly int _connectTimeoutMs;
        private readonly int _readBufferSize = 65536;

        /// <inheritdoc />
        public TransportMode Mode => TransportMode.TcpInterleaved;

        /// <inheritdoc />
        public bool IsConnected => _client?.Connected ?? false;

        /// <summary>
        /// Creates a new TCP interleaved transport instance.
        /// </summary>
        /// <param name="connectTimeoutMs">Connection timeout in milliseconds. Defaults to 5000ms.</param>
        public RTSPTcpTransport(int connectTimeoutMs = 5000)
        {
            _connectTimeoutMs = connectTimeoutMs;
        }

        /// <inheritdoc />
        public async Task OpenAsync(Uri uri, CancellationToken ct)
        {
            _client = new TcpClient();

            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutCts.CancelAfter(_connectTimeoutMs);

                try
                {
                    await _client.ConnectAsync(uri.Host, uri.Port > 0 ? uri.Port : 554).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _client.Close();
                    throw new RTSPConnectionException("Connection timeout");
                }
                catch (Exception ex)
                {
                    _client.Close();
                    throw new RTSPConnectionException($"Failed to connect to {uri.Host}:{uri.Port}: {ex.Message}");
                }
            }

            _stream = _client.GetStream();
        }

        /// <inheritdoc />
        public async Task SendRequestAsync(byte[] data, CancellationToken ct)
        {
            if (_stream == null)
                throw new InvalidOperationException("Transport is not connected.");

            await _stream.WriteAsync(data, 0, data.Length, ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task SendRtpAsync(byte[] data, byte channelId, CancellationToken ct)
        {
            if (_stream == null)
                throw new InvalidOperationException("Transport is not connected.");

            // TCP interleaved framing: $ + channel + length(2 bytes, big-endian) + data
            byte[] framed = new byte[4 + data.Length];
            framed[0] = 0x24; // '$'
            framed[1] = channelId;
            framed[2] = (byte)((data.Length >> 8) & 0xFF);
            framed[3] = (byte)(data.Length & 0xFF);
            Array.Copy(data, 0, framed, 4, data.Length);

            await _stream.WriteAsync(framed, 0, framed.Length, ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task StartReceiveLoopAsync(ChannelWriter<(byte[] Data, byte Channel)> writer, CancellationToken ct)
        {
            if (_stream == null)
                throw new InvalidOperationException("Transport is not connected.");

            var buffer = new byte[_readBufferSize];
            int buffered = 0;

            try
            {
                while (!ct.IsCancellationRequested && IsConnected)
                {
                    int bytesRead = await _stream.ReadAsync(buffer, buffered, buffer.Length - buffered, ct).ConfigureAwait(false);
                    if (bytesRead == 0)
                        break; // Server closed connection

                    buffered += bytesRead;

                    // Parse interleaved frames from the buffer
                    int offset = 0;
                    while (offset < buffered)
                    {
                        // Check for '$' magic byte indicating an interleaved frame
                        if (buffer[offset] != 0x24)
                            break;

                        // Need at least 4 bytes for the frame header ($ + channel + length)
                        if (offset + 4 > buffered)
                            break;

                        byte channel = buffer[offset + 1];
                        int length = (buffer[offset + 2] << 8) | buffer[offset + 3];

                        // Need the full frame body
                        if (offset + 4 + length > buffered)
                            break;

                        // Extract and deliver the RTP data
                        byte[] rtpData = new byte[length];
                        Array.Copy(buffer, offset + 4, rtpData, 0, length);

                        await writer.WriteAsync((rtpData, channel), ct).ConfigureAwait(false);
                        offset += 4 + length;
                    }

                    // Compact the buffer: move unconsumed data to the front
                    if (offset > 0 && offset < buffered)
                    {
                        Buffer.BlockCopy(buffer, offset, buffer, 0, buffered - offset);
                        buffered -= offset;
                    }
                    else if (offset >= buffered)
                    {
                        buffered = 0;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
            catch (ObjectDisposedException)
            {
                // Stream was disposed during read
            }
            finally
            {
                writer.TryComplete();
            }
        }

        /// <inheritdoc />
        public Task CloseAsync(CancellationToken ct)
        {
            _stream?.Close();
            _client?.Close();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _stream?.Dispose();
            _client?.Dispose();
        }
    }
}
