# Cyaim.RTSPClient
CSharp RTSP client.

## Quick Play Audio
1,Install linrary 
> Install-Package Cyaim.RTSPClient
2,Play Audio
```C#
RTSPSession session = RTSPSession.Connect("rtsp://192.168.1.127:554");
await session.LoginDigest("admin", "admin", "rtsp://192.168.1.127:554/1/1", true);
await session.Setup("/trackID=3", "RTP/AVP/TCP;unicast;interleaved=0-1", true);
await session.Play("/trackID=3", "npt=0.000-", true);
byte[] audio = File.ReadAllBytes(@"../../Voice/out1_8k.g711a");
await session.PlayAudio_G711A(audio, 25, 8000, 255);
```

## Other about ffmpeg

> Query support encoder,The corresponding encoder of G.711A is pcm_ alaw.
```
ffmpeg -encoders
```

> The output format of pcm_ alaw is alaw.
```
ffpmeg -formats
```

> Convert mp3 to G.711A 8000Hz.
```
ffmpeg -i 1.mp3 -acodec pcm_alaw -f alaw -ac 1 -ar 8000 -vn out1_8k.g711a
ffplay -i out1_8k.g711a -f alaw -ac 1 -ar 8000
```
