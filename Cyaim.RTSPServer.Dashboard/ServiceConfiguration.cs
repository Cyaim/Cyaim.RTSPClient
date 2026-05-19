using Cyaim.RTSPServer.Config;
using Cyaim.RTSPServer.Dashboard.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cyaim.RTSPServer.Dashboard;

/// <summary>
/// 依赖注入配置
/// </summary>
public static class ServiceConfiguration
{
    public static IHostBuilder CreateHostBuilder(string[] args)
    {
        return Host.CreateDefaultBuilder(args)
            .ConfigureServices((context, services) =>
            {
                // 配置选项
                services.Configure<RtspServerOptions>(options =>
                {
                    options.Host = "0.0.0.0";
                    options.Port = 554;
                    options.MaxConnections = 10000;
                    options.SessionTimeout = 60;

                    // 测试图案流
                    options.Streams.Add(new StreamConfig
                    {
                        Path = "/live/camera1",
                        Name = "Camera 1",
                        Description = "Test pattern stream",
                        SourceType = MediaSourceType.TestPattern,
                        VideoCodec = VideoCodecType.H264,
                        Width = 1920,
                        Height = 1080,
                        Framerate = 25,
                        EnableAudio = true,
                        AudioCodec = AudioCodecType.PCMA
                    });

                    // MP4 文件流 - 用于测试
                    options.Streams.Add(new StreamConfig
                    {
                        Path = "/live/mp4test",
                        Name = "MP4 Test",
                        Description = "MP4 file playback test",
                        Source = @"E:\VID20241228112019.mp4",
                        SourceType = MediaSourceType.File,
                        VideoCodec = VideoCodecType.H264,
                        Width = 1920,
                        Height = 1080,
                        Framerate = 25,
                        EnableAudio = false
                    });
                });

                // 注册服务
                services.AddSingleton<RtspServerService>();
            });
    }
}
