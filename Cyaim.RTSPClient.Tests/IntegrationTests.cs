using Cyaim.RTSPClient.Session;
using Cyaim.RTSPServer;
using Cyaim.RTSPServer.Config;
using Cyaim.RTSPServer.Media;
using Cyaim.RTSPServer.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Cyaim.RTSPClient.Tests;

/// <summary>
/// 进程内 RTSP 服务器（TestPattern 视频 + PCMA 音频）
/// </summary>
internal sealed class InProcRtspServer : IAsyncDisposable
{
    public int Port { get; }
    private readonly ServiceProvider _provider;
    private readonly RtspServerHost _host;

    private InProcRtspServer(int port, ServiceProvider provider, RtspServerHost host)
    {
        Port = port;
        _provider = provider;
        _host = host;
    }

    public static async Task<InProcRtspServer> StartAsync(int port)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        services.AddOptions<RtspServerOptions>().Configure(o =>
        {
            o.Host = "127.0.0.1";
            o.Port = port;
            o.RtpPortRangeStart = 30000 + (port % 1000) * 4;
            o.Streams.Add(new StreamConfig
            {
                Path = "/cam",
                Name = "Test",
                SourceType = MediaSourceType.TestPattern,
                VideoCodec = VideoCodecType.H264,
                EnableAudio = true,
                AudioCodec = AudioCodecType.PCMA,
                Framerate = 25
            });
        });
        services.AddSingleton<StreamManager>();
        services.AddSingleton<RtspProtocolHandler>();
        services.AddSingleton<RtspServerHost>();

        var provider = services.BuildServiceProvider();
        var host = provider.GetRequiredService<RtspServerHost>();
        await host.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await provider.GetRequiredService<StreamManager>().StartStreamAsync("/cam", CancellationToken.None);
        await Task.Delay(200);
        return new InProcRtspServer(port, provider, host);
    }

    public string Url => $"rtsp://127.0.0.1:{Port}/cam";

    public async ValueTask DisposeAsync()
    {
        try { await _host.StopAsync(CancellationToken.None); } catch { }
        try { _provider.GetRequiredService<RtspProtocolHandler>().Dispose(); } catch { }
        await _provider.DisposeAsync();
    }
}

[Trait("Category", "Integration")]
public class TcpFacadeIntegrationTests
{
    [Fact]
    public async Task StartAsync一键拉流_音视频帧解包正常_无丢包()
    {
        await using var server = await InProcRtspServer.StartAsync(18571);

        var config = new RTSPSessionConfig
        {
            Url = server.Url,
            TransportMode = TransportMode.TcpInterleaved,
            AutoReconnect = false
        };

        await using var session = new RTSPSession(config);
        await session.StartAsync();

        Assert.Equal(RTSPConnectionState.Playing, session.State);
        Assert.Equal(2, session.SDP?.MediaDescriptions.Count);

        var videoFrames = session.GetMediaFrameReader(0);
        var audioFrames = session.GetMediaFrameReader(1);

        int videoCount = 0, audioCount = 0, keyFrames = 0;
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline)
        {
            while (videoFrames.TryRead(out var vf)) { videoCount++; if (vf.IsKeyFrame) keyFrames++; }
            while (audioFrames.TryRead(out _)) { audioCount++; }
            await Task.Delay(20);
        }

        Assert.True(videoCount >= 50, $"视频帧 {videoCount} < 50");
        Assert.True(keyFrames >= 2, $"关键帧 {keyFrames} < 2");
        Assert.True(audioCount >= 150, $"音频帧 {audioCount} < 150（PCMA 50fps）");
        Assert.Equal(0, session.PacketsDropped);

        await session.DisconnectAsync();
        Assert.Equal(RTSPConnectionState.Disconnected, session.State);
    }
}

[Trait("Category", "Integration")]
public class UdpIntegrationTests
{
    [Fact]
    public async Task UDP单播拉流_视频帧解包正常()
    {
        await using var server = await InProcRtspServer.StartAsync(18572);

        var config = new RTSPSessionConfig
        {
            Url = server.Url,
            TransportMode = TransportMode.UdpUnicast,
            AutoReconnect = false
        };

        await using var session = new RTSPSession(config);
        await session.StartAsync();

        int rtpCount = 0;
        session.DataReceived += (_, _) => Interlocked.Increment(ref rtpCount);

        var videoFrames = session.GetMediaFrameReader(0);
        int videoCount = 0;
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline)
        {
            while (videoFrames.TryRead(out _)) videoCount++;
            await Task.Delay(20);
        }

        Assert.True(rtpCount > 100, $"UDP RTP 包 {rtpCount} <= 100");
        Assert.True(videoCount >= 50, $"UDP 视频帧 {videoCount} < 50");
        await session.DisconnectAsync();
    }
}

[Trait("Category", "Integration")]
public class ReconnectIntegrationTests
{
    [Fact]
    public async Task 服务器重启后自动重连并恢复媒体流()
    {
        const int port = 18573;
        var server = await InProcRtspServer.StartAsync(port);

        var config = new RTSPSessionConfig
        {
            Url = server.Url,
            TransportMode = TransportMode.TcpInterleaved,
            AutoReconnect = true,
            MaxReconnectAttempts = 10,
            ReconnectDelay = TimeSpan.FromMilliseconds(500)
        };

        await using var session = new RTSPSession(config);

        bool reconnectedFired = false;
        session.Reconnected += (_, _) => reconnectedFired = true;

        long packetsBefore = 0, packetsAfter = 0;
        bool killed = false;
        session.DataReceived += (_, _) =>
        {
            if (!killed) Interlocked.Increment(ref packetsBefore);
            else Interlocked.Increment(ref packetsAfter);
        };

        await session.StartAsync();
        await Task.Delay(1500);
        Assert.True(Interlocked.Read(ref packetsBefore) > 30, "重启前应正常收流");

        // 模拟设备重启：杀掉服务器再重建
        killed = true;
        await server.DisposeAsync();
        await Task.Delay(800);
        var server2 = await InProcRtspServer.StartAsync(port);

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline && Interlocked.Read(ref packetsAfter) < 100)
                await Task.Delay(100);

            Assert.True(reconnectedFired, "Reconnected 事件未触发");
            Assert.True(Interlocked.Read(ref packetsAfter) >= 100,
                $"重连后仅收到 {packetsAfter} 包，媒体流未恢复");
            Assert.Equal(RTSPConnectionState.Playing, session.State);

            await session.DisconnectAsync();
        }
        finally
        {
            await server2.DisposeAsync();
        }
    }
}
