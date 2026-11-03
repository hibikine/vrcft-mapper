using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VRCFTMapper.Editor
{
    /// <summary>
    /// 左右共通のシェイプキーを左右別に分ける。
    /// 境界面は Humanoid の両目ボーンの中点を通り、左目→右目の向きに垂直な面 (無ければアバターのルート基準)。
    /// 中央付近は band の幅でなめらかに混ぜ、すべての頂点で「左 + 右 = 元」になる。
    /// </summary>
    public static class SplitTools
    {
        /// <summary>メッシュ内の命名規則 (_left/_right か _L/_R) に合わせた左右の名前。</summary>
        public static (string left, string right) Names(Mesh mesh, string src)
        {
            bool longForm = mesh != null && Enumerable.Range(0, mesh.blendShapeCount)
                .Any(i => mesh.GetBlendShapeName(i).EndsWith("_left") || mesh.GetBlendShapeName(i).EndsWith("_right"));
            return longForm ? (src + "_left", src + "_right") : (src + "_L", src + "_R");
        }

        /// <summary>split から作られる名前か (UI の欠落チェック用)。</summary>
        public static bool IsSplitName(VRCFTMapper mapper, string shape)
        {
            foreach (var s in mapper.splits)
            {
                if (!s.enabled || s.renderer == null || string.IsNullOrEmpty(s.source)) continue;
                var (l, r) = Names(s.renderer.sharedMesh, s.source);
                if (shape == l || shape == r) return true;
            }
            return false;
        }

        /// <summary>各頂点の「右側らしさ」0..1。renderer の現在の姿勢で計算する。</summary>
        public static float[] RightWeights(SkinnedMeshRenderer r, Transform avatarRoot, float band)
        {
            var anim = avatarRoot != null ? avatarRoot.GetComponent<Animator>() : null;
            Transform le = null, re = null;
            if (anim != null && anim.isHuman)
            {
                le = anim.GetBoneTransform(HumanBodyBones.LeftEye);
                re = anim.GetBoneTransform(HumanBodyBones.RightEye);
            }
            Vector3 center, right; float dist;
            if (le != null && re != null && (re.position - le.position).sqrMagnitude > 1e-8f)
            {
                center = (le.position + re.position) * 0.5f;
                right = re.position - le.position; dist = right.magnitude; right /= dist;
            }
            else
            {
                var head = anim != null && anim.isHuman ? anim.GetBoneTransform(HumanBodyBones.Head) : null;
                center = head != null ? head.position : (avatarRoot != null ? avatarRoot.position : Vector3.zero);
                right = avatarRoot != null ? avatarRoot.right : Vector3.right;
                dist = 0.06f;
            }

            var baked = new Mesh();
            r.BakeMesh(baked, true);
            var v = baked.vertices;
            Object.DestroyImmediate(baked);
            var m = Matrix4x4.TRS(r.transform.position, r.transform.rotation, Vector3.one);
            float b = Mathf.Max(1e-5f, band * dist);
            var w = new float[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                float s = Vector3.Dot(m.MultiplyPoint3x4(v[i]) - center, right);
                w[i] = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(-b, b, s));
            }
            return w;
        }

        /// <summary>mesh (書き換え可能な複製) に左右のシェイプキーを追加。既にあれば何もしない。</summary>
        public static void AddSplit(Mesh mesh, string src, string leftName, string rightName, float[] rightW)
        {
            int si = mesh.GetBlendShapeIndex(src);
            if (si < 0 || rightW == null || rightW.Length != mesh.vertexCount) return;
            int vc = mesh.vertexCount, fc = mesh.GetBlendShapeFrameCount(si);
            foreach (bool isRight in new[] { false, true })
            {
                string name = isRight ? rightName : leftName;
                if (mesh.GetBlendShapeIndex(name) >= 0) continue;
                for (int f = 0; f < fc; f++)
                {
                    var dv = new Vector3[vc]; var dn = new Vector3[vc]; var dt = new Vector3[vc];
                    mesh.GetBlendShapeFrameVertices(si, f, dv, dn, dt);
                    for (int i = 0; i < vc; i++)
                    {
                        float k = isRight ? rightW[i] : 1 - rightW[i];
                        dv[i] *= k; dn[i] *= k; dt[i] *= k;
                    }
                    mesh.AddBlendShapeFrame(name, mesh.GetBlendShapeFrameWeight(si, f), dv, dn, dt);
                }
            }
        }

        /// <summary>mapper の分割設定を適用する。meshFor(renderer) は書き換え可能なメッシュ (複製) を返すこと。</summary>
        public static void Apply(VRCFTMapper mapper, Transform avatarRoot,
            System.Func<SkinnedMeshRenderer, SkinnedMeshRenderer> resolve, System.Func<SkinnedMeshRenderer, Mesh> meshFor)
        {
            var weightCache = new Dictionary<(SkinnedMeshRenderer, float), float[]>();
            foreach (var s in mapper.splits)
            {
                if (!s.enabled || s.renderer == null || string.IsNullOrEmpty(s.source)) continue;
                var r = resolve(s.renderer);
                if (r == null || r.sharedMesh == null || r.sharedMesh.GetBlendShapeIndex(s.source) < 0) continue;
                var (ln, rn) = Names(r.sharedMesh, s.source);
                if (r.sharedMesh.GetBlendShapeIndex(ln) >= 0 && r.sharedMesh.GetBlendShapeIndex(rn) >= 0) continue;
                if (!weightCache.TryGetValue((r, s.band), out var w)) weightCache[(r, s.band)] = w = RightWeights(r, avatarRoot, s.band);
                AddSplit(meshFor(r), s.source, ln, rn, w);
            }
        }
    }
}
