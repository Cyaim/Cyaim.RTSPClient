# Cyaim.RTSPClient
CSharp RTSP client.

## Quick Play Audio
1,Install linrary 
> Install-Package Cyaim.RTSPClient

2,Play Audio
```C#
RTSPSession session = RTSPSession.Connect("rtsp://192.168.1.127:554");
await session.LoginDigest("admin", "admin", "rtsp://192.168.1.127:554/1/1", true);
await session.Setup("1/1/trackID=3", "RTP/AVP/TCP;unicast", true);
await session.Play("1/1", "npt=0.000-", true);
byte[] audio = File.ReadAllBytes(@"../../Voice/out1_8k.g711a");

string[] transport = response.Headers.FirstOrDefault(x => x.Key == "Transport").Value.Split(";");
string interleaved = transport.FirstOrDefault(x => x.IndexOf("interleaved") > -1)?.Replace("interleaved=", string.Empty)?.Split('-').FirstOrDefault();
string ssrc = transport?.FirstOrDefault(x => x.IndexOf("ssrc") > -1);
if (!string.IsNullOrEmpty(ssrc))
{
    ssrc = ssrc.Replace("ssrc=", string.Empty);
}
long ssrc2 = long.Parse(ssrc, System.Globalization.NumberStyles.HexNumber);
byte channel = Convert.ToByte(interleaved);
await session.PlayAudio_G711A(audio, 25, 8000, ssrc2, channel);
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
> Query audio file info
```
ffprobe -i out1_8k.g711a -v quiet -print_format json -show_format -show_streams
```

