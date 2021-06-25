using Cyaim.RTSPClient.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cyaim.RTSPClient
{

    public class RTSPSession : IDisposable
    {
        public TcpClient client;

        private NetworkStream tcpStream { get; set; }

        /// <summary>
        /// 请求结果响应
        /// K：CSeq,V:result
        /// </summary>
        private Dictionary<int, RTSPResponse> requestResults { get; set; } = new Dictionary<int, RTSPResponse>();

        public Uri Uri { get; private set; }

        public Exception Exception { get; private set; }


        public int NewCSeq
        {
            get
            {
                return ++cseq;
            }
        }
        private int cseq;

        public Task AcceptHandler { get; set; }

        public string UserName { get; set; }

        public string Password { get; set; }


        public string Authorization { get; set; }

        public SDP SDP { get; set; }


        public string Session { get; set; }

        public string Public { get; private set; }

        public string OnvifBackChannel { get; set; } = "www.onvif.org/ver20/backchannel";
        public bool HasBackChannelSupported { get; set; }


        /// <summary>
        /// Next tcp connection timeout
        /// </summary>
        public int Timeout { get; private set; }

        /// <summary>
        /// Wait response result timeout millisecond.
        /// </summary>
        public int WaitResponseTimeout { get; set; }

        /// <summary>
        /// Connect server
        /// </summary>
        /// <param name="url">rtsp://192.168.1.127:554</param>
        /// <param name="accetpTask"></param>
        /// <returns></returns>
        public static RTSPSession Connect(string url, Task<TcpClient> accetpTask = null)
        {
            try
            {
                Uri uri = new Uri(url);

                RTSPSession session = new RTSPSession() { Uri = uri };
                session.client = new TcpClient(uri.Host, uri.Port);

                session.AcceptHandler = session.Accept();

                return session;
            }
            catch (Exception)
            {
                throw;
            }
        }

        /// <summary>
        /// Processing response
        /// </summary>
        /// <returns></returns>
        private async Task Accept()
        {

            tcpStream = client.GetStream();

            //StreamReader sr = new StreamReader(ns);//流读写器

            while (true)
            {
                try
                {
                    byte[] raw = new byte[1024];

                    int streamCount = await tcpStream.ReadAsync(raw, 0, raw.Length);

                    if (streamCount == 0)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    string msg = Encoding.Default.GetString(raw, 0, streamCount);

                    RTSPResponse response = new RTSPResponse(msg, raw);

                    requestResults.Remove(response.CSeq);
                    requestResults.Add(response.CSeq, response);

                    //tcpStream.Flush();

                    //ns.Close();
                }
                catch (Exception ex)
                {
                    this.Exception = ex;

                    // server disconnect
                    break;
                }
            }

        }

        #region Send

        /// <summary>
        /// async send data
        /// </summary>
        /// <param name="bin"></param>
        /// <returns></returns>
        public async Task SendAsync(byte[] bin)
        {
            await tcpStream.WriteAsync(bin, 0, bin.Length);
            //tcpStream.Flush();
        }

        /// <summary>
        /// send data
        /// </summary>
        /// <param name="bin"></param>
        public void Send(byte[] bin)
        {
            tcpStream.Write(bin, 0, bin.Length);
        }

        /// <summary>
        /// send string data.
        /// </summary>
        /// <param name="data"></param>
        /// <param name="encoding"></param>
        /// <returns></returns>
        public async Task Send(string data, Encoding encoding)
        {
            byte[] bin = encoding.GetBytes(data);
            await SendAsync(bin);
        }

        /// <summary>
        /// send RTSP control signalling
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public async Task<RTSPResponse> SendAsync(RTSPRequest request)
        {
            string req = RTSPRequest.GetRequest(request);

            await Send(req, Encoding.Default);

            RTSPResponse res = await GetResponse(request.CSeq);
            return res;
        }
        #endregion

        /// <summary>
        /// Update server disconnect time.
        /// </summary>
        /// <param name="res"></param>
        private void UpdateTimeout(RTSPResponse res)
        {
            try
            {
                string sessionVal = res.Headers.Where(x => x.Key == "Session").FirstOrDefault().Value;
                if (!string.IsNullOrEmpty(sessionVal))
                {
                    string[] sessionParms = sessionVal.Split(';');
                    if (sessionParms.Length > 1)
                    {
                        this.Session = sessionParms[0];
                    }

                    string timeout = sessionParms.Where(x => x.IndexOf("timeout=") > -1).FirstOrDefault() ?? string.Empty;
                    timeout = timeout.Replace("timeout=", "");
                    bool canTime = Int32.TryParse(timeout, out int time);
                    if (canTime)
                    {
                        Timeout = time;
                    }
                }
            }
            catch
            {

            }
        }

        /// <summary>
        /// Loop get response by CSeq.
        /// </summary>
        /// <param name="cseq"></param>
        /// <returns></returns>
        public async Task<RTSPResponse> GetResponse(int cseq)
        {
            try
            {
                RTSPResponse res = await Task.Run<RTSPResponse>(() =>
                {
                    Stopwatch stopwatch = new Stopwatch();
                    bool hasResult = false;
                    stopwatch.Start();
                    while (true)
                    {
                        if (WaitResponseTimeout != 0 && stopwatch.ElapsedMilliseconds > WaitResponseTimeout)
                        {
                            throw new TimeoutException($"Get data timeout,use time {stopwatch.ElapsedMilliseconds} ms.");
                            return null;
                        }

                        hasResult = requestResults.TryGetValue(cseq, out RTSPResponse response);

                        if (hasResult)
                        {
                            return response;
                        }
                    }
                });

                return res;
            }
            catch (Exception)
            {

                throw;
            }

        }

        /// <summary>
        /// Login;
        /// mode digest
        /// </summary>
        /// <param name="username"></param>
        /// <param name="password"></param>
        /// <param name="uri"></param>
        /// <param name="useBackchannel"></param>
        /// <returns></returns>
        public async Task<RTSPResponse> LoginDigest(string username, string password, string uri, bool useBackchannel)
        {

            RTSPRequest request = new RTSPRequest()
            {
                Method = "OPTIONS",
                URI = uri,
                Version = "RTSP/1.0",
                CSeq = NewCSeq
            };
            RTSPResponse response = await SendAsync(request);
            if (response.StatusCode != "200")
            {
                throw new Exception("Inquiry server Public failed.");
            }

            var pub = response.Headers.Where(x => x.Key == "Public").FirstOrDefault();
            Public = pub.Value;

            request = new RTSPRequest()
            {
                Method = "DESCRIBE",
                URI = uri,
                Version = "RTSP/1.0",
                Accept = "application/sdp",
                CSeq = NewCSeq,
                //Require = "www.onvif.org/ver20/backchannel"
            };
            if (useBackchannel)
            {
                request.Require = OnvifBackChannel;
            }

            response = await SendAsync(request);
            switch (response.StatusCode)
            {
                case "200":
                    {
                        //无需授权
                        SDP = new SDP(response.Response);
                    }
                    break;
                case "401":
                    {
                        //需要授权
                        string realm = "RTSP SERVER";
                        string nonce = "3e1456b5a39d3b47f90cd2c149b1e24d";
                        string method = "DESCRIBE";

                        var auth = response.Headers.Where(x => x.Key == "WWW-Authenticate").FirstOrDefault();
                        // Digest realm="RTSP SERVER",nonce="3e1456b5a39d3b47f90cd2c149b1e24d",stale="FALSE"

                        if (auth.Value.IndexOf("Digest") != 0)
                        {
                            throw new Exception("Server auth mode not Digest");
                        }

                        GetDigestParams(ref realm, ref nonce, auth.Value);
                        //"rtsp://192.168.1.127:554/1/1"
                        request.Authorization = AuthorizationDigest(username, password, uri, realm, nonce, method);

                        request.CSeq = NewCSeq;

                        //Authorization: Digest username=""admin"", realm=""RTSP SERVER"", nonce=""3e1456b5a39d3b47f90cd2c149b1e24d"", uri=""rtsp://192.168.1.127:554/1/1"", response=""8ec02e57386ea9fcd3bf0bb997da1fb8""
                        response = await SendAsync(request);
                        if (response.StatusCode == "200")
                        {
                            Authorization = request.Authorization;
                            SDP = new SDP(response.Response);
                        }

                    }
                    break;
                default:
                    break;
            }

            if (useBackchannel)
            {
                if (response.StatusCode == "551")
                {
                    HasBackChannelSupported = false;
                }

                if (SDP != null && SDP.MediaDescribes.Count > 0)
                {
                    HasBackChannelSupported = SDP.MediaDescribes.FirstOrDefault(x => x.a.FirstOrDefault(y => y == "sendonly") != null) != null;
                }
                else
                {
                    HasBackChannelSupported = false;
                }

            }


            return response;
        }

        public static void GetDigestParams(ref string realm, ref string nonce, string authHeader)
        {
            string[] authVal = authHeader.Remove(0, 7).Split(',');
            //realm="RTSP SERVER"
            foreach (var item in authVal)
            {
                int splitIndex = item.IndexOf('=');
                string k = item.Substring(0, splitIndex).Trim();
                string v = item.Substring(splitIndex + 1, item.Length - splitIndex - 1).TrimStart('"').TrimEnd('"');

                switch (k)
                {
                    case "realm":
                        realm = v;
                        break;
                    case "nonce":
                        nonce = v;
                        break;
                    default:
                        break;
                }
            }
        }

        public static string AuthorizationDigest(string username, string password, string uri, string realm, string nonce, string method)
        {
            string m1 = $@"{username}:{realm}:{password}".Md532().ToLower();
            string m2 = $@"{method}:{uri}".Md532().ToLower();
            string dig = $@"{m1}:{nonce}:{m2}".Md532().ToLower();

            return $@"Digest username=""{username}"", realm=""{realm}"", nonce=""{nonce}"", uri=""{uri}"", response=""{dig}""";
        }

        public async Task UpdateAuthorization(string authHeader, string uri, string method)
        {
            //需要授权
            //var auth = response.Headers.Where(x => x.Key == "WWW-Authenticate").FirstOrDefault();
            // Digest realm="RTSP SERVER",nonce="3e1456b5a39d3b47f90cd2c149b1e24d",stale="FALSE"

            if (authHeader.IndexOf("Digest") != 0)
            {
                throw new Exception("Server auth mode not Digest");
            }

            string realm = string.Empty;
            string nonce = string.Empty;


            GetDigestParams(ref realm, ref nonce, authHeader);
            //"rtsp://192.168.1.127:554/1/1"
            this.Authorization = AuthorizationDigest(UserName, Password, uri, realm, nonce, method);
        }

        public async Task UpateAuthorization(RTSPResponse response, string uri, string method)
        {
            //需要授权
            var auth = response.Headers.Where(x => x.Key == "WWW-Authenticate").FirstOrDefault();
            // Digest realm="RTSP SERVER",nonce="3e1456b5a39d3b47f90cd2c149b1e24d",stale="FALSE"

            await UpdateAuthorization(auth.Value, uri, method);
        }

        /// <summary>
        /// Setup device channel
        /// </summary>
        /// <param name="channelUri"></param>
        /// <param name="transport"></param>
        /// <param name="useBackchannel"></param>
        /// <returns></returns>
        public async Task<RTSPResponse> Setup(string channelUri, string transport, bool useBackchannel)
        {
            RTSPRequest request = new RTSPRequest()
            {
                Method = "SETUP",
                URI = Uri.AbsoluteUri + channelUri,
                Version = "RTSP/1.0",
                CSeq = NewCSeq,
                Transport = transport,
                Authorization = Authorization,
                //Require = "www.onvif.org/ver20/backchannel"
            };
            if (useBackchannel)
            {
                request.Require = OnvifBackChannel;
            }

            RTSPResponse res = await SendAsync(request);

            switch (res.StatusCode)
            {
                case "401":
                    {
                        await UpateAuthorization(res, request.URI, request.Method);

                        request.CSeq = NewCSeq;

                        //Authorization: Digest username=""admin"", realm=""RTSP SERVER"", nonce=""3e1456b5a39d3b47f90cd2c149b1e24d"", uri=""rtsp://192.168.1.127:554/1/1"", response=""8ec02e57386ea9fcd3bf0bb997da1fb8""
                        res = await SendAsync(request);

                    }
                    break;
                default:
                    break;
            }

            UpdateTimeout(res);

            return res;
        }

        /// <summary>
        /// Play
        /// </summary>
        /// <param name="channelUri"></param>
        /// <param name="range"></param>
        /// <param name="useBackchannel"></param>
        /// <returns></returns>
        public async Task<RTSPResponse> Play(string channelUri, string range, bool useBackchannel)
        {
            Random random = new Random();

            RTSPRequest request = new RTSPRequest()
            {
                Method = "PLAY",
                URI = Uri.AbsoluteUri + channelUri,
                Version = "RTSP/1.0",

                CSeq = NewCSeq,
                Session = this.Session,
                Authorization = Authorization,
                Range = range,
                //Require = "www.onvif.org/ver20/backchannel"
            };
            if (useBackchannel)
            {
                request.Require = OnvifBackChannel;
            }


            RTSPResponse res = await SendAsync(request);

            UpdateTimeout(res);

            return res;
        }

        /// <summary>
        /// Close channel
        /// </summary>
        /// <param name="channelUri"></param>
        /// <param name="useBackchannel"></param>
        /// <returns></returns>
        public async Task<RTSPResponse> Teardown(string channelUri, bool useBackchannel)
        {
            RTSPRequest request = new RTSPRequest()
            {
                Method = "TEARDOWN",
                URI = Uri.AbsoluteUri + channelUri,
                Version = "RTSP/1.0",

                CSeq = NewCSeq,
                Session = this.Session,
                Authorization = Authorization,
                //Require = "www.onvif.org/ver20/backchannel"
            };
            if (useBackchannel)
            {
                request.Require = OnvifBackChannel;
            }

            return await SendAsync(request);
        }

        /// <summary>
        /// Send G711A format audio to device.
        /// </summary>
        /// <param name="audio"></param>
        /// <param name="fps"></param>
        /// <param name="sampleRate"></param>
        /// <param name="ssrc"></param>
        /// <param name="progress">callback;v1:send progress,v2:packet time</param>
        /// <returns></returns>
        public async Task PlayAudio_G711A(byte[] audio, int fps, int sampleRate, long ssrc, Action<decimal, long> progress = null)
        {
            //int packSecLen = 320;

            // 数据包长度
            int rtspHeaderLen = 4;
            int rtpHeaderLen = 12;

            int packSecLen = sampleRate / fps;
            int rtpPackLen = rtpHeaderLen + packSecLen;

            int audioLen = audio.Length;


            int packetHeaderLen = rtspHeaderLen + rtpHeaderLen;

            // 每秒发送数据包
            int packetSecSend = 1000 / fps;

            Stopwatch stopwatch = new Stopwatch();

            for (int i = 0, audioPacketLen = 0; i < (audioPacketLen = audioLen / packSecLen); i++)
            {
                stopwatch.Restart();

                long timestamp = Timestamp.GetNowTimestamp();
                byte[] packet = new byte[packetHeaderLen + packSecLen];
                packet[0] = 0x24;// Magic
                packet[1] = 0x00;// Channel 
                packet[2] = (byte)((rtpPackLen % (0xffff + 1)) / (0xff + 1));// Length1
                packet[3] = (byte)((rtpPackLen % (0xffff + 1)) % (0xff + 1));// Length2
                packet[4] = 0x80;//CSRC False 0x80
                packet[5] = (byte)RTPPayloadType.PCMA;//Payload type:ITU-T G.711 PCMU(0x00)
                packet[6] = (byte)(i / (0xff + 1));
                packet[7] = (byte)(i % (0xff + 1));
                packet[8] = (byte)((timestamp / (0xffff + 1)) / (0xff + 1));
                packet[9] = (byte)((timestamp / (0xffff + 1)) % (0xff + 1));
                packet[10] = (byte)((timestamp % (0xffff + 1)) / (0xff + 1));
                packet[11] = (byte)((timestamp % (0xffff + 1)) % (0xff + 1));
                packet[12] = (byte)((ssrc / (0xffff + 1)) / (0xff + 1));
                packet[13] = (byte)((ssrc / (0xffff + 1)) % (0xff + 1));
                packet[14] = (byte)((ssrc % (0xffff + 1)) / (0xff + 1));
                packet[15] = (byte)((ssrc % (0xffff + 1)) % (0xff + 1));

                Array.Copy(audio, i * packSecLen, packet, packetHeaderLen, packSecLen);

                await SendAsync(packet);

                int packetTime = (int)stopwatch.ElapsedMilliseconds;
                //Console.Error.WriteLine($"{packetTime},{packetSecSend - packetTime < 0}");

                if (progress != null)
                {
                    progress(i / audioPacketLen, packetTime);
                }
                stopwatch.Stop();

                Thread.Sleep(packetSecSend - packetTime < 0 ? 0 : packetSecSend - packetTime);
                Console.WriteLine($"{packetTime},{packetSecSend - packetTime < 0}");

                //Task.Run((prog, time) =>
                //{
                //    Console.WriteLine($"播放进度：{prog}%,{time} \r\n");
                //})
                //Console.Error.WriteLine($"播放进度：{i}/{ audioLen / packSecLen}\r\n");
            }
        }

        #region Dispose
        private bool disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: 释放托管状态(托管对象)

                    try
                    {
                        Teardown(Uri.AbsoluteUri, true).Wait();
                    }
                    finally
                    {
                        tcpStream.Flush();
                        tcpStream.Close();
                        client.Close();


                    }

                }

                // TODO: 释放未托管的资源(未托管的对象)并替代终结器
                // TODO: 将大型字段设置为 null
                disposedValue = true;

                requestResults = null;
                Uri = null;
                Exception = null;
            }
        }

        // // TODO: 仅当“Dispose(bool disposing)”拥有用于释放未托管资源的代码时才替代终结器
        // ~RTSPSession()
        // {
        //     // 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
        //     Dispose(disposing: false);
        // }

        void IDisposable.Dispose()
        {
            // 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
        #endregion

    }

}
