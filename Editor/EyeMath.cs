using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VRCFTMapper.Editor
{
    /// <summary>
    /// 目周りの加工。プレビュー (C#) とビルド (BlendTree のサンプル点) で同じ関数を使い、見た目を一致させる。
    ///
    /// 処理順:
    ///   入力 (-1..1, +右/+上)
    ///   → Shape: オフセット → 倍率 → デッドゾーン → カーブ (|x| に適用、符号は保持)
    ///   → 輻輳: c = (L+R)/2, v = (L-R)/2 (v>0 = 寄り目) → v' = ソフト上限(v × 反映率)
    ///   → 出力 L = c + v', R = c − v' (+ 揺らぎ)
    /// </summary>
    public static class EyeMath
    {
        public struct Result { public float left, right, y; }

        public static float Shape(EyeSettings e, float x, bool isY)
        {
            x = (x + (isY ? e.offsetY : e.offsetX)) * (isY ? e.gainY : e.gainX);
            float a = Mathf.Abs(x);
            a = e.deadzone >= 0.999f ? 0 : Mathf.Max(0, (a - e.deadzone) / (1 - e.deadzone));
            a = Mathf.Clamp01(a);
            if (e.useCurve && e.curve != null && e.curve.length >= 2) a = e.curve.Evaluate(a);
            return Mathf.Sign(x) * a;
        }

        /// <summary>寄り目 (v&gt;0) / 外開き (v&lt;0) を tanh で滑らかに上限処理。</summary>
        public static float SoftVerge(EyeSettings e, float v)
        {
            v *= e.vergenceScale;
            float lim = v >= 0 ? e.maxConverge : e.maxDiverge;
            if (lim < 1e-4f) return 0;
            return Mathf.Sign(v) * lim * Tanh(Mathf.Abs(v) / lim);
        }

        static float Tanh(float x) { float e2 = Mathf.Exp(2 * x); return (e2 - 1) / (e2 + 1); }

        public static Result Process(EyeSettings e, float xl, float xr, float y, bool combined, Vector2 jitter)
        {
            float sl = Shape(e, xl, false), sr = combined ? sl : Shape(e, xr, false), sy = Shape(e, y, true);
            float c = combined ? sl : (sl + sr) * 0.5f;
            float vp = combined ? 0 : SoftVerge(e, (sl - sr) * 0.5f);
            return new Result { left = c + vp + jitter.x, right = c - vp + jitter.x, y = sy + jitter.y };
        }

        // ================= 揺らぎ =================

        const float SampleStep = 1f / 50f;
        public const float JitterLength = 30f;

        static int cacheKey;
        static List<(float t, Vector2 p)> cacheSaccades;

        /// <summary>
        /// 時刻 t の揺らぎ。30 秒でループ (始点と終点が一致するよう周期と跳びを調整)。
        ///  - 固視微動: 周期 1/n (n は 30 秒で割り切れる回数) の正弦の和
        ///  - マイクロサッカード: ランダムな間隔で小さく跳び、約 40ms で移動して留まる
        /// </summary>
        public static Vector2 Jitter(EyeSettings e, float t)
        {
            t = Mathf.Repeat(t, JitterLength);
            float L = JitterLength;
            float w(float hz) => 2 * Mathf.PI * Mathf.Round(hz * L) / L;
            var tremor = new Vector2(
                Mathf.Sin(w(1.3f) * t) * 0.5f + Mathf.Sin(w(3.1f) * t + 1.7f) * 0.3f + Mathf.Sin(w(7.3f) * t + 0.4f) * 0.2f,
                Mathf.Sin(w(1.7f) * t + 2.1f) * 0.5f + Mathf.Sin(w(4.3f) * t + 0.9f) * 0.3f + Mathf.Sin(w(6.1f) * t + 2.6f) * 0.2f)
                * e.tremor;

            var sacc = Saccades(e);
            Vector2 prev = Vector2.zero, cur = Vector2.zero;
            float t0 = 0;
            foreach (var (st, p) in sacc)
            {
                if (st > t) break;
                prev = cur; cur = p; t0 = st;
            }
            const float move = 0.04f;
            var s = t - t0 < move ? Vector2.Lerp(prev, cur, Mathf.SmoothStep(0, 1, (t - t0) / move)) : cur;
            return tremor + s * e.saccade;
        }

        static List<(float, Vector2)> Saccades(EyeSettings e)
        {
            int key = e.seed * 7919 + Mathf.RoundToInt(e.saccadeInterval * 1000);
            if (cacheSaccades != null && cacheKey == key) return cacheSaccades;
            var rnd = new System.Random(e.seed);
            var list = new List<(float, Vector2)>();
            float t = (float)rnd.NextDouble() * e.saccadeInterval;
            while (t < JitterLength - 0.5f)
            {
                // 単位円内の一様ランダム (振幅は Jitter() で掛ける)
                double ang = rnd.NextDouble() * Math.PI * 2, r = Math.Sqrt(rnd.NextDouble());
                list.Add((t, new Vector2((float)(Math.Cos(ang) * r), (float)(Math.Sin(ang) * r * 0.6))));
                t += e.saccadeInterval * (0.4f + (float)rnd.NextDouble() * 1.2f);
            }
            list.Add((JitterLength - 0.3f, Vector2.zero)); // ループ境界で中央へ戻す
            cacheKey = key; cacheSaccades = list;
            return list;
        }

        /// <summary>揺らぎを AnimationCurve に焼き込む (ビルド用)。</summary>
        public static (AnimationCurve x, AnimationCurve y) BakeJitter(EyeSettings e)
        {
            var kx = new List<Keyframe>(); var ky = new List<Keyframe>();
            int n = Mathf.RoundToInt(JitterLength / SampleStep);
            for (int i = 0; i <= n; i++)
            {
                float t = i * SampleStep;
                var j = Jitter(e, i == n ? 0 : t); // 終点 = 始点
                kx.Add(new Keyframe(t, j.x)); ky.Add(new Keyframe(t, j.y));
            }
            var cx = new AnimationCurve(kx.ToArray()); var cy = new AnimationCurve(ky.ToArray());
            Linearize(cx); Linearize(cy);
            return (cx, cy);
        }

        static void Linearize(AnimationCurve c)
        {
            var keys = c.keys;
            for (int i = 0; i < keys.Length; i++)
            {
                float inT = i > 0 ? (keys[i].value - keys[i - 1].value) / (keys[i].time - keys[i - 1].time) : 0;
                float outT = i < keys.Length - 1 ? (keys[i + 1].value - keys[i].value) / (keys[i + 1].time - keys[i].time) : 0;
                keys[i].inTangent = inT; keys[i].outTangent = outT;
            }
            c.keys = keys;
        }

        // ================= 補助 =================

        /// <summary>視線系マッピングがどちらの目の値を使うか。</summary>
        public static bool IsLeft(FTMapping m)
        {
            if (m.eyeSide == EyeSide.Left) return true;
            if (m.eyeSide == EyeSide.Right) return false;
            if (m.parameter == "EyeLeftX" || m.parameter == "EyeLeftY") return true;
            if (m.parameter == "EyeRightX" || m.parameter == "EyeRightY") return false;
            var n = AutoSetup.Normalize(m.blendShape);
            return !n.EndsWith("r");
        }

        public static bool IsYParam(string p) => p == "EyeY" || p == "EyeLeftY" || p == "EyeRightY";

        /// <summary>まぶた追従の対象シェイプキーをマッピングから推定。</summary>
        public static void AutoDetectLids(VRCFTMapper mapper)
        {
            var e = mapper.eye;
            FTMapping Closing(params string[] ps) => mapper.mappings.FirstOrDefault(m =>
                ps.Contains(m.parameter) && m.renderer != null && m.inMin < 0.76f && m.outMin > m.outMax);
            FTMapping Opening(params string[] ps) => mapper.mappings.FirstOrDefault(m =>
                ps.Contains(m.parameter) && m.renderer != null && m.inMin >= 0.74f && m.outMax > m.outMin);

            var dl = Closing("EyeLidLeft", "EyeLid"); var dr = Closing("EyeLidRight", "EyeLid");
            var ul = Opening("EyeLidLeft", "EyeLid"); var ur = Opening("EyeLidRight", "EyeLid");
            e.lidRenderer = (dl ?? dr ?? ul ?? ur)?.renderer ?? e.lidRenderer;
            e.lidDownLeft = dl?.blendShape ?? ""; e.lidDownRight = dr?.blendShape ?? "";
            e.lidUpLeft = ul?.blendShape ?? ""; e.lidUpRight = ur?.blendShape ?? "";
            // 両目共通シェイプ (例: まばたき) の場合は片側だけにする (二重加算を防ぐ)
            if (e.lidDownLeft == e.lidDownRight) e.lidDownRight = "";
            if (e.lidUpLeft == e.lidUpRight) e.lidUpRight = "";
        }

        /// <summary>まぶた追従量 (0..1)。openness = まぶたの開き具合 (0 閉 .. 1 開)。閉じているほど効果を弱める。</summary>
        public static (float down, float up) LidFollow(EyeSettings e, float y, float openness)
        {
            openness = Mathf.Clamp01(openness);
            return (Mathf.Max(0, -y) * e.lidFollowDown * openness, Mathf.Max(0, y) * e.lidFollowUp * openness);
        }

        /// <summary>注視距離 d (m) と瞳孔間距離 ipd (m) から、片目あたりの内寄せ量 (入力単位)。</summary>
        public static float VergenceForDistance(float distance, float ipd, float degreesPerUnit)
        {
            if (distance <= 0 || degreesPerUnit <= 0) return 0;
            return Mathf.Atan2(ipd * 0.5f, distance) * Mathf.Rad2Deg / degreesPerUnit;
        }
    }
}
