using System.Collections.Generic;
using System.Linq;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace VRCFTMapper.Editor
{
    /// <summary>ハンドジェスチャーで表情を変えているレイヤーの検出と、無効化処理の注入。</summary>
    public static class GestureTools
    {
        public const string DisabledParam = "FacialExpressionsDisabled";
        public static readonly HashSet<string> GestureParams = new HashSet<string>
        { "GestureLeft", "GestureRight", "GestureLeftWeight", "GestureRightWeight" };

        public class LayerInfo
        {
            public string name;
            public string source;      // どのコントローラ由来か (UI 表示用)
            public bool usesGesture;
            public bool touchesFace;
            public bool Detected => usesGesture && touchesFace;
        }

        // ---------------- 検出 ----------------

        public static bool UsesGesture(AnimatorStateMachine sm)
        {
            foreach (var t in AllTransitions(sm))
                if (t.conditions.Any(c => GestureParams.Contains(c.parameter))) return true;
            foreach (var m in AllMotions(sm))
                if (m is BlendTree bt && TreeUsesGesture(bt)) return true;
            return false;
        }

        static bool TreeUsesGesture(BlendTree bt)
        {
            if (GestureParams.Contains(bt.blendParameter) || GestureParams.Contains(bt.blendParameterY)) return true;
            foreach (var c in bt.children)
            {
                if (GestureParams.Contains(c.directBlendParameter)) return true;
                if (c.motion is BlendTree sub && TreeUsesGesture(sub)) return true;
            }
            return false;
        }

        /// <summary>facePaths: マッピング対象メッシュのパス (null なら任意のシェイプキーで true)。</summary>
        public static bool TouchesFace(AnimatorStateMachine sm, HashSet<string> facePaths)
        {
            foreach (var clip in AllMotions(sm).SelectMany(Clips))
                foreach (var b in AnimationUtility.GetCurveBindings(clip))
                    if (b.type == typeof(SkinnedMeshRenderer) && b.propertyName.StartsWith("blendShape.") &&
                        (facePaths == null || facePaths.Contains(b.path)))
                        return true;
            return false;
        }

        public static IEnumerable<AnimationClip> Clips(Motion m)
        {
            if (m is AnimationClip c) { yield return c; yield break; }
            if (m is BlendTree bt)
                foreach (var ch in bt.children)
                    foreach (var x in Clips(ch.motion)) yield return x;
        }

        public static IEnumerable<AnimatorStateMachine> AllMachines(AnimatorStateMachine sm)
        {
            if (sm == null) yield break;
            yield return sm;
            foreach (var c in sm.stateMachines)
                foreach (var x in AllMachines(c.stateMachine)) yield return x;
        }

        public static IEnumerable<AnimatorTransitionBase> AllTransitions(AnimatorStateMachine root)
        {
            foreach (var sm in AllMachines(root))
            {
                foreach (var t in sm.anyStateTransitions) yield return t;
                foreach (var t in sm.entryTransitions) yield return t;
                foreach (var s in sm.states)
                    foreach (var t in s.state.transitions) yield return t;
                foreach (var c in sm.stateMachines)
                    foreach (var t in sm.GetStateMachineTransitions(c.stateMachine)) yield return t;
            }
        }

        static IEnumerable<Motion> AllMotions(AnimatorStateMachine root)
        {
            foreach (var sm in AllMachines(root))
                foreach (var s in sm.states)
                    if (s.state.motion != null) yield return s.state.motion;
        }

        public static bool IsTarget(VRCFTMapper mapper, AnimatorControllerLayer layer, HashSet<string> facePaths)
        {
            if (layer.name.StartsWith("VRCFT ")) return false;
            if (mapper.gestureExcluded.Contains(layer.name)) return false;
            if (mapper.gestureForced.Contains(layer.name)) return true;
            return UsesGesture(layer.stateMachine) && TouchesFace(layer.stateMachine, facePaths);
        }

        public static HashSet<string> FacePaths(VRCFTMapper mapper, Transform root)
            => new HashSet<string>(mapper.mappings.Where(m => m.renderer != null)
                .Select(m => AnimationUtility.CalculateTransformPath(m.renderer.transform, root)));

        /// <summary>エディタ表示用: ビルド前の FX + MA Merge Animator (FX) のレイヤーを列挙。</summary>
        public static List<LayerInfo> Scan(VRCAvatarDescriptor desc, VRCFTMapper mapper)
        {
            var list = new List<LayerInfo>();
            if (desc == null || mapper == null) return list;
            var face = FacePaths(mapper, desc.transform);

            void Add(AnimatorController ac, string source, HashSet<string> paths)
            {
                if (ac == null) return;
                foreach (var l in ac.layers)
                    list.Add(new LayerInfo
                    {
                        name = l.name, source = source,
                        usesGesture = UsesGesture(l.stateMachine),
                        touchesFace = TouchesFace(l.stateMachine, paths),
                    });
            }

            if (desc.customizeAnimationLayers)
                foreach (var l in desc.baseAnimationLayers)
                    if (l.type == VRCAvatarDescriptor.AnimLayerType.FX)
                        Add(l.animatorController as AnimatorController, "FX", face);

            foreach (var ma in desc.GetComponentsInChildren<ModularAvatarMergeAnimator>(true))
            {
                if (ma.layerType != VRCAvatarDescriptor.AnimLayerType.FX) continue;
                // 相対パスモードのものはパス判定が難しいので「何らかのシェイプキー」で判定
                Add(ma.animator as AnimatorController, ma.name, ma.pathMode == MergeAnimatorPathMode.Absolute ? face : null);
            }
            return list;
        }

        // ---------------- 注入 (MA マージ後の最終 FX) ----------------

        public static void Inject(VRCFTMapper mapper, AnimatorController fx, Transform root)
        {
            if (fx == null || mapper.gestureMode == GestureDisableMode.None) return;
            var face = FacePaths(mapper, root);
            var layers = fx.layers;
            var targets = new List<int>();
            for (int i = 0; i < layers.Length; i++)
                if (IsTarget(mapper, layers[i], face)) targets.Add(i);
            if (targets.Count == 0) return;

            if (!fx.parameters.Any(p => p.name == DisabledParam))
                fx.AddParameter(DisabledParam, AnimatorControllerParameterType.Bool);

            if (mapper.gestureMode == GestureDisableMode.LayerWeight)
            {
                int ctl = System.Array.FindIndex(layers, l => l.name == BuildNames.GestureControlLayer);
                if (ctl < 0) return;
                foreach (var cs in layers[ctl].stateMachine.states)
                {
                    bool tracking = cs.state.name == "Tracking";
                    foreach (var idx in targets)
                    {
                        var lc = cs.state.AddStateMachineBehaviour<VRC.SDK3.Avatars.Components.VRCAnimatorLayerControl>();
                        lc.playable = VRC.SDKBase.VRC_AnimatorLayerControl.BlendableLayer.FX;
                        lc.layer = idx;
                        lc.goalWeight = tracking ? 0 : layers[idx].defaultWeight;
                        lc.blendDuration = mapper.gestureBlendDuration;
                    }
                }
            }
            else
            {
                foreach (var idx in targets)
                {
                    var sm = layers[idx].stateMachine;
                    foreach (var t in AllTransitions(sm))
                    {
                        if (!t.conditions.Any(c => GestureParams.Contains(c.parameter))) continue;
                        t.AddCondition(AnimatorConditionMode.IfNot, 0, DisabledParam);
                    }
                    // トラッキング開始時に既定ステートへ戻す
                    if (sm.defaultState != null)
                    {
                        var back = sm.AddAnyStateTransition(sm.defaultState);
                        back.AddCondition(AnimatorConditionMode.If, 0, DisabledParam);
                        back.canTransitionToSelf = false;
                        back.hasExitTime = false;
                        back.duration = mapper.gestureBlendDuration;
                    }
                }
            }
        }
    }

    public static class BuildNames
    {
        public const string GestureControlLayer = "VRCFT Gesture Control";
    }
}
