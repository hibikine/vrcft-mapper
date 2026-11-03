using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace VRCFTMapper.Editor
{
    /// <summary>
    /// マッピングを VRChat ネイティブ Face Tracking の設定 (Blendshapes Set / Parameter Behavior) に変換する計画。
    /// UI の状態表示とビルドの両方で使う。
    ///
    /// 変換の方針:
    ///  - 単一キー・線形・0 始まりで最大値が 1 になるもの → Blendshapes Set (倍率) … 最も軽い
    ///  - それ以外 (範囲・反転・カーブ・簡易パラメータ・同じシェイプへの複数マッピング) → Apply to Blendshape + Custom 式
    ///  - out が 0..1 を超える / 負になる → 変形量を拡大 / 反転したシェイプキーを生成して、その 0..1 で表す
    ///  - 目ボーンの加工が必要な視線だけ Forward to Animator (Forwarded Parameters はランクに直結するため最小限)
    /// </summary>
    public class NativePlan
    {
        public const string SetEye = "VRCFT Mapper (Eye)";
        public const string SetFace = "VRCFT Mapper (Face)";
        public const string SetAlways = "VRCFT Mapper";
        public const string ForwardPrefix = "VRCFTM/Native/";
        public const string ShapePrefix = "VRCFTM/";

        public class SetEntry { public string set, key, shape; public float multiplier; }
        public class Behavior
        {
            public string shape;           // Apply to Blendshape の対象 (Forward のときは null)
            public string forwardParam;    // Forward to Animator の出力先
            public string weightSet;       // null = 常に全強度
            public int limit;              // 0 = 適用, 1 = 上限 (Limit Max), 2 = 下限 (Limit Min)
            public string expr;
            public int instructions, operations;
            public bool valid = true;
        }
        public class GeneratedShape { public SkinnedMeshRenderer renderer; public string source, name; public float scale; }
        public class Status { public string text; public bool error, warning; }

        public readonly List<SetEntry> setEntries = new List<SetEntry>();
        public readonly List<Behavior> behaviors = new List<Behavior>();
        public readonly List<GeneratedShape> generated = new List<GeneratedShape>();
        public readonly Dictionary<FTMapping, Status> status = new Dictionary<FTMapping, Status>();
        public readonly Dictionary<FTLimit, Status> limitStatus = new Dictionary<FTLimit, Status>();
        public readonly List<string> warnings = new List<string>();
        public readonly HashSet<SkinnedMeshRenderer> meshes = new HashSet<SkinnedMeshRenderer>();
        public int Forwarded => behaviors.Count(b => b.forwardParam != null);
        public int Instructions => behaviors.Sum(b => b.instructions);
        public int Operations;

        // ---------------- 変換 ----------------

        class Part { public FTMapping m; public string shape; public string expr; public string gateSet; public SkinnedMeshRenderer renderer; public SetEntry setCandidate; }

        public static NativePlan Build(VRCFTMapper mapper)
        {
            var plan = new NativePlan();
            var parts = new List<Part>();
            int segs = Mathf.Clamp(mapper.nativeCurveSegments, 2, 16);

            foreach (var m in mapper.mappings)
            {
                if (!m.enabled) continue;
                if (m.renderer == null || string.IsNullOrEmpty(m.blendShape)) { plan.status[m] = new Status { text = "未設定", warning = true }; continue; }
                bool gaze = !m.useExpression && VRCFTMapper.IsGaze(m.parameter);
                if (gaze && mapper.gazeMode != GazeMode.BlendShape)
                {
                    plan.status[m] = new Status { text = mapper.gazeMode == GazeMode.None ? "視線: 無効" : "視線: ボーン側で処理", warning = mapper.gazeMode == GazeMode.None };
                    continue;
                }
                string x;
                if (m.useExpression)
                {
                    x = (m.expression ?? "").Trim();
                    var unknown = NativeKeys.UnknownNames(x);
                    if (x.Length == 0) { plan.status[m] = new Status { text = "式が空です", error = true }; continue; }
                    if (unknown.Count > 0) { plan.status[m] = new Status { text = "式に不明な名前: " + string.Join(", ", unknown), error = true }; continue; }
                    if (!Estimate(x).valid) { plan.status[m] = new Status { text = "式の書き方が正しくありません", error = true }; continue; }
                }
                else x = NativeKeys.ExprFor(m.parameter);
                if (x == null) { plan.status[m] = new Status { text = "変換不可: 対応キーなし", error = true }; continue; }
                plan.meshes.Add(m.renderer);

                // in → out を折れ線の式に (FTMath.Evaluate と同じ関数)。カーブは式が上限に収まるまで区間数を減らす
                float lo = Mathf.Min(m.inMin, m.inMax), hi = Mathf.Max(m.inMin, m.inMax);
                if (hi - lo < 1e-4f) hi = lo + 1e-4f;
                int n = m.useCurve ? segs : 1;
                float[] xs = null, ys = null;
                string f = null;
                for (; n >= 1; n--)
                {
                    xs = Enumerable.Range(0, n + 1).Select(i => Mathf.Lerp(lo, hi, i / (float)n)).ToArray();
                    ys = xs.Select(v => FTMath.Evaluate(m, v)).ToArray();
                    f = PiecewiseExpr(x, xs, ys);
                    // 負の部分や拡大の係数で包まれる分 (約 40 文字・数命令) の余裕を見る
                    var (ok, ins, _) = Estimate($"max(-({f}), 0) * 1.5");
                    if (ok && f.Length + 40 <= MaxExprLength && ins <= MaxExprInstructions) break;
                    if (!m.useCurve) break;
                }
                n = Mathf.Max(n, 1);
                float maxY = ys.Max(), minY = ys.Min();

                string gateSet = GateSet(m);
                var notes = new List<string>();
                // 正の部分
                if (maxY > 1e-4f)
                {
                    float s = maxY > 1.0001f && m.overshootShape ? maxY : 1f;
                    if (maxY > 1.0001f && !m.overshootShape) notes.Add("1 で頭打ち");
                    string shape = m.blendShape;
                    if (s > 1f) { shape = ShapeName(m.blendShape, s); plan.AddGenerated(m.renderer, m.blendShape, shape, s); notes.Add($"生成 ×{NativeKeys.Fmt(s)}"); }
                    string pos = minY < -1e-4f ? $"max({f}, 0)" : f;
                    if (s > 1f) pos = $"({pos}) * {NativeKeys.Fmt(1 / s)}";
                    var part = new Part { m = m, shape = shape, expr = pos, gateSet = gateSet, renderer = m.renderer };
                    // Blendshapes Set で表せるか: 単一キー・カーブなし・0 始まり・最大が正規化後 1
                    if (NativeKeys.IsSingleKey(x) && !m.useCurve && Mathf.Abs(m.inMin) < 1e-4f && m.inMax > 1e-4f &&
                        Mathf.Abs(m.outMin) < 1e-4f && Mathf.Abs(m.outMax / s - 1) < 1e-4f)
                        part.setCandidate = new SetEntry { set = gateSet, key = x, shape = shape, multiplier = 1 / m.inMax };
                    parts.Add(part);
                }
                // 負の部分 (反転シェイプ)
                if (minY < -1e-4f)
                {
                    if (m.overshootShape)
                    {
                        float s = -minY;
                        string shape = ShapeName(m.blendShape, -s);
                        plan.AddGenerated(m.renderer, m.blendShape, shape, -s);
                        parts.Add(new Part { m = m, shape = shape, expr = $"max(-({f}), 0) * {NativeKeys.Fmt(1 / s)}", gateSet = gateSet, renderer = m.renderer });
                        notes.Add($"生成 ×-{NativeKeys.Fmt(s)}");
                    }
                    else notes.Add("負の値は 0");
                }
                if (m.useCurve) notes.Add(n < segs ? $"カーブ {n} 区間 (式の上限のため {segs} から削減)" : $"カーブ {n} 区間");
                if (m.useExpression) notes.Add("式入力");
                else if (!NativeKeys.IsKey(m.parameter)) notes.Add("簡易パラメータ→式");
                if (gaze) notes.Add("目の加工なし");
                plan.status[m] = new Status { text = string.Join(" / ", notes), warning = notes.Contains("1 で頭打ち") || notes.Contains("負の値は 0") };
            }

            // まぶた追従 (下を見る → 上まぶたを下げる / 上を見る → 見開く)。閉じているほど弱める
            var e = mapper.eye;
            if (mapper.gazeMode != GazeMode.None && e.lidRenderer != null && (e.lidFollowDown > 0 || e.lidFollowUp > 0))
            {
                plan.meshes.Add(e.lidRenderer);
                foreach (bool left in new[] { true, false })
                {
                    string s = left ? "Left" : "Right";
                    string y = NativeKeys.ExprFor("Eye" + s + "Y");
                    string open = $"clamp((1 - EyeClosed{s}), 0, 1)";
                    void Add(string shape, string expr)
                    {
                        if (string.IsNullOrEmpty(shape)) return;
                        parts.Add(new Part { m = null, shape = shape, expr = expr, gateSet = SetEye, renderer = e.lidRenderer });
                    }
                    if (e.lidFollowDown > 0) Add(left ? e.lidDownLeft : e.lidDownRight, $"max(-({y}), 0) * {NativeKeys.Fmt(e.lidFollowDown)} * {open}");
                    if (e.lidFollowUp > 0) Add(left ? e.lidUpLeft : e.lidUpRight, $"max({y}, 0) * {NativeKeys.Fmt(e.lidFollowUp)} * {open}");
                }
            }

            // シェイプキーごとにまとめる。1 つだけで Set に載せられ、そのキーがまだ Set で使われていなければ Set、それ以外は式の合計
            var usedKeys = new HashSet<(string set, string key)>();
            foreach (var g in parts.GroupBy(p => p.shape))
            {
                var list = g.ToList();
                var gates = list.Select(p => p.gateSet).Distinct().ToList();
                if (gates.Count > 1) plan.warnings.Add($"{g.Key}: Eye / Face の異なるマッピングが同じシェイプキーを動かしています ({string.Join(", ", gates.Select(x => x ?? "常時"))})");
                if (list.Count == 1 && list[0].setCandidate != null && usedKeys.Add((list[0].setCandidate.set, list[0].setCandidate.key)))
                {
                    plan.setEntries.Add(list[0].setCandidate);
                    if (list[0].m != null && plan.status.TryGetValue(list[0].m, out var st)) st.text = Prefix("直結 (Set)", st.text);
                    continue;
                }
                var expr = list.Count == 1 ? list[0].expr : string.Join(" + ", list.Select(p => "(" + p.expr + ")"));
                plan.behaviors.Add(new Behavior { shape = g.Key, weightSet = gates[0], expr = expr });
                foreach (var p in list)
                    if (p.m != null && plan.status.TryGetValue(p.m, out var st))
                        st.text = Prefix(list.Count > 1 ? "式 (合算)" : "式", st.text);
            }

            // 上限 / 下限 (Limit Blendshape)。対象シェイプの最終値を、式の値で制限する
            foreach (var l in mapper.limits)
            {
                if (!l.enabled) continue;
                var expr = (l.expression ?? "").Trim();
                var unknown = NativeKeys.UnknownNames(expr);
                if (string.IsNullOrEmpty(l.blendShape) || expr.Length == 0) { plan.limitStatus[l] = new Status { text = "未設定", warning = true }; continue; }
                if (unknown.Count > 0) { plan.limitStatus[l] = new Status { text = "式に不明な名前: " + string.Join(", ", unknown), error = true }; continue; }
                plan.behaviors.Add(new Behavior { shape = l.blendShape, expr = expr, limit = l.isMin ? 2 : 1 });
                plan.limitStatus[l] = new Status { text = l.isMin ? "下限" : "上限" };
            }

            // 目ボーン: 加工 (寄り目制限・カーブ・揺らぎ) はアニメーターで行うため、視線だけ転送
            if (mapper.gazeMode == GazeMode.Bone)
                foreach (var p in NativeGazeParams(mapper))
                    plan.behaviors.Add(new Behavior { forwardParam = ForwardPrefix + p, expr = NativeKeys.ExprFor(p) });

            plan.Validate();
            return plan;
        }

        static string Prefix(string head, string text)
        {
            if (text.StartsWith("直結") || text.StartsWith("式")) return text.StartsWith(head) ? text : head + " + " + text;
            return text.Length > 0 ? head + " / " + text : head;
        }

        public static string[] NativeGazeParams(VRCFTMapper mapper) =>
            mapper.gazeCombined ? new[] { "EyeX", "EyeY" } : new[] { "EyeLeftX", "EyeRightX", "EyeY" };

        static string GateSet(FTMapping m)
        {
            var g = m.gate;
            if (g == FTGate.Auto)
                g = m.useExpression ? (NativeKeys.UsesEyeKeys(m.expression) ? FTGate.Eye : FTGate.Lip) : FTCatalog.AutoGate(m.parameter);
            if (!m.useExpression && VRCFTMapper.IsGaze(m.parameter)) g = FTGate.Eye;
            return g == FTGate.Eye ? SetEye : g == FTGate.Lip ? SetFace : SetAlways;
        }

        public static string ShapeName(string src, float scale) => ShapePrefix + src + "*" + NativeKeys.Fmt(scale);

        void AddGenerated(SkinnedMeshRenderer r, string src, string name, float scale)
        {
            if (generated.Any(x => x.renderer == r && x.name == name)) return;
            generated.Add(new GeneratedShape { renderer = r, source = src, name = name, scale = scale });
        }

        /// <summary>y0 + Σ dy_i * clamp((x - x_i) * k_i, 0, 1)。区間外は端の値に張り付く。</summary>
        static string PiecewiseExpr(string x, float[] xs, float[] ys)
        {
            string X = NativeKeys.IsKey(x) ? x : "(" + x + ")";
            var terms = new List<string>();
            if (Mathf.Abs(ys[0]) > 1e-5f) terms.Add(NativeKeys.Fmt(ys[0]));
            for (int i = 0; i < xs.Length - 1; i++)
            {
                float dy = ys[i + 1] - ys[i];
                if (Mathf.Abs(dy) < 1e-5f) continue;
                float k = 1 / (xs[i + 1] - xs[i]);
                string inner = Mathf.Abs(xs[i]) < 1e-6f ? $"{X} * {NativeKeys.Fmt(k)}" : $"({X} - {NativeKeys.Fmt(xs[i])}) * {NativeKeys.Fmt(k)}";
                if (Mathf.Abs(k - 1) < 1e-6f) inner = Mathf.Abs(xs[i]) < 1e-6f ? X : $"{X} - {NativeKeys.Fmt(xs[i])}";
                string c = $"clamp({inner}, 0, 1)";
                terms.Add(Mathf.Abs(dy - 1) < 1e-6f ? c : Mathf.Abs(dy + 1) < 1e-6f ? "-" + c : $"{NativeKeys.Fmt(dy)} * {c}");
            }
            if (terms.Count == 0) return "0";
            var joined = string.Join(" + ", terms).Replace("+ -", "- ");
            return joined.StartsWith("-") ? "0 " + joined.Substring(0, 1) + " " + joined.Substring(1) : joined;
        }

        // ---------------- 検証 (SDK の見積もりを使用) ----------------

        static MethodInfo estimate;
        static bool estimateResolved;

        public static (bool valid, int instructions, int operations) Estimate(string expr)
        {
            if (!estimateResolved)
            {
                estimateResolved = true;
                var t = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("VRC.SDK3.Avatars.FaceTrackingPerformanceScanner")).FirstOrDefault(x => x != null);
                estimate = t?.GetMethod("EstimateExpressionCost", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }
            if (estimate == null) return (true, expr.Count(ch => "+-*/(".Contains(ch)), expr.Count(ch => "+-*/".Contains(ch)));
            var r = estimate.Invoke(null, new object[] { expr, true });
            var rt = r.GetType();
            T F<T>(string n) => (T)(rt.GetField(n)?.GetValue(r) ?? rt.GetProperty(n)?.GetValue(r) ?? default(T));
            return (F<bool>("IsValid"), F<int>("Instructions"), F<int>("Operations"));
        }

        public const int MaxExprLength = 1024, MaxExprInstructions = 256, MaxTotalInstructions = 2048,
                         MaxBehaviors = 128, MaxMeshes = 16;

        void Validate()
        {
            int ops = 0;
            foreach (var b in behaviors)
            {
                var (valid, ins, op) = Estimate(b.expr);
                b.valid = valid && b.expr.Length <= MaxExprLength && ins <= MaxExprInstructions;
                b.instructions = ins; b.operations = op;
                if (!b.valid) warnings.Add($"式が無効または上限超過 ({b.shape ?? b.forwardParam}: {b.expr.Length} 文字 / {ins} 命令)");
                ops += Math.Max(1, op) * (b.shape != null ? MeshesWith(b.shape) : 1);
            }
            foreach (var s in setEntries) ops += MeshesWith(s.shape);
            Operations = ops;
            if (Instructions > MaxTotalInstructions) warnings.Add($"式の命令数の合計が上限を超えています ({Instructions} / {MaxTotalInstructions})");
            if (behaviors.Count > MaxBehaviors) warnings.Add($"Parameter Behavior が上限を超えています ({behaviors.Count} / {MaxBehaviors})");
            if (meshes.Count > MaxMeshes) warnings.Add($"Face Tracking Meshes が上限を超えています ({meshes.Count} / {MaxMeshes})");
        }

        int MeshesWith(string shape)
        {
            int n = 0;
            foreach (var r in meshes)
                if (r != null && r.sharedMesh != null && (r.sharedMesh.GetBlendShapeIndex(shape) >= 0 || generated.Any(gs => gs.renderer == r && gs.name == shape))) n++;
            return Math.Max(1, n);
        }

        // ---------------- ランク ----------------
        public static string Rank(int v, int[] limits)
        {
            string[] names = { "Excellent", "Good", "Medium", "Poor" };
            for (int i = 0; i < limits.Length; i++) if (v <= limits[i]) return names[i];
            return "Very Poor";
        }
        public static readonly int[] OpsLimits = { 2000, 4000, 8000, 16000 };
        public static readonly int[] ForwardLimits = { 10, 20, 30, 40 };
    }
}
