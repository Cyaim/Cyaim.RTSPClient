using Cyaim.RTSPClient.Common;
using Cyaim.RTSPClient.Rtp;
using System.Text;
using Xunit;

namespace Cyaim.RTSPClient.Tests;

/// <summary>
/// RTSP 请求构造：内容体发送、UTF-8 长度、头注入防护
/// </summary>
public class RtspRequestTests
{
    [Fact]
    public void 内容体随请求发送_ContentLength为UTF8字节数()
    {
        var req = new RTSPRequest
        {
            Method = "SET_PARAMETER",
            URI = "rtsp://127.0.0.1/test",
            CSeq = 5,
            ContentType = "text/parameters",
            Content = "参数: 值\r\n"   // 非 ASCII：字符数 != 字节数
        };

        string text = RTSPRequest.GetRequest(req);
        int bodyStart = text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
        string body = text[bodyStart..];
        int declaredLength = int.Parse(
            text.Split("\r\n").First(l => l.StartsWith("Content-Length:"))["Content-Length:".Length..].Trim());

        Assert.Equal("参数: 值\r\n", body);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), declaredLength);
    }

    [Fact]
    public void URI中的CRLF被剔除_防头注入()
    {
        var evil = new RTSPRequest { Method = "SETUP", URI = "rtsp://x/a\r\nEvil: 1", CSeq = 1 };
        Assert.DoesNotContain("\r\nEvil:", RTSPRequest.GetRequest(evil));
    }
}

/// <summary>
/// RTSP 响应解析容错
/// </summary>
public class RtspResponseTests
{
    [Fact]
    public void 非数字CSeq不抛异常()
    {
        // 旧实现 Convert.ToInt32 抛 FormatException，会杀死整个接收循环
        var response = new RTSPResponse("RTSP/1.0 200 OK\r\nCSeq: abc\r\n\r\n", null);
        Assert.Equal(0, response.CSeq);
        Assert.Equal("200", response.StatusCode);
    }
}

/// <summary>
/// Digest 认证：RFC 标准测试向量
/// </summary>
public class DigestTests
{
    [Fact]
    public void RFC2617_qop_auth_标准测试向量()
    {
        // RFC 2617 §3.5 示例
        Func<string, string> md5 = s => s.Md532().ToLower();
        string ha1 = md5("Mufasa:testrealm@host.com:Circle Of Life");
        string ha2 = md5("GET:/dir/index.html");

        string response = RTSPSession.ComputeDigestResponse(
            ha1, ha2, "dcd98b7102dd2f0e8b11d0f600bfb0c093", "00000001", "0a4f113b", "auth", md5);

        Assert.Equal("6629fae49393a05397450978507c4ef1", response);
    }

    [Fact]
    public void RFC2069_无qop旧格式()
    {
        Func<string, string> md5 = s => s.Md532().ToLower();
        string ha1 = md5("user:realm:pass");
        string ha2 = md5("DESCRIBE:rtsp://cam/live");
        const string nonce = "abcdef";

        string response = RTSPSession.ComputeDigestResponse(ha1, ha2, nonce, null, null, null, md5);

        Assert.Equal(md5($"{ha1}:{nonce}:{ha2}"), response);
    }
}

/// <summary>
/// 热路径分配预算：RTP 解析必须零拷贝
/// </summary>
public class AllocationTests
{
    [Fact]
    public void RTPPacketParser_Parse_每包近零分配()
    {
        // 构造一个典型 1400 字节 RTP 包
        var data = new byte[1400];
        data[0] = 0x80;
        data[1] = 96;
        new Random(3).NextBytes(data.AsSpan(12));

        // 预热（JIT/静态初始化）
        for (int i = 0; i < 100; i++)
            RTPPacketParser.Parse(data, 0, StreamType.Video);

        const int iterations = 10_000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            var packet = RTPPacketParser.Parse(data, 0, StreamType.Video);
            _ = packet.PayloadSegment.Count; // 消费零拷贝路径
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        double perPacket = allocated / (double)iterations;
        // 零拷贝解析理论 0 分配；预算 32 字节/包 防止未来退化（如有人把 PayloadSegment 改回拷贝会瞬间超标）
        Assert.True(perPacket < 32, $"Parse 每包分配 {perPacket:F1} 字节，超出零拷贝预算（32B）——热路径疑似引入分配");
    }
}
