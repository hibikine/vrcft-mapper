using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace VRCFTMapper.Editor
{
    /// <summary>
    /// VRChat の代わりに OSCQuery サービスを公開し、VRCFaceTracking に「このアバターを着た VRChat」と認識させる。
    ///  - mDNS (224.0.0.251:5353) で "VRChat-Client-XXXXXX" を _oscjson._tcp / _osc._udp として広告
    ///  - HTTP (TCP, localhost) で HOST_INFO とアバターのパラメータツリー (JSON) を返す
    ///  - OSC 送信先として受信ポート (既定 9002) を通知 → VRCFT がそこへ送ってくる
    /// VRChat 本体を起動していなくても VRCFT からの送信をテストできる。
    /// </summary>
    public class OscQueryHost : IDisposable
    {
        public bool Running { get; private set; }
        public string status = "";
        public int httpPort;
        public int queriesAnswered, httpRequests;

        readonly string instance;          // VRChat-Client-XXXXXX
        const string HostName = "vrcft-mapper-unity.local";
        const string OscJson = "_oscjson._tcp.local";
        const string OscUdp = "_osc._udp.local";

        int oscPort;
        volatile string treeJson = "{}";
        volatile string avatarId = "avtr_00000000-0000-0000-0000-vrcftmapper0";
        TcpListener http;
        UdpClient mdns;                                   // 受信 (0.0.0.0:5353)
        readonly List<UdpClient> senders = new List<UdpClient>(); // 送信 (各 NIC の IP:5353)
        public bool vrcftPortDetected;
        Thread httpThread, mdnsThread, announceThread;

        public OscQueryHost()
        {
            var r = new Random();
            instance = "VRChat-Client-" + r.Next(0x100000, 0xFFFFFF).ToString("X6");
        }

        /// <summary>パラメータ一覧を更新 (メインスレッドから)。name は /avatar/parameters/ 以下, isBool で型。</summary>
        public void SetParameters(IEnumerable<(string name, bool isBool)> parameters)
        {
            treeJson = BuildTree(parameters.Distinct().ToList());
        }

        public void Start(int oscReceivePort)
        {
            Stop();
            oscPort = oscReceivePort;
            try
            {
                http = new TcpListener(IPAddress.Loopback, 0);
                http.Start();
                httpPort = ((IPEndPoint)http.LocalEndpoint).Port;

                mdns = new UdpClient();
                mdns.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                mdns.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
                mdns.JoinMulticastGroup(IPAddress.Parse("224.0.0.251"));
                mdns.MulticastLoopback = true;

                // VRCFT は「自分の NIC の IP から来た」mDNS 応答しか受け付けないので、NIC ごとに送信ソケットを作る
                foreach (var ip in LocalIpv4())
                {
                    try
                    {
                        var u = new UdpClient(AddressFamily.InterNetwork);
                        u.Client.ExclusiveAddressUse = false;
                        u.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        u.Client.Bind(new IPEndPoint(ip, 5353));
                        u.JoinMulticastGroup(IPAddress.Parse("224.0.0.251"), ip);
                        u.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                        senders.Add(u); // 受信は 0.0.0.0:5353 側でまとめて行う
                    }
                    catch { }
                }
                if (senders.Count == 0) throw new Exception("マルチキャスト可能なネットワークアダプタが見つかりません");

                Running = true;
                httpThread = new Thread(HttpLoop) { IsBackground = true, Name = "VRCFT OSCQuery HTTP" }; httpThread.Start();
                mdnsThread = new Thread(MdnsLoop) { IsBackground = true, Name = "VRCFT OSCQuery mDNS" }; mdnsThread.Start();
                announceThread = new Thread(AnnounceLoop) { IsBackground = true, Name = "VRCFT OSCQuery announce" }; announceThread.Start();
                status = $"{instance} を公開中 (HTTP {httpPort} / OSC {oscPort})";
            }
            catch (Exception e)
            {
                status = "OSCQuery 開始失敗: " + e.Message;
                Stop();
            }
        }

        public void Stop()
        {
            if (Running) { try { SendAll(goodbye: true); } catch { } }
            Running = false;
            try { http?.Stop(); } catch { }
            try { mdns?.Close(); } catch { }
            foreach (var u in senders) { try { u.Close(); } catch { } }
            senders.Clear();
            http = null; mdns = null;
            if (status.Contains("公開中")) status = "停止";
        }

        /// <summary>アバター変更を通知したい時 (パラメータ構成が変わった時) に ID を変える。</summary>
        public string AvatarId => avatarId;
        public int vrcftPort = 9001;   // VRCFT の受信ポート (VRChat の OSC 出力先と同じ)
        public int avatarChangeSent;

        /// <summary>
        /// VRChat がアバター読み込み時に送る /avatar/change を VRCFT に送る。
        /// VRCFT はこれを受けてパラメータ一覧を (OSCQuery / 設定ファイルから) 再取得する。
        /// </summary>
        public void SendAvatarChange()
        {
            // VRCFT は OSCQuery で Unity を見つけるまでは /avatar/change を受けると設定ファイルを探しに行く
            // (Avatar config file ... not found になる)。見つけた後は自分の受信ポートを mDNS で広告するので、そこへ送る。
            try
            {
                var msg = new List<byte>();
                void Str(string v)
                {
                    var b = Encoding.UTF8.GetBytes(v);
                    msg.AddRange(b);
                    int pad = 4 - (b.Length % 4);
                    for (int i = 0; i < pad; i++) msg.Add(0);
                }
                Str("/avatar/change"); Str(",s"); Str(avatarId);
                using (var u = new UdpClient())
                    u.Send(msg.ToArray(), msg.Count, new IPEndPoint(IPAddress.Loopback, vrcftPort));
                avatarChangeSent++;
            }
            catch (Exception e) { status = "/avatar/change 送信失敗: " + e.Message; }
        }

        /// <summary>
        /// 旧方式 (OSCQuery 以前) の OSC 設定ファイルを書き出す。VRCFT が OSCQuery で取得できない環境向けの保険。
        /// 書き出し先: %LocalLow%/VRChat/VRChat/OSC/usr_*/Avatars/{avatarId}.json
        /// </summary>
        public List<string> WriteLegacyConfig(IEnumerable<(string name, bool isBool)> parameters, string avatarName)
        {
            var written = new List<string>();
            var localLow = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low";
            var oscDir = System.IO.Path.Combine(localLow, "VRChat", "VRChat", "OSC");
            var userDirs = System.IO.Directory.Exists(oscDir)
                ? System.IO.Directory.GetDirectories(oscDir, "usr_*").ToList() : new List<string>();
            if (userDirs.Count == 0) userDirs.Add(System.IO.Path.Combine(oscDir, "usr_00000000-0000-0000-0000-vrcftmapper0"));

            var sb = new StringBuilder();
            sb.Append("{\"id\":\"").Append(avatarId).Append("\",\"name\":\"").Append(avatarName.Replace("\"", "")).Append("\",\"parameters\":[");
            bool first = true;
            foreach (var (name, isBool) in parameters.Distinct())
            {
                if (!first) sb.Append(',');
                first = false;
                var t = isBool ? "Bool" : "Float";
                var a = "/avatar/parameters/" + name;
                sb.Append("{\"name\":\"").Append(name).Append("\",\"input\":{\"address\":\"").Append(a).Append("\",\"type\":\"").Append(t)
                  .Append("\"},\"output\":{\"address\":\"").Append(a).Append("\",\"type\":\"").Append(t).Append("\"}}");
            }
            sb.Append("]}");

            foreach (var u in userDirs)
            {
                var dir = System.IO.Path.Combine(u, "Avatars");
                System.IO.Directory.CreateDirectory(dir);
                var path = System.IO.Path.Combine(dir, avatarId + ".json");
                System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                written.Add(path);
            }
            return written;
        }

        public void BumpAvatar() => avatarId = "avtr_00000000-0000-0000-0000-" + DateTime.Now.Ticks.ToString("x").PadLeft(12, '0').Substring(0, 12);

        // ================= HTTP =================
        void HttpLoop()
        {
            while (Running)
            {
                try
                {
                    var client = http.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(_ => Serve(client));
                }
                catch { if (!Running) break; }
            }
        }

        void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    stream.ReadTimeout = 2000;
                    var buf = new byte[8192];
                    int n = stream.Read(buf, 0, buf.Length);
                    var req = Encoding.ASCII.GetString(buf, 0, n);
                    var first = req.Split('\n')[0].Split(' ');
                    var target = first.Length > 1 ? Uri.UnescapeDataString(first[1]) : "/";
                    httpRequests++;

                    string body;
                    if (target.Contains("HOST_INFO"))
                    {
                        body = "{\"NAME\":\"" + instance + "\",\"OSC_IP\":\"127.0.0.1\",\"OSC_PORT\":" + oscPort +
                               ",\"OSC_TRANSPORT\":\"UDP\",\"EXTENSIONS\":{\"ACCESS\":true,\"CLIPMODE\":false,\"RANGE\":true,\"TYPE\":true,\"VALUE\":true}}";
                    }
                    else
                    {
                        var path = target.Split('?')[0];
                        body = Subtree(path) ?? "";
                        if (body == "") { Write(stream, "404 Not Found", "OSC Path not found"); return; }
                    }
                    Write(stream, "200 OK", body);
                }
                catch { }
            }
        }

        static void Write(NetworkStream s, string code, string body)
        {
            var b = Encoding.UTF8.GetBytes(body);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {code}\r\nContent-Type: application/json\r\nContent-Length: {b.Length}\r\nConnection: close\r\n\r\n");
            s.Write(head, 0, head.Length);
            s.Write(b, 0, b.Length);
        }

        // ---- パラメータツリー ----
        class Node
        {
            public string full;
            public string type; // null = コンテナ
            public SortedDictionary<string, Node> children = new SortedDictionary<string, Node>();
        }

        Node root;

        string BuildTree(List<(string name, bool isBool)> ps)
        {
            var r = new Node { full = "/" };
            Node Ensure(string path)
            {
                var cur = r;
                var acc = "";
                foreach (var seg in path.Trim('/').Split('/'))
                {
                    if (seg == "") continue;
                    acc += "/" + seg;
                    if (!cur.children.TryGetValue(seg, out var nx)) cur.children[seg] = nx = new Node { full = acc };
                    cur = nx;
                }
                return cur;
            }
            Ensure("/avatar/parameters");
            var change = Ensure("/avatar/change"); change.type = "s";
            foreach (var (name, isBool) in ps) Ensure("/avatar/parameters/" + name).type = isBool ? "T" : "f";
            root = r;
            return Json(r);
        }

        string Subtree(string path)
        {
            var r = root;
            if (r == null) return null;
            if (path == "/" || path == "") return treeJson;
            foreach (var seg in path.Trim('/').Split('/'))
                if (!r.children.TryGetValue(seg, out r)) return null;
            return Json(r);
        }

        string Json(Node n)
        {
            var sb = new StringBuilder();
            sb.Append("{\"FULL_PATH\":\"").Append(n.full).Append('"');
            if (n.type == null)
            {
                sb.Append(",\"ACCESS\":0,\"CONTENTS\":{");
                bool first = true;
                foreach (var kv in n.children)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(kv.Key).Append("\":").Append(Json(kv.Value));
                }
                sb.Append('}');
            }
            else if (n.type == "s")
                sb.Append(",\"ACCESS\":3,\"TYPE\":\"s\",\"VALUE\":[\"").Append(avatarId).Append("\"]");
            else if (n.type == "T")
                sb.Append(",\"ACCESS\":3,\"TYPE\":\"T\",\"VALUE\":[false]");
            else
                sb.Append(",\"ACCESS\":3,\"TYPE\":\"f\",\"VALUE\":[0.0]");
            sb.Append('}');
            return sb.ToString();
        }

        // ================= mDNS =================
        static List<IPAddress> LocalIpv4()
        {
            var list = new List<IPAddress>();
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                if (!ni.SupportsMulticast) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork) { list.Add(a.Address); break; }
            }
            return list;
        }

        void MdnsLoop() => RecvLoop(mdns);

        void RecvLoop(UdpClient u)
        {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            while (Running)
            {
                try
                {
                    var data = u.Receive(ref ep);
                    HandlePacket(data);
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { break; }
                catch { }
            }
        }

        void AnnounceLoop()
        {
            // 起動直後は数回、その後は定期的に広告。VRCFT は応答パケットなら問い合わせ無しでも受け取る
            int i = 0;
            while (Running)
            {
                try
                {
                    SendAll(false);
                    if (!vrcftPortDetected) SendQuery(OscUdp); // VRCFT の受信ポートを調べる
                }
                catch { }
                Thread.Sleep(i++ < 5 ? 1000 : 5000);
            }
        }

        void SendRaw(byte[] msg)
        {
            var ep = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
            foreach (var u in senders) { try { u.Send(msg, msg.Length, ep); } catch { } }
        }

        void SendAll(bool goodbye)
        {
            SendRaw(BuildResponse(OscJson, httpPort, goodbye));
            SendRaw(BuildResponse(OscUdp, oscPort, goodbye));
        }

        void SendQuery(string name)
        {
            var w = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
            w.AddRange(EncodeName(name));
            w.Add(0); w.Add(255); w.Add(0); w.Add(1);
            SendRaw(w.ToArray());
        }

        struct Rr { public string name; public int type; public byte[] data; public int dataOffset; }

        void HandlePacket(byte[] d)
        {
            if (d.Length < 12) return;
            int flags = (d[2] << 8) | d[3];
            int qd = (d[4] << 8) | d[5], an = (d[6] << 8) | d[7], ns = (d[8] << 8) | d[9], ar = (d[10] << 8) | d[11];
            int off = 12;
            var questions = new List<string>();
            for (int i = 0; i < qd; i++) { var n = ReadName(d, ref off); if (n == null) return; questions.Add(n); off += 4; }

            if ((flags & 0x8000) == 0)
            {
                // 問い合わせ → 該当するものに応答
                foreach (var q in questions.Select(x => x.ToLowerInvariant()))
                {
                    if (q == OscJson || q == OscUdp || q.StartsWith(instance.ToLowerInvariant()) || q == HostName)
                    {
                        SendAll(false);
                        queriesAnswered++;
                        break;
                    }
                }
                return;
            }

            // 応答 → VRCFT 自身の _osc._udp 広告から受信ポートを取得 (/avatar/change の送り先)
            var rrs = new List<Rr>();
            for (int i = 0; i < an + ns + ar; i++)
            {
                var name = ReadName(d, ref off);
                if (name == null || off + 10 > d.Length) return;
                int type = (d[off] << 8) | d[off + 1];
                int len = (d[off + 8] << 8) | d[off + 9];
                off += 10;
                if (off + len > d.Length) return;
                rrs.Add(new Rr { name = name, type = type, data = d, dataOffset = off });
                off += len;
            }
            if (rrs.Count == 0 || rrs[0].type != 12 || !rrs[0].name.Equals(OscUdp, StringComparison.OrdinalIgnoreCase)) return;
            int p0 = rrs[0].dataOffset;
            var target = ReadName(d, ref p0);
            if (target == null || !target.StartsWith("VRCFT-")) return;
            var srv = rrs.Skip(1).FirstOrDefault(r => r.type == 33);
            if (srv.data == null) return;
            int port = (d[srv.dataOffset + 4] << 8) | d[srv.dataOffset + 5];
            if (port > 0 && (port != vrcftPort || !vrcftPortDetected))
            {
                vrcftPort = port;
                vrcftPortDetected = true;
            }
        }

        static string ReadName(byte[] d, ref int off)
        {
            var parts = new List<string>();
            int jumps = 0, ret = -1;
            while (off < d.Length)
            {
                int len = d[off];
                if (len == 0) { off++; break; }
                if ((len & 0xC0) == 0xC0)
                {
                    if (off + 1 >= d.Length || ++jumps > 10) return null;
                    int ptr = ((len & 0x3F) << 8) | d[off + 1];
                    if (ret < 0) ret = off + 2;
                    off = ptr;
                    continue;
                }
                if (off + 1 + len > d.Length) return null;
                parts.Add(Encoding.UTF8.GetString(d, off + 1, len));
                off += 1 + len;
            }
            if (ret >= 0) off = ret;
            return string.Join(".", parts);
        }

        static byte[] EncodeName(string n)
        {
            var l = new List<byte>();
            foreach (var p in n.Split('.'))
            {
                var b = Encoding.UTF8.GetBytes(p);
                l.Add((byte)b.Length); l.AddRange(b);
            }
            l.Add(0);
            return l.ToArray();
        }

        /// <summary>
        /// VRChat と同じ形の応答: answers[0] = PTR, additionals = TXT / SRV / A。
        /// VRCFT は answers[0] の PTR が "VRChat-Client*" で、additionals に SRV と A があるものを VRChat とみなす。
        /// </summary>
        byte[] BuildResponse(string service, int port, bool goodbye)
        {
            uint ttl = goodbye ? 0u : 120u;
            var w = new List<byte>();
            void U16(int v) { w.Add((byte)(v >> 8)); w.Add((byte)v); }
            void U32(uint v) { w.Add((byte)(v >> 24)); w.Add((byte)(v >> 16)); w.Add((byte)(v >> 8)); w.Add((byte)v); }
            void Rec(string name, int type, bool flush, byte[] rdata)
            {
                w.AddRange(EncodeName(name));
                U16(type);
                U16(flush ? 0x8001 : 0x0001);
                U32(ttl);
                U16(rdata.Length);
                w.AddRange(rdata);
            }
            string inst = instance + "." + service;
            var srv = new List<byte> { 0, 0, 0, 0, (byte)(port >> 8), (byte)port };
            srv.AddRange(EncodeName(HostName));
            var txtB = Encoding.ASCII.GetBytes("txtvers=1");
            var txt = new List<byte> { (byte)txtB.Length }; txt.AddRange(txtB);

            U16(0); U16(0x8400); U16(0); U16(1); U16(0); U16(3);
            Rec(service, 12, false, EncodeName(inst));          // answer: PTR
            Rec(inst, 16, true, txt.ToArray());                 // additional: TXT
            Rec(inst, 33, true, srv.ToArray());                 // additional: SRV
            Rec(HostName, 1, true, new byte[] { 127, 0, 0, 1 }); // additional: A
            return w.ToArray();
        }

        public void Dispose() => Stop();
    }
}
