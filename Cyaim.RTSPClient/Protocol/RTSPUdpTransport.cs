using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Cyaim.RTSPClient.Exceptions;

namespace Cyaim.RTSPClient.Protocol
{
    /// <summary>
    /// RTP over UDP传输实现
    /// RTSP信令使用TCP，RTP/RTCP数据使用UDP
    /// </summary>
    [Obsolete("RTSPSession 尚不支持 UDP 传输，此类型从未被接线且存在缺陷。此类型将在后续版本移除。")]
    public class RTSPUdpTransport : IRTSPTransport
    {
        private TcpClient? _tcpClient;
        private NetworkStream? _tcpStream;
        private UdpClient? _rtpSocket;
        private UdpClient? _rtcpSocket;
        private IPEndPoint? _remoteRtpEndpoint;
        private IPEndPoint? _remoteRtcpEndpoint;
        private readonly int _connectTimeoutMs;
        private bool _isConnected;

        /// <summary>
        /// 本地RTP端口
        /// </summary>
        public int LocalRtpPort { get; private set; }

        /// <summary>
        /// 本地RTCP端口
        /// </summary>
        public int LocalRtcpPort { get; private set; }

        /// <summary>
        /// 远程RTP端口
        /// </summary>
        public int RemoteRtpPort { get; private set; }

        /// <summary>
        /// 远程RTCP端口
        /// </summary>
        public int RemoteRtcpPort { get; private set; }

        public TransportMode Mode => TransportMode.UdpUnicast;
        public bool IsConnected => _isConnected && (_tcpClient?.Connected ?? false);

        public RTSPUdpTransport(int connectTimeoutMs = 5000)
        {
            _connectTimeoutMs = connectTimeoutMs;
        }

        /// <summary>
        /// 打开TCP连接并绑定UDP端口
        /// </summary>
        public async Task OpenAsync(Uri uri, CancellationToken ct)
        {
            // 1. 建立TCP连接用于RTSP信令
            _tcpClient = new TcpClient();
            var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(_connectTimeoutMs);

            try
            {
                await _tcpClient.ConnectAsync(uri.Host, uri.Port);
                _tcpStream = _tcpClient.GetStream();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new RTSPConnectionException("TCP connection timeout");
            }

            // 2. 绑定本地UDP端口 (RTP用偶数端口，RTCP用奇数端口)
            BindLocalUdpPorts();

            _isConnected = true;
        }

        /// <summary>
        /// 绑定本地UDP端口对
        /// </summary>
        private void BindLocalUdpPorts()
        {
            // 找一对可用的偶数/奇数端口
            int basePort = 10000; // 起始端口

            for (int port = basePort; port < 65000; port += 2)
            {
                try
                {
                    _rtpSocket = new UdpClient(port);
                    _rtcpSocket = new UdpClient(port + 1);

                    LocalRtpPort = port;
                    LocalRtcpPort = port + 1;
                    return;
                }
                catch (SocketException)
                {
                    // 端口被占用，尝试下一组
                    _rtpSocket?.Dispose();
                    _rtcpSocket?.Dispose();
                    continue;
                }
            }

            throw new RTSPTransportException("Failed to bind local UDP ports");
        }

        /// <summary>
        /// 设置远程RTP/RTCP端口 (从SETUP响应解析)
        /// </summary>
        public void SetRemotePorts(string host, int rtpPort, int rtcpPort)
        {
            _remoteRtpEndpoint = new IPEndPoint(IPAddress.Parse(host), rtpPort);
            _remoteRtcpEndpoint = new IPEndPoint(IPAddress.Parse(host), rtcpPort);
            RemoteRtpPort = rtpPort;
            RemoteRtcpPort = rtcpPort;
        }

        /// <summary>
        /// 发送RTSP请求 (通过TCP)
        /// </summary>
        public async Task SendRequestAsync(byte[] data, CancellationToken ct)
        {
            if (_tcpStream == null)
                throw new InvalidOperationException("TCP stream not connected");

            await _tcpStream.WriteAsync(data, 0, data.Length, ct);
        }

        /// <summary>
        /// 发送RTP数据 (通过UDP)
        /// </summary>
        public async Task SendRtpAsync(byte[] data, byte channelId, CancellationToken ct)
        {
            if (_rtpSocket == null || _remoteRtpEndpoint == null)
                throw new InvalidOperationException("UDP RTP socket not initialized");

            // channelId: 偶数=RTP, 奇数=RTCP
            if (channelId % 2 == 0)
            {
                // RTP数据
                await _rtpSocket.SendAsync(data, data.Length, _remoteRtpEndpoint);
            }
            else if (_rtcpSocket != null && _remoteRtcpEndpoint != null)
            {
                // RTCP数据
                await _rtcpSocket.SendAsync(data, data.Length, _remoteRtcpEndpoint);
            }
        }

        /// <summary>
        /// 接收循环 - 从UDP读取RTP/RTCP数据
        /// </summary>
        public async Task StartReceiveLoopAsync(ChannelWriter<(byte[] Data, byte Channel)> writer, CancellationToken ct)
        {
            var tasks = new List<Task>();

            // 同时接收RTP和RTCP
            if (_rtpSocket != null)
            {
                tasks.Add(ReceiveUdpLoopAsync(_rtpSocket, writer, 0, ct));  // channel 0 = RTP
            }
            if (_rtcpSocket != null)
            {
                tasks.Add(ReceiveUdpLoopAsync(_rtcpSocket, writer, 1, ct)); // channel 1 = RTCP
            }

            // 同时接收TCP RTSP响应
            tasks.Add(ReceiveTcpLoopAsync(writer, ct));

            try
            {
                await Task.WhenAny(tasks);
            }
            finally
            {
                writer.TryComplete();
            }
        }

        /// <summary>
        /// UDP接收循环
        /// </summary>
        private async Task ReceiveUdpLoopAsync(UdpClient socket, ChannelWriter<(byte[] Data, byte Channel)> writer, byte channel, CancellationToken ct)
        {
            if (socket == null) return;

            try
            {
                while (!ct.IsCancellationRequested && _isConnected)
                {
                    var result = await socket.ReceiveAsync();
                    if (result.Buffer != null && result.Buffer.Length > 0)
                    {
                        await writer.WriteAsync((result.Buffer, channel), ct);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
        }

        /// <summary>
        /// TCP接收循环 (接收RTSP响应)
        /// </summary>
        private async Task ReceiveTcpLoopAsync(ChannelWriter<(byte[] Data, byte Channel)> writer, CancellationToken ct)
        {
            if (_tcpStream == null) return;

            var buffer = new byte[65536];
            int buffered = 0;

            try
            {
                while (!ct.IsCancellationRequested && _isConnected)
                {
                    int read = await _tcpStream.ReadAsync(buffer, buffered, buffer.Length - buffered, ct);
                    if (read == 0) break;

                    buffered += read;

                    // 检查是否为RTSP响应 (以"RTSP/"开头)
                    if (buffered >= 5 && buffer[0] == 'R' && buffer[1] == 'T' && buffer[2] == 'S' && buffer[3] == 'P' && buffer[4] == '/')
                    {
                        // 找到响应结束位置 (\r\n\r\n)
                        int headerEnd = FindHeaderEnd(buffer, buffered);
                        if (headerEnd > 0)
                        {
                            byte[] responseData = new byte[headerEnd];
                            Array.Copy(buffer, responseData, headerEnd);

                            // channel 0xFF 表示RTSP响应
                            await writer.WriteAsync((responseData, 0xFF), ct);

                            // 移动剩余数据
                            int remaining = buffered - headerEnd;
                            if (remaining > 0)
                            {
                                Buffer.BlockCopy(buffer, headerEnd, buffer, 0, remaining);
                            }
                            buffered = remaining;
                        }
                    }
                    else if (buffered >= 4 && buffer[0] == 0x24) // '$' interleaved
                    {
                        // 解析interleaved帧
                        int length = (buffer[2] << 8) | buffer[3];
                        if (buffered >= 4 + length)
                        {
                            byte channel = buffer[1];
                            byte[] rtpData = new byte[length];
                            Array.Copy(buffer, 4, rtpData, 0, length);

                            await writer.WriteAsync((rtpData, channel), ct);

                            int consumed = 4 + length;
                            int remaining = buffered - consumed;
                            if (remaining > 0)
                            {
                                Buffer.BlockCopy(buffer, consumed, buffer, 0, remaining);
                            }
                            buffered = remaining;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) when (ct.IsCancellationRequested) { }
        }

        /// <summary>
        /// 查找HTTP头结束位置 (\r\n\r\n)
        /// </summary>
        private static int FindHeaderEnd(byte[] buffer, int length)
        {
            for (int i = 0; i < length - 3; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                {
                    return i + 4;
                }
            }
            return -1;
        }

        /// <summary>
        /// 关闭所有连接
        /// </summary>
        public async Task CloseAsync(CancellationToken ct)
        {
            _isConnected = false;

            _tcpStream?.Close();
            _tcpClient?.Close();
            _rtpSocket?.Close();
            _rtcpSocket?.Close();

            await Task.CompletedTask;
        }

        public void Dispose()
        {
            _isConnected = false;

            _tcpStream?.Dispose();
            _tcpClient?.Dispose();
            _rtpSocket?.Dispose();
            _rtcpSocket?.Dispose();
        }
    }
}
