using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace VRCFTMapper.Editor
{
    public static class AutoSetup
    {
        /// <summary>VRChat でよく使われる、容量に収まりやすい標準セット。</summary>
        public static readonly string[] StandardSet =
        {
            "EyeLidLeft", "EyeLidRight", "EyeSquint",
            "BrowExpressionLeft", "BrowExpressionRight",
            "JawOpen", "JawX", "MouthClosed",
            "MouthX", "MouthUpperUp", "MouthLowerDown",
            "SmileFrownLeft", "SmileFrownRight",
            "LipFunnel", "LipPucker", "LipSuckUpper", "LipSuckLower",
            "CheekPuffSuck", "NoseSneer", "TongueOut",
        };

        // 精度が欲しいもの (まぶた・顎) は float、それ以外は 4bit バイナリ
        // 視線は Binary だとカクつくので常に float
        static readonly HashSet<string> FloatDefault = new HashSet<string> { "EyeLidLeft", "EyeLidRight", "EyeLid", "JawOpen" };

        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int dot = s.LastIndexOf('.');
            // "blendShape1.jawOpen" / "Face.JawOpen" → 後半 (ただし vrc.v_aa は残す)
            if (dot >= 0 && !s.StartsWith("vrc.")) s = s.Substring(dot + 1);
            var sb = new StringBuilder();
            foreach (var c in s)
            {
                if (c == '_' || c == '-' || c == ' ' || c == '.' || c == '　') continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            var r = sb.ToString();
            // 左右表記揺れ: _L / _R / .L / Left
            if (r.EndsWith("left")) r = r.Substring(0, r.Length - 4) + "l";
            else if (r.EndsWith("right")) r = r.Substring(0, r.Length - 5) + "r";
            return r;
        }

        struct ShapeRef { public SkinnedMeshRenderer smr; public string name; }

        public static List<SkinnedMeshRenderer> FaceRenderers(GameObject avatar)
        {
            var list = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => r.sharedMesh != null && r.sharedMesh.blendShapeCount > 0).ToList();
            // 顔っぽいメッシュを優先 (Body / Face / 顔 / シェイプキーの多いもの)
            return list.OrderByDescending(r =>
            {
                var n = r.name.ToLowerInvariant();
                int s = r.sharedMesh.blendShapeCount;
                if (n == "body" || n.Contains("face") || n.Contains("head") || n.Contains("顔")) s += 10000;
                return s;
            }).ToList();
        }

        public static int Run(VRCFTMapper mapper, GameObject avatar, bool replace, bool standardOnly)
        {
            var smrs = FaceRenderers(avatar);
            var index = new Dictionary<string, ShapeRef>();
            foreach (var smr in smrs)
                for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++)
                {
                    var n = smr.sharedMesh.GetBlendShapeName(i);
                    var key = Normalize(n);
                    if (!index.ContainsKey(key)) index[key] = new ShapeRef { smr = smr, name = n };
                }

            Undo.RecordObject(mapper, "VRCFT 自動設定");
            if (replace) mapper.mappings.Clear();

            var used = new HashSet<string>(mapper.mappings.Select(m => m.blendShape));
            var existingParams = new HashSet<string>(mapper.mappings.Select(m => m.parameter));
            var added = new List<FTMapping>();

            IEnumerable<FTParamSpec> specs = standardOnly
                ? StandardSet.Select(FTCatalog.Find).Where(s => s != null)
                : FTCatalog.All;
            // 視線: シェイプキー型のときだけ対象 (標準セットは両目共通の EyeX/EyeY)
            specs = specs.Where(s => s.group != "Gaze");
            if (mapper.gazeMode == GazeMode.BlendShape)
            {
                var gaze = mapper.gazeCombined || standardOnly
                    ? new[] { "EyeX", "EyeY" }
                    : new[] { "EyeLeftX", "EyeRightX", "EyeY" };
                specs = gaze.Select(FTCatalog.Find).Concat(specs);
            }

            foreach (var spec in specs)
            {
                if (existingParams.Contains(spec.name)) continue;
                // L/R 両方がある場合、両側まとめのパラメータは使わない
                if (!standardOnly && existingParams.Contains(spec.name + "Left") && existingParams.Contains(spec.name + "Right")) continue;

                var found = new List<FTMapping>();
                foreach (var t in spec.targets)
                {
                    foreach (var c in t.candidates)
                    {
                        if (!index.TryGetValue(Normalize(c), out var sr)) continue;
                        if (used.Contains(sr.name)) continue;
                        found.Add(new FTMapping
                        {
                            parameter = spec.name,
                            signed = spec.signed,
                            type = FloatDefault.Contains(spec.name) || spec.group == "Gaze" ? FTParamType.Float : FTParamType.Binary,
                            binaryBits = 4,
                            renderer = sr.smr,
                            blendShape = sr.name,
                            inMin = t.inMin, inMax = t.inMax, outMin = t.outMin, outMax = t.outMax,
                        });
                        used.Add(sr.name);
                        break;
                    }
                }
                if (found.Count == 0) continue;
                existingParams.Add(spec.name);
                added.AddRange(found);
            }

            mapper.mappings.AddRange(added);
            EditorUtility.SetDirty(mapper);
            PrefabUtility.RecordPrefabInstancePropertyModifications(mapper);
            return added.Count;
        }

        /// <summary>手動修正用: シェイプキー名から類似パラメータを推定。</summary>
        public static string GuessParameter(string shape)
        {
            var n = Normalize(shape);
            foreach (var p in FTCatalog.All)
                foreach (var t in p.targets)
                    if (t.candidates.Any(c => Normalize(c) == n)) return p.name;
            return null;
        }
    }
}
