using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VRCFTMapper.Editor
{
    /// <summary>
    /// コンポーネントのコピーやプレハブ化でメッシュ参照が外れたとき、
    /// シェイプキー名を持つ SkinnedMeshRenderer をアバター内から探して付け直す。
    /// </summary>
    public static class RefRepair
    {
        public static int BrokenCount(VRCFTMapper m)
            => m.mappings.Count(x => x.renderer == null && !string.IsNullOrEmpty(x.blendShape))
             + m.splits.Count(s => s.renderer == null && !string.IsNullOrEmpty(s.source))
             + (m.eye.lidRenderer == null && LidShape(m) != null ? 1 : 0);

        static string LidShape(VRCFTMapper m)
            => new[] { m.eye.lidDownLeft, m.eye.lidUpLeft, m.eye.lidDownRight, m.eye.lidUpRight }.FirstOrDefault(s => !string.IsNullOrEmpty(s));

        public static (int fixedN, List<string> missing) Repair(VRCFTMapper m, GameObject avatarRoot)
        {
            var rs = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).ToList();
            var splitNames = new HashSet<string>();
            SkinnedMeshRenderer Find(string shape)
            {
                var r = rs.FirstOrDefault(x => x.sharedMesh.GetBlendShapeIndex(shape) >= 0);
                if (r != null) return r;
                // 左右分離で生成されるシェイプキー → 元のシェイプキーで探す
                foreach (var sfx in new[] { "_left", "_right", "_L", "_R" })
                    if (shape.EndsWith(sfx))
                    {
                        r = rs.FirstOrDefault(x => x.sharedMesh.GetBlendShapeIndex(shape.Substring(0, shape.Length - sfx.Length)) >= 0);
                        if (r != null) return r;
                    }
                return null;
            }
            int n = 0; var miss = new List<string>();
            foreach (var x in m.mappings)
                if (x.renderer == null && !string.IsNullOrEmpty(x.blendShape))
                { x.renderer = Find(x.blendShape); if (x.renderer != null) n++; else miss.Add(x.blendShape); }
            foreach (var s in m.splits)
                if (s.renderer == null && !string.IsNullOrEmpty(s.source))
                { s.renderer = Find(s.source); if (s.renderer != null) n++; else miss.Add(s.source); }
            var lid = LidShape(m);
            if (m.eye.lidRenderer == null && lid != null)
            { m.eye.lidRenderer = Find(lid); if (m.eye.lidRenderer != null) n++; else miss.Add(lid); }
            return (n, miss);
        }
    }
}
