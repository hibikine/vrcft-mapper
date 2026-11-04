using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Desc = VRC.SDK3.Avatars.Components.VRCAvatarDescriptor;

namespace VRCFTMapper.Editor
{
    /// <summary>アバターデスクリプタの Face Tracking 設定の判定・削除・VRCFT Mapper への読み込み。</summary>
    public static class DescriptorFT
    {
        /// <summary>Face Tracking に何らかの設定があり、初期状態ではないか。</summary>
        public static bool HasSettings(Desc d)
        {
            if (d == null) return false;
            if (d.faceTrackingMeshes != null && d.faceTrackingMeshes.Any(r => r != null)) return true;
            if (d.faceTrackingParameterBehaviors != null && d.faceTrackingParameterBehaviors.Count > 0) return true;
            if (d.faceTrackingBlendshapesSetList != null && d.faceTrackingBlendshapesSetList.Any(s =>
                    (s.faceTrackingBlendshapeNames != null && s.faceTrackingBlendshapeNames.Any(n => !string.IsNullOrEmpty(n))) ||
                    (s.visemeBlendshapeNames != null && s.visemeBlendshapeNames.Any(n => !string.IsNullOrEmpty(n)))))
                return true;
            return false;
        }

        public static string Summary(Desc d)
        {
            int meshes = d.faceTrackingMeshes?.Count(r => r != null) ?? 0;
            int sets = d.faceTrackingBlendshapesSetList?.Count ?? 0;
            int shapes = d.faceTrackingBlendshapesSetList?.Sum(s => s.faceTrackingBlendshapeNames?.Count(n => !string.IsNullOrEmpty(n)) ?? 0) ?? 0;
            int behaviors = d.faceTrackingParameterBehaviors?.Count ?? 0;
            return $"Meshes {meshes} / Blendshapes Set {sets} (割当 {shapes}) / Parameter Behaviors {behaviors}";
        }

        /// <summary>Face Tracking 以下の項目をすべて初期状態に戻す (Enable の状態に関わらず)。</summary>
        public static void Clear(Desc d, bool undo = true)
        {
            if (undo) Undo.RecordObject(d, "Face Tracking 設定を削除");
            d.enableFaceTracking = false;
            d.useAllTrackingKeys = false;
            d.faceTrackingMeshes = new List<SkinnedMeshRenderer>();
            d.faceTrackingBlendshapesSetList = new List<Desc.FaceTrackingBlendshapesSet>();
            d.faceTrackingParameterBehaviors = new List<Desc.FaceTrackingParameterBehavior>();
            if (undo)
            {
                EditorUtility.SetDirty(d);
                PrefabUtility.RecordPrefabInstancePropertyModifications(d);
            }
        }

        public class ImportResult { public int mappings, limits, skipped, duplicates; public List<string> notes = new List<string>(); }

        /// <summary>デスクリプタの設定をマッピング / 上限・下限に変換して読み込む。overwrite = 既存のマッピングを置き換える。Undo 可能。</summary>
        public static ImportResult Import(VRCFTMapper mapper, Desc d, bool overwrite)
        {
            var res = new ImportResult();
            Undo.RecordObject(mapper, overwrite ? "Face Tracking 設定を上書き読み込み" : "Face Tracking 設定を追加読み込み");
            if (overwrite) { mapper.mappings.Clear(); mapper.limits.Clear(); }

            var meshes = (d.faceTrackingMeshes ?? new List<SkinnedMeshRenderer>()).Where(r => r != null && r.sharedMesh != null).ToList();
            SkinnedMeshRenderer Find(string shape) =>
                meshes.FirstOrDefault(r => r.sharedMesh.GetBlendShapeIndex(shape) >= 0) ?? meshes.FirstOrDefault();

            void Add(FTMapping m)
            {
                bool dup = mapper.mappings.Any(x => x.renderer == m.renderer && x.blendShape == m.blendShape &&
                    x.useExpression == m.useExpression && (m.useExpression ? x.expression == m.expression : x.parameter == m.parameter));
                if (dup) { res.duplicates++; return; }
                mapper.mappings.Add(m); res.mappings++;
            }

            int visemes = 0;
            foreach (var set in d.faceTrackingBlendshapesSetList ?? new List<Desc.FaceTrackingBlendshapesSet>())
            {
                var names = set.faceTrackingBlendshapeNames ?? new string[0];
                for (int i = 0; i < names.Length && i < NativeKeys.Count; i++)
                {
                    if (string.IsNullOrEmpty(names[i])) continue;
                    float mul = set.faceTrackingBlendshapeMultipliers != null && i < set.faceTrackingBlendshapeMultipliers.Length ? set.faceTrackingBlendshapeMultipliers[i] : 1f;
                    Add(new FTMapping { parameter = NativeKeys.All[i], renderer = Find(names[i]), blendShape = names[i], outMax = mul });
                }
                visemes += set.visemeBlendshapeNames?.Count(n => !string.IsNullOrEmpty(n)) ?? 0;
            }
            if (visemes > 0) { res.skipped += visemes; res.notes.Add($"Viseme の割当 {visemes} 件は対応する設定がないため読み込みません"); }

            int other = 0;
            foreach (var b in d.faceTrackingParameterBehaviors ?? new List<Desc.FaceTrackingParameterBehavior>())
            {
                string expr = ExprOf(b);
                switch (b.targetMode)
                {
                    case Desc.ParameterBehaviorTargetMode.ApplyToBlendshape when !string.IsNullOrEmpty(b.targetBlendshapeName) && !string.IsNullOrEmpty(expr):
                        if (NativeKeys.IsKey(expr)) Add(new FTMapping { parameter = expr, renderer = Find(b.targetBlendshapeName), blendShape = b.targetBlendshapeName });
                        else Add(new FTMapping { parameter = expr, useExpression = true, expression = expr, renderer = Find(b.targetBlendshapeName), blendShape = b.targetBlendshapeName });
                        break;
                    case Desc.ParameterBehaviorTargetMode.LimitBlendshapeMax when !string.IsNullOrEmpty(b.targetBlendshapeName) && !string.IsNullOrEmpty(expr):
                    case Desc.ParameterBehaviorTargetMode.LimitBlendshapeMin when !string.IsNullOrEmpty(b.targetBlendshapeName) && !string.IsNullOrEmpty(expr):
                        bool isMin = b.targetMode == Desc.ParameterBehaviorTargetMode.LimitBlendshapeMin;
                        if (mapper.limits.Any(l => l.blendShape == b.targetBlendshapeName && l.isMin == isMin && l.expression == expr)) { res.duplicates++; break; }
                        mapper.limits.Add(new FTLimit { blendShape = b.targetBlendshapeName, isMin = isMin, expression = expr });
                        res.limits++;
                        break;
                    default:
                        other++;
                        break;
                }
            }
            if (other > 0) { res.skipped += other; res.notes.Add($"Forward to Animator / Apply Blend Set Weight などの Parameter Behavior {other} 件は読み込みません"); }

            EditorUtility.SetDirty(mapper);
            PrefabUtility.RecordPrefabInstancePropertyModifications(mapper);
            return res;
        }

        /// <summary>Parameter Behavior の入力を Tracking Key の式にする。</summary>
        static string ExprOf(Desc.FaceTrackingParameterBehavior b)
        {
            if (b.combineMode == Desc.ParameterBehaviorCombineMode.Custom) return (b.customExpression ?? "").Trim();
            var terms = new List<string>();
            int n = System.Math.Max(b.inputTrackingKeyNames?.Length ?? 0, b.inputTrackingKeys?.Length ?? 0);
            for (int i = 0; i < n; i++)
            {
                string k = b.inputTrackingKeyNames != null && i < b.inputTrackingKeyNames.Length ? b.inputTrackingKeyNames[i] : null;
                if (string.IsNullOrEmpty(k) && b.inputTrackingKeys != null && i < b.inputTrackingKeys.Length && b.inputTrackingKeys[i] >= 0 && b.inputTrackingKeys[i] < NativeKeys.Count)
                    k = NativeKeys.All[b.inputTrackingKeys[i]];
                if (string.IsNullOrEmpty(k)) continue;
                bool inv = b.inputInverted != null && i < b.inputInverted.Length && b.inputInverted[i];
                terms.Add(inv ? $"(1 - {k})" : k);
            }
            if (terms.Count == 0) return null;
            switch (b.combineMode)
            {
                case Desc.ParameterBehaviorCombineMode.Max: return terms.Aggregate((a, x) => $"max({a}, {x})");
                case Desc.ParameterBehaviorCombineMode.Min: return terms.Aggregate((a, x) => $"min({a}, {x})");
                case Desc.ParameterBehaviorCombineMode.Multiply: return string.Join(" * ", terms);
                default: return string.Join(" + ", terms);
            }
        }
    }
}
