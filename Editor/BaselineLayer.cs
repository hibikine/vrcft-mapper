using System.Collections.Generic;
using System.Linq;
using nadena.dev.modular_avatar.core;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace VRCFTMapper.Editor
{
    /// <summary>
    /// ベースラインレイヤー: 生成コントローラーが書くシェイプキーの既定値を、FX の最下層で毎フレーム書く。
    /// これにより、マッピングレイヤーを重み 0 で止めたとき「誰も書かないシェイプキーに最後の値が残る」問題を防ぎつつ、
    /// 最下層なのでアバター側の表情レイヤーは (WD On/Off/混在を問わず) 常に上書きできる。
    /// VRChat 自身が毎フレーム駆動するシェイプ (Viseme / まぶた / ネイティブ FT) は除外する (書くと口パク等が止まるため)。
    /// </summary>
    public partial class VRCFTMapperPlugin
    {
        const int BaselinePriority = -1000000;

        public static void AddBaseline(BuildContext ctx, GameObject parent, IEnumerable<AnimatorController> sources,
            IEnumerable<string> extraExcludedShapes = null)
        {
            var root = ctx.AvatarRootTransform;
            var desc = ctx.AvatarRootObject.GetComponent<VRCAvatarDescriptor>();
            var excluded = ExcludedShapes(desc, root);
            var extra = new HashSet<string>(extraExcludedShapes ?? Enumerable.Empty<string>());

            var bindings = new HashSet<EditorCurveBinding>();
            foreach (var c in sources.Where(c => c != null))
                foreach (var clip in c.animationClips)
                    foreach (var b in AnimationUtility.GetCurveBindings(clip))
                        if (b.type == typeof(SkinnedMeshRenderer) && b.propertyName.StartsWith("blendShape."))
                            bindings.Add(b);

            var clipOut = new AnimationClip { name = "VRCFT Baseline" };
            int count = 0;
            foreach (var b in bindings.OrderBy(b => b.path).ThenBy(b => b.propertyName))
            {
                var shape = b.propertyName.Substring("blendShape.".Length);
                if (excluded.Contains((b.path, shape)) || extra.Contains(shape)) continue;
                var t = string.IsNullOrEmpty(b.path) ? root : root.Find(b.path);
                var r = t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
                if (r == null || r.sharedMesh == null) continue;
                int idx = r.sharedMesh.GetBlendShapeIndex(shape);
                if (idx < 0) continue;
                AnimationUtility.SetEditorCurve(clipOut, b, AnimationCurve.Constant(0, 1f / 60f, r.GetBlendShapeWeight(idx)));
                count++;
            }
            if (count == 0) return;
            Save(ctx, clipOut);

            var ctrl = new AnimatorController { name = "VRCFT Baseline (generated)" };
            Save(ctx, ctrl);
            var sm = new AnimatorStateMachine { name = "VRCFT Baseline", hideFlags = HideFlags.HideInHierarchy };
            Save(ctx, sm);
            var st = sm.AddState("Baseline");
            st.motion = clipOut;
            st.writeDefaultValues = true; // 全対象をクリップで明示的に書くので WD の値に依存しない
            ctrl.AddLayer(new AnimatorControllerLayer { name = "VRCFT Baseline", stateMachine = sm, defaultWeight = 1 });

            var go = new GameObject("VRCFT Baseline (generated)");
            go.transform.SetParent(parent.transform, false);
            var merge = go.AddComponent<ModularAvatarMergeAnimator>();
            merge.animator = ctrl;
            merge.layerType = VRCAvatarDescriptor.AnimLayerType.FX;
            merge.pathMode = MergeAnimatorPathMode.Absolute;
            merge.matchAvatarWriteDefaults = false;
            merge.layerPriority = BaselinePriority; // アバター自身のレイヤーより下 (先頭側) に入れる
        }

        /// <summary>VRChat がアニメーター外から駆動するシェイプ (Viseme, まぶた)。(パス, シェイプ名)</summary>
        static HashSet<(string, string)> ExcludedShapes(VRCAvatarDescriptor desc, Transform root)
        {
            var set = new HashSet<(string, string)>();
            if (desc == null) return set;
            string PathOf(Component c) => c == null ? null : AnimationUtility.CalculateTransformPath(c.transform, root);

            if (desc.lipSync == VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape && desc.VisemeSkinnedMesh != null
                && desc.VisemeBlendShapes != null)
            {
                var p = PathOf(desc.VisemeSkinnedMesh);
                foreach (var s in desc.VisemeBlendShapes) if (!string.IsNullOrEmpty(s)) set.Add((p, s));
            }
            if (desc.lipSync == VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.JawFlapBlendShape && desc.VisemeSkinnedMesh != null
                && !string.IsNullOrEmpty(desc.MouthOpenBlendShapeName))
                set.Add((PathOf(desc.VisemeSkinnedMesh), desc.MouthOpenBlendShapeName));

            var eye = desc.customEyeLookSettings;
            if (desc.enableEyeLook && eye.eyelidType == VRCAvatarDescriptor.EyelidType.Blendshapes
                && eye.eyelidsSkinnedMesh != null && eye.eyelidsSkinnedMesh.sharedMesh != null && eye.eyelidsBlendshapes != null)
            {
                var p = PathOf(eye.eyelidsSkinnedMesh);
                var mesh = eye.eyelidsSkinnedMesh.sharedMesh;
                foreach (var i in eye.eyelidsBlendshapes)
                    if (i >= 0 && i < mesh.blendShapeCount) set.Add((p, mesh.GetBlendShapeName(i)));
            }

            // ネイティブ FT が駆動するシェイプ
            if (desc.enableFaceTracking && desc.faceTrackingMeshes != null)
            {
                var names = new HashSet<string>();
                if (desc.faceTrackingBlendshapesSetList != null)
                    foreach (var s in desc.faceTrackingBlendshapesSetList)
                    {
                        if (s.faceTrackingBlendshapeNames != null) foreach (var n in s.faceTrackingBlendshapeNames) if (!string.IsNullOrEmpty(n)) names.Add(n);
                        if (s.visemeBlendshapeNames != null) foreach (var n in s.visemeBlendshapeNames) if (!string.IsNullOrEmpty(n)) names.Add(n);
                    }
                if (desc.faceTrackingParameterBehaviors != null)
                    foreach (var b in desc.faceTrackingParameterBehaviors) if (!string.IsNullOrEmpty(b.targetBlendshapeName)) names.Add(b.targetBlendshapeName);
                foreach (var r in desc.faceTrackingMeshes.Where(r => r != null))
                {
                    var p = PathOf(r);
                    foreach (var n in names) set.Add((p, n));
                }
            }
            return set;
        }
    }
}
