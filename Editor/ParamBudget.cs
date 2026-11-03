using System.Collections.Generic;
using System.Linq;
using nadena.dev.modular_avatar.core;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace VRCFTMapper.Editor
{
    public struct BudgetResult
    {
        public int ours, others, total, limit;
        public int ourParamCount;
        public List<(string name, int bits)> breakdown;
    }

    /// <summary>同期パラメータの容量 (bit) を計算。VRChat 上限 256 bit。</summary>
    public static class ParamBudget
    {
        public const int Limit = 256;

        public static BudgetResult Compute(VRCAvatarDescriptor desc, VRCFTMapper mapper)
        {
            var others = new Dictionary<string, int>();
            if (desc != null && desc.expressionParameters != null && desc.expressionParameters.parameters != null)
                foreach (var p in desc.expressionParameters.parameters)
                {
                    if (p == null || string.IsNullOrEmpty(p.name) || !p.networkSynced) continue;
                    others[p.name] = p.valueType == VRCExpressionParameters.ValueType.Bool ? 1 : 8;
                }

            if (desc != null)
                foreach (var mp in desc.GetComponentsInChildren<ModularAvatarParameters>(true))
                    foreach (var c in mp.parameters)
                    {
                        if (c.isPrefix || c.internalParameter || c.localOnly) continue;
                        int bits = c.syncType switch
                        {
                            ParameterSyncType.Bool => 1,
                            ParameterSyncType.Int => 8,
                            ParameterSyncType.Float => 8,
                            _ => 0,
                        };
                        if (bits == 0) continue;
                        var n = string.IsNullOrEmpty(c.remapTo) ? c.nameOrPrefix : c.remapTo;
                        others[n] = bits;
                    }

            var ours = new Dictionary<string, int>();
            if (mapper != null && mapper.outputMode == FTOutputMode.Native)
            {
                // ネイティブ: 表情データは同期パラメータを使わない。FT / 表情トグルだけ
                if (mapper.nativeToggle) { ours["VRCFTM/NativeFT"] = 1; others.Remove("VRCFTM/NativeFT"); }
                if (mapper.styleGroup.enabled && mapper.mappings.Any(x => x.enabled && x.style > 0)) { ours["VRCFTM/Style"] = 8; others.Remove("VRCFTM/Style"); }
                int o2 = ours.Values.Sum(), x2 = others.Values.Sum();
                return new BudgetResult { ours = o2, others = x2, total = o2 + x2, limit = Limit, ourParamCount = ours.Count,
                    breakdown = ours.Select(kv => (kv.Key, kv.Value)).ToList() };
            }
            if (mapper != null)
                foreach (var g in mapper.mappings.Where(m => m.enabled && m.renderer != null && !VRCFTMapper.IsGaze(m.parameter)).GroupBy(m => m.parameter))
                {
                    int bits = g.Max(FTMath.SyncBits);
                    ours[mapper.FullName(g.Key)] = bits;
                    others.Remove(mapper.FullName(g.Key));
                }
            if (mapper != null)
            {
                foreach (var gp in mapper.GazeParams()) { ours[mapper.FullName(gp)] = 8; others.Remove(mapper.FullName(gp)); }
                // トラッキング有効フラグ (常に宣言)
                foreach (var a in new[] { "EyeTrackingActive", "LipTrackingActive", "ExpressionTrackingActive" })
                { ours[a] = 1; others.Remove(a); }
            }

            int o = ours.Values.Sum(), x = others.Values.Sum();
            return new BudgetResult
            {
                ours = o, others = x, total = o + x, limit = Limit, ourParamCount = ours.Count,
                breakdown = ours.Select(kv => (kv.Key, kv.Value)).OrderByDescending(t => t.Value).ToList(),
            };
        }
    }
}
