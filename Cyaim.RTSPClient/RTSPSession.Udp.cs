using Cyaim.RTSPClient.Exceptions;
using Cyaim.RTSPClient.Rtp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Cyaim.RTSPClient
{
    /// <summary>
    /// RTSPSession UDP 单播传输支持：
    /// SETUP 协商 client_port/server_port，本地绑定相邻端口对接收 RTP/RTCP，
    /// 经乱序重排缓冲后进入统一的包分发泵。
    /// </summary>
    public partial class RTSPSession
    {
        private sealed class UdpTrackTransport : IDisposable
        {
            public int TrackId;
            public StreamType StreamType;
            public UdpClient? RtpSocket;
            public UdpClient? RtcpSocket;
            public IPEndPoint? ServerRtpEndpoint;
            public IPEndPoint? ServerRtcpEndpoint;
            public RtpReorderBuffer Reorder = new();
            public CancellationTokenSource? Cts;

            public void Dispose()
            {
                try { Cts?.Cancel(); } catch { }
                try { RtpSocket?.Dispose(); } catch { }
                try { RtcpSocket?.Dispose(); } catch { }
            }
        }

        private readonly ConcurrentDictionary<int, UdpTrackTransport> _udpTransports = new();

        /// <summary>
        /// 使用 UDP 单播 SETUP 一条媒体轨。
        /// 本地自动分配相邻端口对（RTP 偶数 / RTCP 奇数），接收侧带乱序重排。
        /// </summary>
        public async Task<RTSPResponse> SetupUdpAsync(string channelUri, CancellationToken ct = default)
        {
            if (Uri == null)
                throw new InvalidOperationException("Not connected");

            int trackId = TryParseTrackIdFromControl(channelUri) ?? _udpTransports.Count;

            // 分配相邻端口对
            var (rtpSocket, rtcpSocket, rtpPort) = BindUdpPortPair();

            var transport = new UdpTrackTransport
            {
                TrackId = trackId,
                StreamType = ResolveTrackMedia(trackId)?.IsAudio == true ? StreamType.Audio : StreamType.Video,
                RtpSocket = rtpSocket,
                RtcpSocket = rtcpSocket
            };

            string transportHeader = $"RTP/AVP;unicast;client_port={rtpPort}-{rtpPort + 1}";
            var response = await SetupInternalAsync(channelUri, transportHeader, recordHistory: true, useBackchannel: false, ct)
                .ConfigureAwait(false);

            if (response.StatusCode != "200")
            {
                transport.Dispose();
                return response;
            }

            // 解析 server_port，用于 NAT 打洞与 RTCP 发送
            string? responseTransport = GetHeader(response, "Transport");
            var (serverRtpPort, serverRtcpPort) = ParseServerPorts(responseTransport);
            if (serverRtpPort > 0)
            {
                var serverAddress = (await Dns.GetHostAddressesAsync(Uri.Host).ConfigureAwait(false))[0];
                transport.ServerRtpEndpoint = new IPEndPoint(serverAddress, serverRtpPort);
                transport.ServerRtcpEndpoint = new IPEndPoint(serverAddress, serverRtcpPort > 0 ? serverRtcpPort : serverRtpPort + 1);

                // NAT 打洞：向服务器端口各发一个空包
                try
                {
                    await rtpSocket.SendAsync(Array.Empty<byte>(), 0, transport.ServerRtpEndpoint).ConfigureAwait(false);
                    await rtcpSocket.SendAsync(Array.Empty<byte>(), 0, transport.ServerRtcpEndpoint).ConfigureAwait(false);
                }
                catch { }
            }

            _udpTransports[trackId] = transport;

            // 注册 RTCP 统计上下文（键 = trackId*2，与接收循环一致）
            int clockRate = ResolveTrackMedia(trackId)?.GetPrimaryCodec()?.ClockRate ?? 0;
            if (clockRate <= 0)
                clockRate = transport.StreamType == StreamType.Video ? 90000 : 8000;
            RegisterRtpTracker((byte)(trackId * 2), (byte)(trackId * 2 + 1), trackId, clockRate);

            // 启动接收循环
            transport.Cts = CancellationTokenSource.CreateLinkedTokenSource(_receiveCts?.Token ?? CancellationToken.None);
            var token = transport.Cts.Token;
            _ = Task.Run(() => UdpRtpReceiveLoopAsync(transport, token));
            _ = Task.Run(() => UdpRtcpReceiveLoopAsync(transport, token));

            return response;
        }

        /// <summary>
        /// 绑定相邻端口对（RTP 偶数端口，RTCP = RTP + 1）
        /// </summary>
        private static (UdpClient rtp, UdpClient rtcp, int rtpPort) BindUdpPortPair()
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                UdpClient? probe = null;
                try
                {
                    probe = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                    int port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
                    int rtpPort = port % 2 == 0 ? port : port + 1;

                    if (rtpPort != port)
                    {
                        probe.Dispose();
                        probe = new UdpClient(new IPEndPoint(IPAddress.Any, rtpPort));
                    }

                    var rtcp = new UdpClient(new IPEndPoint(IPAddress.Any, rtpPort + 1));
                    return (probe, rtcp, rtpPort);
                }
                catch (SocketException)
                {
                    probe?.Dispose();
                }
            }

            throw new RTSPTransportException("Failed to bind an adjacent UDP port pair for RTP/RTCP");
        }

        private static (int rtpPort, int rtcpPort) ParseServerPorts(string? transportHeader)
        {
            if (transportHeader == null)
                return (0, 0);

            foreach (var part in transportHeader.Split(';'))
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith("server_port=", StringComparison.OrdinalIgnoreCase))
                {
                    var ports = trimmed.Substring("server_port=".Length).Split('-');
                    int rtp = ports.Length >= 1 && int.TryParse(ports[0], out int p1) ? p1 : 0;
                    int rtcp = ports.Length >= 2 && int.TryParse(ports[1], out int p2) ? p2 : 0;
                    return (rtp, rtcp);
                }
            }
            return (0, 0);
        }

        private async Task UdpRtpReceiveLoopAsync(UdpTrackTransport transport, CancellationToken ct)
        {
            var socket = transport.RtpSocket;
            if (socket == null)
                return;

            try
            {
                while (!ct.IsCancellationRequested)
                {
#if NET8_0_OR_GREATER
                    var result = await socket.ReceiveAsync(ct).ConfigureAwait(false);
#else
                    var result = await socket.ReceiveAsync().ConfigureAwait(false);
#endif
                    var data = result.Buffer;
                    if (data.Length < 12)
                        continue;

                    RTPPacket packet;
                    try
                    {
                        packet = RTPPacketParser.Parse(data, transport.TrackId, transport.StreamType);
                    }
                    catch (RTPParseException)
                    {
                        continue;
                    }

                    // 统计（RR 用）：UDP 轨以 trackId*2 作为统计键
                    UpdateReceptionStats((byte)(transport.TrackId * 2), in packet);

                    // 乱序重排后进入统一分发泵
                    foreach (var ordered in transport.Reorder.Feed(packet))
                    {
                        EnqueuePacket(ordered);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException ex)
            {
                LastException = ex;
            }
        }

        private async Task UdpRtcpReceiveLoopAsync(UdpTrackTransport transport, CancellationToken ct)
        {
            var socket = transport.RtcpSocket;
            if (socket == null)
                return;

            try
            {
                while (!ct.IsCancellationRequested)
                {
#if NET8_0_OR_GREATER
                    var result = await socket.ReceiveAsync(ct).ConfigureAwait(false);
#else
                    var result = await socket.ReceiveAsync().ConfigureAwait(false);
#endif
                    // 复用 interleaved RTCP 处理（SR 解析 / 墙钟映射）
                    HandleRtcpFrame((byte)(transport.TrackId * 2 + 1), transport.TrackId, result.Buffer);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException ex)
            {
                LastException = ex;
            }
        }

        /// <summary>
        /// 通过 UDP 发送 RTCP RR（RtcpLoop 对 UDP 轨调用）
        /// </summary>
        private async Task<bool> TrySendRtcpOverUdpAsync(int trackId, byte[] payload, CancellationToken ct)
        {
            if (_udpTransports.TryGetValue(trackId, out var transport) &&
                transport.RtcpSocket != null && transport.ServerRtcpEndpoint != null)
            {
                try
                {
                    await transport.RtcpSocket.SendAsync(payload, payload.Length, transport.ServerRtcpEndpoint)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LastException = ex;
                }
                return true;
            }
            return false;
        }

        private void CleanupUdpTransports()
        {
            foreach (var transport in _udpTransports.Values)
            {
                transport.Dispose();
            }
            _udpTransports.Clear();
        }
    }
}
