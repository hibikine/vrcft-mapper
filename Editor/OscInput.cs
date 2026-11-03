using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace VRCFTMapper.Editor
{
    /// <summary>OSC 受信 (UDP) / 録画 / 再生。値はパラメータ名 → float の辞書に集約。</summary>
    public class OscInput : IDisposable
    {
        public enum Source { Manual, OscLive, Playback }

        public Source source = Source.Manual;
        public int port = 9002;
        public readonly Dictionary<string, float> values = new Dictionary<string, float>();
        public string status = "";
        public int receivedCount;
        /// <summary>デバッグ用: アドレスごとの最新値 (ソースに関係なく記録)。</summary>
        public readonly SortedDictionary<string, float> lastSeen = new SortedDictionary<string, float>();

        UdpClient udp;
        Thread thread;
        volatile bool running;
        readonly ConcurrentQueue<(string addr, float v)> queue = new ConcurrentQueue<(string, float)>();

        // 録画
        public bool recording;
        double recStart;
        OscRecording recBuffer;

        // 再生
        public OscRecording clip;
        public bool playing;
        public bool loop = true;
        public float playhead;
        int playIndex;
        double lastTick;

        public bool Listening => running;

        public void StartListen()
        {
            StopListen();
            try
            {
                udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                running = true;
                thread = new Thread(Loop) { IsBackground = true, Name = "VRCFT Mapper OSC" };
                thread.Start();
                status = $"ポート {port} で受信中";
            }
            catch (Exception e) { status = "受信開始失敗: " + e.Message; running = false; }
        }

        public void StopListen()
        {
            running = false;
            try { udp?.Close(); } catch { }
            udp = null;
            thread = null;
            if (status.StartsWith("ポート")) status = "停止";
        }

        void Loop()
        {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            while (running)
            {
                try
                {
                    var data = udp.Receive(ref ep);
                    Parse(data, 0, data.Length);
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { break; }
                catch (Exception) { }
            }
        }

        // ---- OSC パーサ (message / bundle, f i T F) ----
        void Parse(byte[] d, int off, int len)
        {
            int end = off + len;
            var addr = ReadStr(d, ref off);
            if (addr == "#bundle")
            {
                off += 8; // timetag
                while (off + 4 <= end)
                {
                    int size = ReadInt(d, ref off);
                    if (size <= 0 || off + size > end) break;
                    Parse(d, off, size);
                    off += size;
                }
                return;
            }
            if (off >= end) return;
            var tags = ReadStr(d, ref off);
            if (tags.Length < 2 || tags[0] != ',') return;
            float v;
            switch (tags[1])
            {
                case 'f': v = ReadFloat(d, ref off); break;
                case 'i': v = ReadInt(d, ref off); break;
                case 'T': v = 1; break;
                case 'F': v = 0; break;
                default: return;
            }
            const string pre = "/avatar/parameters/";
            if (addr.StartsWith(pre)) addr = addr.Substring(pre.Length);
            queue.Enqueue((addr, v));
        }

        static string ReadStr(byte[] d, ref int off)
        {
            int s = off;
            while (off < d.Length && d[off] != 0) off++;
            var str = Encoding.UTF8.GetString(d, s, off - s);
            off = (off + 4) & ~3;
            return str;
        }
        static int ReadInt(byte[] d, ref int off)
        {
            int v = (d[off] << 24) | (d[off + 1] << 16) | (d[off + 2] << 8) | d[off + 3];
            off += 4; return v;
        }
        static float ReadFloat(byte[] d, ref int off)
        {
            var b = new[] { d[off + 3], d[off + 2], d[off + 1], d[off] };
            off += 4; return BitConverter.ToSingle(b, 0);
        }

        /// <summary>EditorApplication.update から呼ぶ。値が変わったら true。</summary>
        public bool Tick()
        {
            bool changed = false;
            double now = EditorApplication.timeSinceStartup;
            while (queue.TryDequeue(out var e))
            {
                receivedCount++;
                lastSeen[e.addr] = e.v;
                if (recording && recBuffer != null)
                    recBuffer.samples.Add(new OscSample { time = (float)(now - recStart), address = e.addr, value = e.v });
                if (source == Source.OscLive) { values[e.addr] = e.v; changed = true; }
            }

            if (source == Source.Playback && playing && clip != null && clip.samples.Count > 0)
            {
                float dt = (float)(now - lastTick);
                playhead += dt;
                if (playhead > clip.length)
                {
                    if (loop) { Seek(0); }
                    else { playhead = clip.length; playing = false; }
                }
                while (playIndex < clip.samples.Count && clip.samples[playIndex].time <= playhead)
                {
                    var s = clip.samples[playIndex++];
                    values[s.address] = s.value;
                    changed = true;
                }
            }
            lastTick = now;
            return changed;
        }

        public void Seek(float t)
        {
            if (clip == null) return;
            playhead = Mathf.Clamp(t, 0, clip.length);
            values.Clear();
            playIndex = 0;
            while (playIndex < clip.samples.Count && clip.samples[playIndex].time <= playhead)
            {
                var s = clip.samples[playIndex++];
                values[s.address] = s.value;
            }
        }

        public void BeginRecord()
        {
            if (!running) StartListen();
            recBuffer = ScriptableObject.CreateInstance<OscRecording>();
            recStart = EditorApplication.timeSinceStartup;
            recording = true;
        }

        public OscRecording EndRecord()
        {
            recording = false;
            if (recBuffer == null) return null;
            recBuffer.length = (float)(EditorApplication.timeSinceStartup - recStart);
            var path = EditorUtility.SaveFilePanelInProject("OSC 録画を保存", "VRCFT_Recording", "asset", "");
            var r = recBuffer;
            recBuffer = null;
            if (string.IsNullOrEmpty(path)) { UnityEngine.Object.DestroyImmediate(r); return null; }
            AssetDatabase.CreateAsset(r, path);
            Undo.RegisterCreatedObjectUndo(r, "OSC 録画を保存");
            AssetDatabase.SaveAssets();
            return r;
        }

        /// <summary>受信値からマッピングの入力値を得る (Float / Binary デコード)。prefix 違いも許容。</summary>
        public bool TryGetInput(VRCFTMapper mapper, FTMapping m, out float v)
        {
            v = 0;
            if (m.type == FTParamType.Float)
                return TryFind(mapper, m.parameter, out v);

            bool any = false;
            int steps = (1 << m.binaryBits) - 1;
            float sum = 0;
            for (int b = 0; b < m.binaryBits; b++)
                if (TryFind(mapper, m.parameter + (1 << b), out var bit)) { any = true; if (bit > 0.5f) sum += 1 << b; }
            if (!any)
                return TryFind(mapper, m.parameter, out v); // float で来ている場合
            v = sum / steps;
            if (m.signed && TryFind(mapper, m.parameter + "Negative", out var neg) && neg > 0.5f) v = -v;
            return true;
        }

        public bool TryFind(VRCFTMapper mapper, string name, out float v)
        {
            if (values.TryGetValue(mapper.prefix + name, out v)) return true;
            if (values.TryGetValue(name, out v)) return true;
            if (values.TryGetValue("v2/" + name, out v)) return true;
            if (values.TryGetValue("FT/v2/" + name, out v)) return true;
            var suffix = "/v2/" + name;
            foreach (var kv in values)
                if (kv.Key.EndsWith(suffix)) { v = kv.Value; return true; }
            return false;
        }

        public void Dispose() => StopListen();
    }
}
