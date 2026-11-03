using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace VRCFTMapper.Editor
{
    /// <summary>Left 側のマッピングを Right 側へ鏡像コピーする (左右対称設定)。</summary>
    public static class MirrorTools
    {
        /// <summary>Left/Right を含むパラメータの相方。なければ null。視線は対象外 (X の符号が逆になるため)。</summary>
        public static string Counterpart(string param)
        {
            if (string.IsNullOrEmpty(param) || VRCFTMapper.IsGaze(param)) return null;
            int l = param.LastIndexOf("Left"), r = param.LastIndexOf("Right");
            if (l >= 0 && l >= r) return param.Substring(0, l) + "Right" + param.Substring(l + 4);
            if (r >= 0) return param.Substring(0, r) + "Left" + param.Substring(r + 5);
            return null;
        }

        public static bool IsLeftParam(string param)
        {
            var c = Counterpart(param);
            return c != null && param.LastIndexOf("Left") > param.LastIndexOf("Right");
        }

        static readonly (string a, string b)[] Pairs =
        {
            ("_left", "_right"), ("_Left", "_Right"), ("Left", "Right"), ("left", "right"),
            (".L", ".R"), ("_L", "_R"), ("_l", "_r"), ("L_", "R_"), ("左", "右"),
        };

        /// <summary>シェイプキー名の左右を入れ替えた候補のうち、実在するもの。なければ元の名前 (左右共通のシェイプ)。</summary>
        public static string MirrorShape(VRCFTMapper mapper, SkinnedMeshRenderer r, string shape)
        {
            if (string.IsNullOrEmpty(shape)) return shape;
            var mesh = r != null ? r.sharedMesh : null;
            bool Exists(string n) => (mesh != null && mesh.GetBlendShapeIndex(n) >= 0) || SplitTools.IsSplitName(mapper, n);
            foreach (var (a, b) in Pairs)
                foreach (var (from, to) in new[] { (a, b), (b, a) })
                {
                    int i = shape.LastIndexOf(from, System.StringComparison.Ordinal);
                    if (i < 0) continue;
                    var cand = shape.Substring(0, i) + to + shape.Substring(i + from.Length);
                    if (Exists(cand)) return cand;
                }
            return shape;
        }

        /// <summary>mirrored に登録された全ペアを同期。変更があれば true。</summary>
        public static bool SyncAll(VRCFTMapper mapper)
        {
            bool changed = false;
            foreach (var lp in mapper.mirrored.ToList()) changed |= Sync(mapper, lp);
            return changed;
        }

        public static bool Sync(VRCFTMapper mapper, string leftParam)
        {
            var rp = Counterpart(leftParam);
            if (rp == null) return false;
            var src = mapper.mappings.Where(m => !m.useExpression && m.parameter == leftParam).ToList();
            var dstIdx = Enumerable.Range(0, mapper.mappings.Count)
                .Where(i => !mapper.mappings[i].useExpression && mapper.mappings[i].parameter == rp).ToList();

            var made = src.Select(s =>
            {
                var c = s.Clone();
                c.parameter = rp;
                c.blendShape = MirrorShape(mapper, s.renderer, s.blendShape);
                c.eyeSide = s.eyeSide == EyeSide.Left ? EyeSide.Right : s.eyeSide == EyeSide.Right ? EyeSide.Left : s.eyeSide;
                return c;
            }).ToList();

            if (dstIdx.Count == made.Count)
            {
                bool diff = false;
                for (int k = 0; k < made.Count; k++)
                    if (!Same(mapper.mappings[dstIdx[k]], made[k])) { mapper.mappings[dstIdx[k]] = made[k]; diff = true; }
                return diff;
            }

            // 件数が違う → 作り直し (Right の最初の位置、なければ Left の直後に置く)
            int at = dstIdx.Count > 0 ? dstIdx[0] : mapper.mappings.LastIndexOf(src.LastOrDefault()) + 1;
            for (int k = dstIdx.Count - 1; k >= 0; k--) { mapper.mappings.RemoveAt(dstIdx[k]); if (dstIdx[k] < at) at--; }
            at = Mathf.Clamp(at, 0, mapper.mappings.Count);
            mapper.mappings.InsertRange(at, made);
            return true;
        }

        static bool Same(FTMapping a, FTMapping b)
            => UnityEditor.EditorJsonUtility.ToJson(new Box { m = a }) == UnityEditor.EditorJsonUtility.ToJson(new Box { m = b });

        [System.Serializable] class Box { public FTMapping m; }
    }
}
