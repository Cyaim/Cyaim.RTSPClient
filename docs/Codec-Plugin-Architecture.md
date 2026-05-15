# 编解码器插件架构

## 概述

Cyaim.RTSPClient 采用插件化架构设计编解码器，允许第三方开发者扩展新的编解码器而无需修改核心库。

## 架构图

```
┌─────────────────────────────────────────────────────────────┐
│                    Cyaim.RTSPClient                         │
│  ┌─────────────────────────────────────────────────────┐    │
│  │                  CodecFactory                       │    │
│  │  (自动发现和加载编解码器插件)                          │    │
│  └─────────────────────────────────────────────────────┘    │
│                           │                                 │
│         ┌─────────────────┼─────────────────┐               │
│         ▼                 ▼                 ▼               │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐         │
│  │   内置编解码器 │  │  插件编解码器  │  │  硬件编解码器  │         │
│  │  G.711/722   │  │  AAC/Opus   │  │  NVENC/QSV  │         │
│  │  G.726/729   │  │  AMR/Speex  │  │  AMF/DXVA   │         │
│  └─────────────┘  └─────────────┘  └─────────────┘         │
└─────────────────────────────────────────────────────────────┘
```

## 创建编解码器插件

### 1. 创建类库项目

```bash
dotnet new classlib -n MyCodecPlugin
```

### 2. 添加引用

```xml
<ItemGroup>
  <PackageReference Include="Cyaim.RTSPClient" Version="2.0.0" />
</ItemGroup>
```

### 3. 实现编解码器

```csharp
using Cyaim.RTSPClient.Media;
using Cyaim.RTSPClient.Media.Codecs;

namespace MyCodecs
{
    // 解码器
    public class MyDecoder : IAudioDecoder
    {
        public string Name => "My Audio Decoder";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State { get; private set; }
        public AudioCodec[] SupportedCodecs => new[] { AudioCodec.OPUS };

        public Task InitializeAsync(AudioDecoderConfig config, CancellationToken ct = default)
        {
            State = ProcessorState.Ready;
            return Task.CompletedTask;
        }

        public Task<AudioFrame> DecodeAsync(EncodedAudioFrame input, CancellationToken ct = default)
        {
            // 实现解码逻辑
            byte[] pcm = DecodeOpus(input.Data.Span);
            return Task.FromResult(new AudioFrame
            {
                Data = pcm,
                SampleRate = input.SampleRate,
                Channels = input.Channels,
                BitsPerSample = 16,
                Timestamp = input.Timestamp
            });
        }

        // ... 其他方法
    }

    // 编码器
    public class MyEncoder : IAudioEncoder
    {
        // 类似实现
    }
}
```

### 4. 实现工厂

```csharp
public class MyCodecFactory : IAudioDecoderFactory, IAudioEncoderFactory
{
    public string Name => "My Codecs";
    public int Priority => 100; // 高于内置实现
    public bool IsHardwareAccelerated => false;
    public AudioCodec[] SupportedCodecs => new[] { AudioCodec.OPUS };

    public bool CanCreate(AudioCodec codec, bool preferHardware = true)
        => codec == AudioCodec.OPUS;

    public IAudioDecoder Create(AudioCodec codec) => new MyDecoder();
    public IAudioEncoder Create(AudioCodec codec) => new MyEncoder();
}
```

### 5. 注册插件

```csharp
// 在应用启动时注册
CodecFactory.Instance.RegisterFactory(new MyCodecFactory());
```

## 硬件加速插件

### NVIDIA NVENC 示例

```csharp
public class NvidiaEncoderFactory : IVideoEncoderFactory
{
    public string Name => "NVIDIA NVENC";
    public int Priority => 1000; // 最高优先级
    public bool IsHardwareAccelerated => true;
    public VideoCodec[] SupportedCodecs => new[] { VideoCodec.H264, VideoCodec.H265 };

    public bool CanCreate(VideoCodec codec, bool preferHardware = true)
    {
        if (!preferHardware) return false;
        return CheckNvidiaGpu();
    }

    public bool SupportsDevice(string deviceName) => deviceName == "cuda";

    public IVideoEncoder Create(VideoCodec codec) => new NvidiaEncoder(codec);

    private static bool CheckNvidiaGpu()
    {
        // 检查 NVIDIA GPU
        return File.Exists("/dev/nvidia0") || 
               Environment.GetEnvironmentVariable("CUDA_PATH") != null;
    }
}
```

## 最佳实践

1. **优先级设置**
   - 内置软件实现: 0
   - 第三方软件实现: 50-99
   - 硬件加速实现: 100+

2. **错误处理**
   - 初始化失败应抛出明确异常
   - 解码/编码失败应返回空帧或抛出异常

3. **资源管理**
   - 实现 `IDisposable` 释放资源
   - 使用 `ArrayPool` 减少内存分配

4. **线程安全**
   - 编解码器实例不应跨线程共享
   - 使用 `ProcessorState` 跟踪状态
