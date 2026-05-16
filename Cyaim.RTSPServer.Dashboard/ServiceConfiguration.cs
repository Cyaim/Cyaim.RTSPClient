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

                    // 添加示例流
                    options.Streams.Add(new StreamConfig
                    {
                        Path = "/live/camera1",
                        Name = "Camera 1",
                        Description = "Main entrance camera",
                        SourceType = MediaSourceType.TestPattern,
                        VideoCodec = VideoCodecType.H264,
                        Width = 1920,
                        Height = 1080,
                        Framerate = 25,
                        EnableAudio = true,
                        AudioCodec = AudioCodecType.PCMA
                    });

                    options.Streams.Add(new StreamConfig
                    {
                        Path = "/live/camera2",
                        Name = "Camera 2",
                        Description = "Parking lot camera",
                        SourceType = MediaSourceType.TestPattern,
                        VideoCodec = VideoCodecType.H264,
                        Width = 1280,
                        Height = 720,
                        Framerate = 15,
                        EnableAudio = false
                    });
                });

                // 注册服务
                services.AddSingleton<RtspServerService>();
            });
    }
}
