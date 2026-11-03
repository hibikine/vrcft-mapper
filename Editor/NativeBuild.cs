using System.Collections.Generic;
using System.Linq;
using nadena.dev.modular_avatar.core;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;

namespace VRCFTMapper.Editor
{
    /// <summary>
    /// ネイティブ Face Tracking 向けのビルド。ビルド用の複製のアバターデスクリプタに書き込む (非破壊)。
    ///  - Face Tracking Meshes / Blendshapes Set / Parameter Behavior
    ///  - out の範囲外用に、変形量を拡大・反転したシェイプキーをメッシュの複製に追加
    ///  - アニメーター: Eye / Face の有効判定 (UserEyeTracking / UserFaceTracking + FT/表情トグル) で Set の重みを切替、
    ///    ジェスチャー表情の停止、目ボーンの加工 (視線だけ Forward to Animator)
    /// </summary>
    public partial class VRCFTMapperPlugin
    {
        public const string NativeFT = "VRCFTM/NativeFT";
        public const string EyeOn = "VRCFTM/EyeOn";
        public const string FaceOn = "VRCFTM/FaceOn";
        const string UserEyeTracking = "UserEyeTracking";
        const string UserFaceTracking = "UserFaceTracking";
        const int VisemeCount = 15;

        static void GenerateNative(BuildContext ctx, VRCFTMapper mapper)
        {
            var plan = NativePlan.Build(mapper);
            foreach (var w in plan.warnings) Debug.LogWarning("[VRCFT Mapper] " + w);
            var desc = ctx.AvatarRootObject.GetComponent<VRCAvatarDescriptor>();

            // ---- 1) 範囲外用のシェイプキーを生成 ----
            var copies = new Dictionary<SkinnedMeshRenderer, Mesh>();
            foreach (var gs in plan.generated)
            {
                if (gs.renderer == null || gs.renderer.sharedMesh == null) continue;
                if (!copies.TryGetValue(gs.renderer, out var mesh))
                {
                    mesh = Object.Instantiate(gs.renderer.sharedMesh);
                    mesh.name = gs.renderer.sharedMesh.name + " (VRCFT Mapper)";
                    Save(ctx, mesh);
                    gs.renderer.sharedMesh = mesh;
                    copies[gs.renderer] = mesh;
                }
                AddScaledShape(mesh, gs.source, gs.name, gs.scale);
            }

            // ---- 2) アバターデスクリプタ ----
            desc.enableFaceTracking = true;
            desc.useAllTrackingKeys = true;
            if (desc.faceTrackingMeshes == null) desc.faceTrackingMeshes = new List<SkinnedMeshRenderer>();
            foreach (var r in plan.meshes)
            {
                if (r == null || desc.faceTrackingMeshes.Contains(r)) continue;
                if (desc.faceTrackingMeshes.Count >= NativePlan.MaxMeshes) { Debug.LogWarning($"[VRCFT Mapper] Face Tracking Meshes が上限 ({NativePlan.MaxMeshes}) のため {r.name} を追加できません"); continue; }
                desc.faceTrackingMeshes.Add(r);
            }

            if (desc.faceTrackingBlendshapesSetList == null) desc.faceTrackingBlendshapesSetList = new List<VRCAvatarDescriptor.FaceTrackingBlendshapesSet>();
            desc.faceTrackingBlendshapesSetList.RemoveAll(s => s.setName != null && s.setName.StartsWith(NativePlan.SetAlways));
            var created = new HashSet<string>();
            foreach (var setName in new[] { NativePlan.SetAlways, NativePlan.SetEye, NativePlan.SetFace })
            {
                var entries = plan.setEntries.Where(x => x.set == setName).ToList();
                bool usedByBehavior = setName != NativePlan.SetAlways && plan.behaviors.Any(b => b.weightSet == setName);
                if (entries.Count == 0 && !usedByBehavior) continue;
                int n = NativeKeys.Count;
                var set = new VRCAvatarDescriptor.FaceTrackingBlendshapesSet
                {
                    setName = setName,
                    // Eye / Face はアニメーターの切替レイヤーが有効時に 1 にする
                    setWeight = setName == NativePlan.SetAlways ? 1f : 0f,
                    faceTrackingBlendshapeNames = Enumerable.Repeat("", n).ToArray(),
                    faceTrackingBlendshapeMultipliers = Enumerable.Repeat(1f, n).ToArray(),
                    visemeBlendshapeNames = Enumerable.Repeat("", VisemeCount).ToArray(),
                    visemeBlendshapeMultipliers = Enumerable.Repeat(1f, VisemeCount).ToArray(),
                };
                foreach (var e in entries)
                {
                    int i = NativeKeys.IndexOf(e.key);
                    if (i < 0) continue;
                    set.faceTrackingBlendshapeNames[i] = e.shape;
                    set.faceTrackingBlendshapeMultipliers[i] = e.multiplier;
                }
                desc.faceTrackingBlendshapesSetList.Add(set);
                created.Add(setName);
            }

            if (desc.faceTrackingParameterBehaviors == null) desc.faceTrackingParameterBehaviors = new List<VRCAvatarDescriptor.FaceTrackingParameterBehavior>();
            foreach (var b in plan.behaviors)
            {
                if (!b.valid) continue;
                desc.faceTrackingParameterBehaviors.Add(new VRCAvatarDescriptor.FaceTrackingParameterBehavior
                {
                    targetMode = b.forwardParam != null
                        ? VRCAvatarDescriptor.ParameterBehaviorTargetMode.ForwardToAnimator
                        : VRCAvatarDescriptor.ParameterBehaviorTargetMode.ApplyToBlendshape,
                    outputParameterName = b.forwardParam ?? "",
                    targetBlendshapeName = b.shape ?? "",
                    targetBlendSetName = "",
                    weightBlendSetName = b.weightSet != null && created.Contains(b.weightSet) && b.weightSet != NativePlan.SetAlways ? b.weightSet : "",
                    inputTrackingKeys = new int[0],
                    inputTrackingKeyNames = new string[0],
                    inputInverted = new bool[0],
                    combineMode = VRCAvatarDescriptor.ParameterBehaviorCombineMode.Custom,
                    customExpression = b.expr,
                });
            }

            // ---- 3) アニメーター ----
            var root = ctx.AvatarRootTransform;
            var ctrl = new AnimatorController { name = "VRCFT Mapper Native (generated)" };
            Save(ctx, ctrl);
            var g = new Gen { ctx = ctx, ctrl = ctrl };
            g.Decl(One, 1);

            string[] eyeConds = mapper.nativeToggle ? new[] { UserEyeTracking, NativeFT } : new[] { UserEyeTracking };
            string[] faceConds = mapper.nativeToggle ? new[] { UserFaceTracking, NativeFT } : new[] { UserFaceTracking };
            bool eyeSet = created.Contains(NativePlan.SetEye), faceSet = created.Contains(NativePlan.SetFace);
            bool boneGaze = mapper.gazeMode == GazeMode.Bone;

            BoolGateLayer(g, BuildNames.EyeControlLayer, eyeConds, EyeOn, (st, on) =>
            {
                if (eyeSet) SetBlendWeight(st, NativePlan.SetEye, on ? 1 : 0, mapper.nativeLerpSpeed);
                if (boneGaze && mapper.trackingControl)
                {
                    // 目ボーンはこちらの加工 (Additive) で動かすので、VRChat 側の視線制御を止める
                    var tc = st.AddStateMachineBehaviour<VRCAnimatorTrackingControl>();
                    tc.trackingEyes = on ? VRC_AnimatorTrackingControl.TrackingType.Animation : VRC_AnimatorTrackingControl.TrackingType.Tracking;
                }
            });
            BoolGateLayer(g, BuildNames.LipControlLayer, faceConds, FaceOn, (st, on) =>
            {
                if (faceSet) SetBlendWeight(st, NativePlan.SetFace, on ? 1 : 0, mapper.nativeLerpSpeed);
            });
            if (mapper.gestureMode != GestureDisableMode.None) GestureLayer(g, EyeOn, FaceOn);

            var go = new GameObject("VRCFT Mapper (generated)");
            go.transform.SetParent(root, false);
            var merge = go.AddComponent<ModularAvatarMergeAnimator>();
            merge.animator = ctrl;
            merge.layerType = VRCAvatarDescriptor.AnimLayerType.FX;
            merge.pathMode = MergeAnimatorPathMode.Absolute;
            merge.matchAvatarWriteDefaults = false;

            var maParams = go.AddComponent<ModularAvatarParameters>();
            if (mapper.nativeToggle)
                maParams.parameters.Add(new ParameterConfig { nameOrPrefix = NativeFT, syncType = ParameterSyncType.Bool, saved = true, defaultValue = 1 });
            if (mapper.gestureMode != GestureDisableMode.None)
                maParams.parameters.Add(new ParameterConfig { nameOrPrefix = GestureTools.DisabledParam, syncType = ParameterSyncType.NotSynced, localOnly = true });

            if (mapper.generateTrackingMenu && mapper.nativeToggle)
            {
                var menuGo = new GameObject(mapper.menuName);
                menuGo.transform.SetParent(go.transform, false);
                menuGo.AddComponent<ModularAvatarMenuInstaller>();
                var mi = menuGo.AddComponent<ModularAvatarMenuItem>();
                mi.Control = new VRCExpressionsMenu.Control
                {
                    name = mapper.menuName, type = VRCExpressionsMenu.Control.ControlType.Toggle, value = 1,
                    parameter = new VRCExpressionsMenu.Control.Parameter { name = NativeFT },
                };
            }

            // 目ボーン: 転送した視線で Additive の加工パイプラインを動かす (AAP はレイヤー間で共有されないので有効判定も Additive 側に作る)
            if (boneGaze)
                BuildGaze(ctx, mapper, go, p => NativePlan.ForwardPrefix + p, EyeOn,
                    gg => BoolGateLayer(gg, "VRCFT Eye Gate", eyeConds, EyeOn, null));
        }

        /// <summary>
        /// Bool パラメータがすべて true のとき On、どれかが false のとき Off。AAP に 1/0 を書き、各ステートで setup を呼ぶ。
        /// </summary>
        static void BoolGateLayer(Gen g, string name, string[] bools, string aap, System.Action<AnimatorState, bool> setup)
        {
            foreach (var b in bools) g.Decl(b, b == NativeFT ? 1 : 0, AnimatorControllerParameterType.Bool);
            g.Decl(aap);
            var sm = g.Layer(name);
            var off = sm.AddState("Off"); off.motion = g.Aap(aap, 0); off.writeDefaultValues = true;
            var on = sm.AddState("On"); on.motion = g.Aap(aap, 1); on.writeDefaultValues = true;
            sm.defaultState = off;
            var t = off.AddTransition(on); t.hasExitTime = false; t.duration = 0;
            foreach (var b in bools) t.AddCondition(AnimatorConditionMode.If, 0, b);
            foreach (var b in bools)
            {
                var back = on.AddTransition(off); back.hasExitTime = false; back.duration = 0;
                back.AddCondition(AnimatorConditionMode.IfNot, 0, b);
            }
            setup?.Invoke(off, false);
            setup?.Invoke(on, true);
        }

        static void SetBlendWeight(AnimatorState st, string set, float weight, float speed)
        {
            var b = st.AddStateMachineBehaviour<VRCAnimatorFaceTrackingBlendshapesSet>();
            b.blendSets = new List<VRC_AnimatorFaceTrackingBlendshapesSet.BlendSet>
            {
                new VRC_AnimatorFaceTrackingBlendshapesSet.BlendSet
                {
                    type = VRC_AnimatorFaceTrackingBlendshapesSet.SourceType.Value,
                    blendshapesSetName = set,
                    weight = weight,
                    lerpSpeed = speed,
                },
            };
        }

        /// <summary>FacialExpressionsDisabled を、Eye / Face の有効判定 (AAP) から駆動する。Layer Control は後段で注入。</summary>
        static void GestureLayer(Gen g, string eyeParam, string faceParam)
        {
            g.Decl(GestureTools.DisabledParam, 0, AnimatorControllerParameterType.Bool);
            var sm = g.Layer(BuildNames.GestureControlLayer);
            var empty = g.Empty();
            var idle = sm.AddState("Idle"); idle.motion = empty; idle.writeDefaultValues = true;
            var trk = sm.AddState("Tracking"); trk.motion = empty; trk.writeDefaultValues = true;
            sm.defaultState = idle;
            foreach (var p in new[] { eyeParam, faceParam })
            {
                var t = idle.AddTransition(trk); t.hasExitTime = false; t.duration = 0;
                t.AddCondition(AnimatorConditionMode.Greater, 0.5f, p);
            }
            var back = trk.AddTransition(idle); back.hasExitTime = false; back.duration = 0;
            back.AddCondition(AnimatorConditionMode.Less, 0.5f, eyeParam);
            back.AddCondition(AnimatorConditionMode.Less, 0.5f, faceParam);
            foreach (var (st, v) in new[] { (idle, 0f), (trk, 1f) })
            {
                var d = st.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
                d.localOnly = false;
                d.parameters.Add(new VRC_AvatarParameterDriver.Parameter
                { type = VRC_AvatarParameterDriver.ChangeType.Set, name = GestureTools.DisabledParam, value = v });
            }
        }

        // ---------------- 範囲外用シェイプキー ----------------

        /// <summary>
        /// src の変形量を scale 倍したシェイプキー dst を追加する。
        /// scale &gt; 1: 元の各段を weight/scale に置き、100% には元の 100×scale% (最後の区間を延長) を置く。
        /// scale &lt; 0: 元の最初の区間を 0 より手前へ延長した変形 (= 逆向き) を 100% に置く。
        /// どちらも Unity が 100% 超 / 負の重みでシェイプキーを動かしたときと同じ形になる。
        /// </summary>
        public static void AddScaledShape(Mesh mesh, string src, string dst, float scale)
        {
            int si = mesh.GetBlendShapeIndex(src);
            if (si < 0 || mesh.GetBlendShapeIndex(dst) >= 0) return;
            int vc = mesh.vertexCount, fc = mesh.GetBlendShapeFrameCount(si);
            var w = new float[fc];
            var dv = new Vector3[fc][]; var dn = new Vector3[fc][]; var dt = new Vector3[fc][];
            for (int f = 0; f < fc; f++)
            {
                w[f] = mesh.GetBlendShapeFrameWeight(si, f);
                dv[f] = new Vector3[vc]; dn[f] = new Vector3[vc]; dt[f] = new Vector3[vc];
                mesh.GetBlendShapeFrameVertices(si, f, dv[f], dn[f], dt[f]);
            }

            Vector3[] Extrapolate(Vector3[][] d, float target)
            {
                // 最後の区間 (1 段なら 0 → w0) を target まで延長
                int b = target >= 0 ? fc - 1 : 0;
                int a = target >= 0 ? b - 1 : -1;
                float wa = a >= 0 ? w[a] : 0, wb = w[b];
                var r = new Vector3[vc];
                for (int i = 0; i < vc; i++)
                {
                    var da = a >= 0 ? d[a][i] : Vector3.zero;
                    r[i] = d[b][i] + (d[b][i] - da) * ((target - wb) / (wb - wa));
                    if (target < 0) r[i] = d[0][i] * (target / w[0]);
                }
                return r;
            }

            if (scale > 0)
            {
                for (int f = 0; f < fc; f++)
                {
                    float nw = w[f] / scale;
                    if (nw < 99.999f) mesh.AddBlendShapeFrame(dst, nw, dv[f], dn[f], dt[f]);
                }
                float target = 100f * scale;
                mesh.AddBlendShapeFrame(dst, 100f, Extrapolate(dv, target), Extrapolate(dn, target), Extrapolate(dt, target));
            }
            else
            {
                float target = 100f * scale; // 負
                mesh.AddBlendShapeFrame(dst, 100f, Extrapolate(dv, target), Extrapolate(dn, target), Extrapolate(dt, target));
            }
        }
    }
}
