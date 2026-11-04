using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;
using VRC.SDK3.Avatars.Components;

namespace VRCFTMapper.Editor.Mcp
{
    /// <summary>
    /// UnityMCP (jp.shiranui-isuzu.unity-mcp) 用のツール。
    /// UnityMCP が入っているときだけコンパイルされる (asmdef の versionDefines)。
    /// </summary>
    public static class VRCFTMcpTools
    {
        const string MapperArg = "VRCFT Mapper が付いた GameObject のシーンパス (例: \"Kyui FT\")。省略時はシーン内に 1 つだけあるものを使う。";

        // ------------------------------------------------------------ 共通
        static string PathOf(Transform t) => t == null ? "" : (t.parent ? PathOf(t.parent) + "/" : "") + t.name;

        static VRCFTMapper Resolve(string mapper)
        {
            var all = Resources.FindObjectsOfTypeAll<VRCFTMapper>()
                .Where(m => !EditorUtility.IsPersistent(m) && m.gameObject.scene.IsValid() && (m.hideFlags & HideFlags.HideInHierarchy) == 0)
                .ToList();
            if (!string.IsNullOrEmpty(mapper))
            {
                var hit = all.Where(m => PathOf(m.transform) == mapper || m.name == mapper).ToList();
                if (hit.Count == 1) return hit[0];
                var desc = all.Where(m => { var d = m.GetComponentInParent<VRCAvatarDescriptor>(true); return d != null && (PathOf(d.transform) == mapper || d.name == mapper); }).ToList();
                if (desc.Count == 1) return desc[0];
                throw new ArgumentException($"VRCFT Mapper '{mapper}' が見つからない、または複数あります。候補: {string.Join(", ", all.Select(m => PathOf(m.transform)))}");
            }
            if (all.Count == 1) return all[0];
            throw new ArgumentException(all.Count == 0 ? "シーンに VRCFT Mapper がありません。" : $"VRCFT Mapper が複数あります。mapper で指定してください: {string.Join(", ", all.Select(m => PathOf(m.transform)))}");
        }

        static VRCAvatarDescriptor Avatar(VRCFTMapper m) => m.GetComponentInParent<VRCAvatarDescriptor>(true);

        static void Commit(VRCFTMapper m)
        {
            MirrorTools.SyncAll(m);
            EditorUtility.SetDirty(m);
            PrefabUtility.RecordPrefabInstancePropertyModifications(m);
        }

        static JObject MappingJson(VRCFTMapper owner, FTMapping x, int i) => new JObject
        {
            ["index"] = i,
            ["enabled"] = x.enabled,
            ["parameter"] = x.useExpression ? null : x.parameter,
            ["expression"] = x.useExpression ? x.expression : null,
            ["renderer"] = x.renderer ? x.renderer.name : null,
            ["blendShape"] = x.blendShape,
            ["inMin"] = x.inMin, ["inMax"] = x.inMax, ["outMin"] = x.outMin, ["outMax"] = x.outMax,
            ["useCurve"] = x.useCurve,
            ["style"] = x.style > 0 && x.style <= owner.styleGroup.options.Count ? owner.styleGroup.options[x.style - 1] : null,
            ["styleIndex"] = x.style,
            ["overshootShape"] = x.overshootShape,
            ["gate"] = x.gate.ToString(),
            ["smoothing"] = x.smoothing,
        };

        // ------------------------------------------------------------ ツール
        [McpTool("vrcft_status",
            "VRCFT Mapper (VRChat フェイストラッキング → シェイプキー) の設定を要約する。入力ごとのマッピング、スタイル切替、左右対称、外れたメッシュ参照、ネイティブ FT のコスト見積もり、同期パラメータ容量を返す。設定を変える前にまずこれを呼ぶ。",
            Idempotency = McpIdempotency.Safe)]
        public static JObject Status(
            [McpArg(MapperArg)] string mapper = null,
            [McpArg("true で各マッピングの全項目 (in/out 範囲など) を含める。false なら入力ごとのシェイプキー名だけ。")] bool detail = false)
        {
            var m = Resolve(mapper);
            var desc = Avatar(m);
            var groups = new JObject();
            for (int i = 0; i < m.mappings.Count; i++)
            {
                var x = m.mappings[i];
                var key = x.useExpression ? "ƒ " + x.expression : x.parameter ?? "";
                if (!(groups[key] is JArray arr)) groups[key] = arr = new JArray();
                arr.Add(detail ? (JToken)MappingJson(m, x, i) : $"#{i} {(x.enabled ? "" : "(off) ")}{x.blendShape}{(x.style > 0 ? " [" + MappingJson(m, x, i)["style"] + "]" : "")}");
            }
            var res = new JObject
            {
                ["mapper"] = PathOf(m.transform),
                ["avatar"] = desc ? PathOf(desc.transform) : null,
                ["outputMode"] = m.outputMode.ToString(),
                ["mappingCount"] = m.mappings.Count,
                ["inputs"] = groups,
                ["mirrored"] = new JArray(m.mirrored.Select(l => $"{l} → {MirrorTools.Counterpart(l)}")),
                ["brokenReferences"] = RefRepair.BrokenCount(m),
                ["styleGroup"] = m.styleGroup.enabled ? new JObject
                {
                    ["name"] = m.styleGroup.name,
                    ["options"] = new JArray(m.styleGroup.options),
                    ["auto"] = m.styleGroup.auto,
                    ["autoExpression"] = m.styleGroup.autoExpression,
                } : null,
                ["splits"] = new JArray(m.splits.Where(s => s.enabled).Select(s => s.source)),
            };
            if (m.outputMode == FTOutputMode.Native)
            {
                var plan = NativePlan.Build(m);
                res["native"] = new JObject
                {
                    ["forwarded"] = plan.Forwarded,
                    ["operations"] = plan.Operations,
                    ["instructions"] = plan.Instructions,
                    ["animatorMappings"] = plan.animatorMappings.Count,
                    ["warnings"] = new JArray(plan.warnings),
                    ["mappingErrors"] = new JArray(plan.status.Where(kv => kv.Value.error || kv.Value.warning)
                        .Select(kv => $"#{m.mappings.IndexOf(kv.Key)} {kv.Key.blendShape}: {kv.Value.text}")),
                };
            }
            if (desc)
            {
                var b = ParamBudget.Compute(desc, m);
                res["syncedBits"] = new JObject { ["vrcftMapper"] = b.ours, ["total"] = b.total, ["limit"] = b.limit };
            }
            return res;
        }

        [McpTool("vrcft_evaluate",
            "フェイストラッキングの入力値を与えたときに各シェイプキーへ入るウェイト (0..100) をエディタ上で計算する。シーンは変えない。式入力のマッピングは対象外。",
            Idempotency = McpIdempotency.Safe,
            Examples = new[] { "{\"inputs\": {\"EyeClosedLeft\": 0.4, \"MouthCornerPullLeft\": 0.8}, \"style\": \"半目\"}" })]
        public static JObject Evaluate(
            [McpArg("入力パラメータ名 → 値。省略したパラメータはカタログの安静値 (多くは 0)。")] Dictionary<string, float> inputs,
            [McpArg(MapperArg)] string mapper = null,
            [McpArg("スタイル切替の選択肢名 (例: \"半目\")。\"auto\" で style_auto の値から補間。省略時は自動。")] string style = null,
            [McpArg("style が auto のとき、自動判定の式の値 (-1..1)。")] float style_auto = 0)
        {
            var m = Resolve(mapper);
            var g = m.styleGroup;
            int sel = 0;
            if (!string.IsNullOrEmpty(style) && style != "auto")
            {
                sel = g.options.IndexOf(style) + 1;
                if (sel <= 0) throw new ArgumentException($"スタイル '{style}' はありません: {string.Join(", ", g.options)}");
            }
            var w = new Dictionary<string, float>();
            var skipped = new List<string>();
            foreach (var x in m.mappings)
            {
                if (!x.enabled || string.IsNullOrEmpty(x.blendShape)) continue;
                if (x.useExpression) { skipped.Add(x.blendShape); continue; }
                float v = inputs != null && inputs.TryGetValue(x.parameter, out var iv) ? iv : FTCatalog.Find(x.parameter)?.rest ?? 0f;
                float sw = StyleWeight(g, x, sel, style_auto);
                if (sw <= 0) continue;
                var key = (x.renderer ? x.renderer.name + "/" : "") + x.blendShape;
                w.TryGetValue(key, out var cur);
                w[key] = cur + FTMath.Evaluate(x, v) * 100f * sw;
            }
            var res = new JObject
            {
                ["weights"] = new JObject(w.Where(kv => Mathf.Abs(kv.Value) > 0.05f).OrderByDescending(kv => kv.Value).Select(kv => new JProperty(kv.Key, Math.Round(kv.Value, 1)))),
            };
            if (skipped.Count > 0) res["skippedExpressionMappings"] = new JArray(skipped.Distinct());
            return res;
        }

        static float StyleWeight(FTStyleGroup g, FTMapping m, int sel, float autoValue)
        {
            if (!g.enabled || m.style <= 0) return 1;
            int opt = m.style - 1;
            if (sel > 0 || !g.auto) return opt == Mathf.Max(0, sel - 1) ? 1 : 0;
            var pts = Enumerable.Range(0, g.options.Count).Where(k => k < g.autoValues.Count && !float.IsNaN(g.autoValues[k])).OrderBy(k => g.autoValues[k]).ToList();
            if (pts.Count == 0) return 0;
            if (autoValue <= g.autoValues[pts[0]]) return opt == pts[0] ? 1 : 0;
            if (autoValue >= g.autoValues[pts[pts.Count - 1]]) return opt == pts[pts.Count - 1] ? 1 : 0;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                float a = g.autoValues[pts[i]], b = g.autoValues[pts[i + 1]];
                if (autoValue < a || autoValue > b) continue;
                float t = Mathf.InverseLerp(a, b, autoValue);
                return opt == pts[i] ? 1 - t : opt == pts[i + 1] ? t : 0;
            }
            return 0;
        }

        [McpTool("vrcft_mapping_set",
            "マッピングを追加・変更する。index を指定すると既存を変更、省略すると追加。fields に変えたい項目だけ渡す: enabled, parameter, expression (式入力にする), renderer (メッシュ名), blendShape, inMin, inMax, outMin, outMax, useCurve, curve ([[t, v], ...] 0..1), style (選択肢名 / \"常に\"), overshootShape, gate (Auto/Eye/Lip/Always), smoothing (-1 = 全体設定)。左右対称の Right 側は Left 側から自動生成されるので、Left 側を変える。",
            UndoGroup = "VRCFT Mapper: マッピング変更",
            Examples = new[]
            {
                "{\"index\": 3, \"fields\": {\"inMin\": 0.1, \"inMax\": 0.75}}",
                "{\"fields\": {\"parameter\": \"MouthCornerPullLeft\", \"blendShape\": \"mouth_good\", \"outMax\": 0.6}}",
            })]
        public static JObject MappingSet(
            [McpArg("変更する項目。")] JObject fields,
            [McpArg("変更するマッピングの番号 (vrcft_status の #)。省略で新規追加。")] int index = -1,
            [McpArg(MapperArg)] string mapper = null)
        {
            var m = Resolve(mapper);
            Undo.RecordObject(m, "VRCFT Mapper: マッピング変更");
            FTMapping x;
            if (index < 0)
            {
                x = new FTMapping { renderer = Avatar(m) ? AutoSetup.FaceRenderers(Avatar(m).gameObject).FirstOrDefault() : null };
                m.mappings.Add(x);
                index = m.mappings.Count - 1;
            }
            else
            {
                if (index >= m.mappings.Count) throw new ArgumentException($"index {index} は範囲外です (0..{m.mappings.Count - 1})");
                x = m.mappings[index];
                var p = x.parameter;
                var left = MirrorTools.Counterpart(p);
                if (left != null && m.mirrored.Contains(left))
                    throw new ArgumentException($"{p} は {left} と左右対称で自動生成されています。{left} 側を変更するか、vrcft_mirror で対称を解除してください。");
            }
            Apply(m, x, fields ?? new JObject());
            Commit(m);
            int ni = m.mappings.IndexOf(x);
            return new JObject { ["index"] = ni, ["mapping"] = MappingJson(m, x, ni) };
        }

        static void Apply(VRCFTMapper owner, FTMapping x, JObject f)
        {
            foreach (var p in f.Properties())
            {
                var v = p.Value;
                switch (p.Name)
                {
                    case "renderer":
                        var av = Avatar(owner);
                        var name = (string)v;
                        x.renderer = av ? av.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault(r => r.name == name) : null;
                        if (x.renderer == null) throw new ArgumentException($"メッシュ '{name}' がアバター内にありません");
                        break;
                    case "expression":
                        x.expression = (string)v; x.useExpression = !string.IsNullOrEmpty(x.expression);
                        break;
                    case "parameter":
                        x.parameter = (string)v; x.useExpression = false;
                        x.signed = FTCatalog.Find(x.parameter)?.signed ?? false;
                        break;
                    case "style":
                        var s = (string)v;
                        if (string.IsNullOrEmpty(s) || s == "常に" || s == "always") x.style = 0;
                        else
                        {
                            int k = owner.styleGroup.options.IndexOf(s);
                            if (k < 0) throw new ArgumentException($"スタイル '{s}' はありません: {string.Join(", ", owner.styleGroup.options)}");
                            x.style = k + 1;
                        }
                        break;
                    case "gate":
                        x.gate = (FTGate)Enum.Parse(typeof(FTGate), (string)v, true);
                        break;
                    case "curve":
                        var keys = ((JArray)v).Select(k => new Keyframe((float)k[0], (float)k[1])).ToArray();
                        x.curve = new AnimationCurve(keys);
                        for (int i = 0; i < keys.Length; i++) AnimationUtility.SetKeyLeftTangentMode(x.curve, i, AnimationUtility.TangentMode.Auto);
                        for (int i = 0; i < keys.Length; i++) AnimationUtility.SetKeyRightTangentMode(x.curve, i, AnimationUtility.TangentMode.Auto);
                        x.useCurve = true;
                        break;
                    default:
                        var fi = typeof(FTMapping).GetField(p.Name, BindingFlags.Public | BindingFlags.Instance);
                        if (fi == null) throw new ArgumentException($"項目 '{p.Name}' はありません");
                        if (fi.FieldType.IsEnum) fi.SetValue(x, Enum.Parse(fi.FieldType, (string)v, true));
                        else fi.SetValue(x, v.ToObject(fi.FieldType));
                        break;
                }
            }
        }

        [McpTool("vrcft_mapping_remove", "マッピングを削除する (Undo 可能)。", Destructive = true, UndoGroup = "VRCFT Mapper: マッピング削除")]
        public static JObject MappingRemove(
            [McpArg("削除するマッピングの番号 (vrcft_status の #)。")] int index,
            [McpArg(MapperArg)] string mapper = null)
        {
            var m = Resolve(mapper);
            if (index < 0 || index >= m.mappings.Count) throw new ArgumentException($"index {index} は範囲外です");
            var x = m.mappings[index];
            Undo.RecordObject(m, "VRCFT Mapper: マッピング削除");
            m.mappings.RemoveAt(index);
            Commit(m);
            return new JObject { ["removed"] = $"#{index} {x.parameter} → {x.blendShape}", ["mappingCount"] = m.mappings.Count };
        }

        [McpTool("vrcft_mirror",
            "Left/Right の組を左右対称にする / 解除する。有効にすると Right 側のマッピングは Left 側の鏡像 (シェイプキー名の Left/Right も入れ替え) で置き換えられ、以後 Left 側の変更が自動で反映される。",
            UndoGroup = "VRCFT Mapper: 左右対称")]
        public static JObject Mirror(
            [McpArg("Left 側のパラメータ名 (例: EyeClosedLeft)。")] string left_parameter,
            [McpArg("true で対称にする、false で解除 (今の Right 側の設定は残る)。")] bool enabled = true,
            [McpArg(MapperArg)] string mapper = null)
        {
            var m = Resolve(mapper);
            if (!MirrorTools.IsLeftParam(left_parameter)) throw new ArgumentException($"{left_parameter} は Left 側のパラメータではありません (視線は対象外)");
            Undo.RecordObject(m, "VRCFT Mapper: 左右対称");
            if (enabled) { if (!m.mirrored.Contains(left_parameter)) m.mirrored.Add(left_parameter); }
            else m.mirrored.Remove(left_parameter);
            Commit(m);
            var right = MirrorTools.Counterpart(left_parameter);
            return new JObject
            {
                ["mirrored"] = new JArray(m.mirrored),
                ["right"] = new JArray(m.mappings.Where(x => !x.useExpression && x.parameter == right).Select(x => x.blendShape)),
            };
        }

        [McpTool("vrcft_repair_refs",
            "コンポーネントのコピーやプレハブ化で外れたメッシュ参照を、シェイプキー名からアバター内のメッシュを探して付け直す (Undo 可能)。",
            UndoGroup = "VRCFT Mapper: 参照の修復")]
        public static JObject RepairRefs([McpArg(MapperArg)] string mapper = null)
        {
            var m = Resolve(mapper);
            var av = Avatar(m);
            if (av == null) throw new ArgumentException("VRC Avatar Descriptor の配下にありません");
            Undo.RecordObject(m, "VRCFT Mapper: 参照の修復");
            var (n, miss) = RefRepair.Repair(m, av.gameObject);
            Commit(m);
            return new JObject { ["fixed"] = n, ["notFound"] = new JArray(miss.Distinct()), ["stillBroken"] = RefRepair.BrokenCount(m) };
        }

        [McpTool("vrcft_build_check",
            "アバターを複製して NDMF ビルドを実行し、VRCFT Mapper が生成したもの (FX レイヤー、同期パラメータ、ネイティブ FT の設定数) と警告・エラーを返す。複製は最後に削除するのでシーンは変わらない。数秒かかる。",
            Idempotency = McpIdempotency.Safe)]
        public static JObject BuildCheck([McpArg(MapperArg)] string mapper = null)
        {
            var m = Resolve(mapper);
            var av = Avatar(m);
            if (av == null) throw new ArgumentException("VRC Avatar Descriptor の配下にありません");
            var logs = new List<string>();
            void OnLog(string msg, string st, LogType t)
            {
                if ((t == LogType.Warning || t == LogType.Error || t == LogType.Exception) && (msg.Contains("VRCFT") || t != LogType.Warning))
                    logs.Add($"{t}: {msg.Split('\n')[0]}");
            }
            var clone = UnityEngine.Object.Instantiate(av.gameObject);
            clone.name = av.name + " (VRCFT build check)";
            Application.logMessageReceived += OnLog;
            try
            {
                nadena.dev.ndmf.AvatarProcessor.ProcessAvatar(clone);
                var d = clone.GetComponent<VRCAvatarDescriptor>();
                var layers = new JArray();
                var prms = new JArray();
                foreach (var l in d.baseAnimationLayers)
                {
                    if (!(l.animatorController is UnityEditor.Animations.AnimatorController c)) continue;
                    foreach (var ly in c.layers.Where(y => y.name.StartsWith("VRCFT"))) layers.Add($"{l.type}: {ly.name}");
                    foreach (var p in c.parameters.Where(p => p.name.StartsWith("VRCFTM/") || p.name.StartsWith(m.prefix ?? "FT/"))) prms.Add($"{p.name} ({p.type})");
                }
                var res = new JObject
                {
                    ["layers"] = layers,
                    ["animatorParameters"] = new JArray(prms.Distinct()),
                    ["logs"] = new JArray(logs.Distinct()),
                };
                if (m.outputMode == FTOutputMode.Native)
                    res["nativeFaceTracking"] = new JObject
                    {
                        ["enabled"] = d.enableFaceTracking,
                        ["meshes"] = new JArray((d.faceTrackingMeshes ?? new List<SkinnedMeshRenderer>()).Where(r => r).Select(r => r.name)),
                        ["blendshapeSets"] = new JArray((d.faceTrackingBlendshapesSetList ?? new List<VRCAvatarDescriptor.FaceTrackingBlendshapesSet>()).Select(s => s.setName)),
                        ["behaviors"] = d.faceTrackingParameterBehaviors?.Count ?? 0,
                    };
                var syncedParams = d.expressionParameters ? d.expressionParameters.parameters.Where(p => p.networkSynced) : Enumerable.Empty<VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter>();
                res["syncedBitsTotal"] = syncedParams.Sum(p => p.valueType == VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType.Bool ? 1 : 8);
                return res;
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
                UnityEngine.Object.DestroyImmediate(clone);
            }
        }
    }
}
