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

[assembly: ExportsPlugin(typeof(VRCFTMapper.Editor.VRCFTMapperPlugin))]

namespace VRCFTMapper.Editor
{
    /// <summary>
    /// ビルド時に FX / Additive レイヤー、MA Parameters、MA Menu を生成し Modular Avatar にマージさせる。
    /// 元のアバターは一切変更しない (非破壊)。
    ///
    /// FX に生成されるレイヤー:
    ///   VRCFT Gate          … LipActive = Lip OR Expression (AAP), 平滑化係数 K (IsLocal で切替)
    ///   VRCFT Eye Control   … Tracking Control (目/まぶた)
    ///   VRCFT Lip Control   … Tracking Control (口/リップシンク)
    ///   VRCFT Gesture Control … FacialExpressionsDisabled 駆動 + (後段で) Layer Control 注入
    ///   VRCFT Decode        … Binary → float (AAP)
    ///   VRCFT Smooth        … 指数平滑 (AAP)
    ///   VRCFT Mapping       … シェイプキー出力 (重み = トラッキング有効フラグ)
    /// Additive:
    ///   VRCFT Gaze          … 目ボーン (マッスル)
    /// </summary>
    public class VRCFTMapperPlugin : Plugin<VRCFTMapperPlugin>
    {
        public override string QualifiedName => "works.hikage.vrcft-mapper";
        public override string DisplayName => "VRCFT Mapper";

        protected override void Configure()
        {
            InPhase(BuildPhase.Generating)
                .BeforePlugin("nadena.dev.modular-avatar")
                .Run("Generate VRCFT animator", ctx =>
                {
                    foreach (var mapper in ctx.AvatarRootObject.GetComponentsInChildren<VRCFTMapper>(true))
                        Generate(ctx, mapper);
                });

            // MA のマージ・アニメーターの確定後に、最終 FX のジェスチャーレイヤーへ注入
            InPhase(BuildPhase.Optimizing)
                .Run("Inject gesture control", ctx =>
                {
                    var desc = ctx.AvatarRootObject.GetComponent<VRCAvatarDescriptor>();
                    foreach (var mapper in ctx.AvatarRootObject.GetComponentsInChildren<VRCFTMapper>(true))
                    {
                        var fx = desc.baseAnimationLayers
                            .Where(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX)
                            .Select(l => l.animatorController as AnimatorController)
                            .FirstOrDefault();
                        GestureTools.Inject(mapper, fx, ctx.AvatarRootTransform);
                        Object.DestroyImmediate(mapper);
                    }
                });
        }

        // ---- 共通パラメータ名 ----
        public const string One = "VRCFTM/One";
        public const string EyeActive = "EyeTrackingActive";
        public const string LipTrackingActive = "LipTrackingActive";
        public const string ExprActive = "ExpressionTrackingActive";
        public const string LipActive = "VRCFTM/LipActive";
        const string K = "VRCFTM/K", InvK = "VRCFTM/1-K";

        static void Save(BuildContext ctx, Object o)
        {
#pragma warning disable CS0618
            if (ctx.AssetContainer != null && !EditorUtility.IsPersistent(o))
                AssetDatabase.AddObjectToAsset(o, ctx.AssetContainer);
#pragma warning restore CS0618
        }

        class Gen
        {
            public BuildContext ctx;
            public AnimatorController ctrl;
            readonly HashSet<string> declared = new HashSet<string>();

            public void Decl(string n, float def = 0, AnimatorControllerParameterType t = AnimatorControllerParameterType.Float)
            {
                if (!declared.Add(n)) return;
                ctrl.AddParameter(new AnimatorControllerParameter { name = n, type = t, defaultFloat = def, defaultBool = def > 0.5f });
            }

            public BlendTree Direct(string name)
            {
                var t = new BlendTree { name = name, blendType = BlendTreeType.Direct, hideFlags = HideFlags.HideInHierarchy };
                Save(ctx, t);
                return t;
            }

            public void Child(BlendTree parent, Motion m, string weightParam, float threshold = 0)
            {
                var ch = parent.children;
                ArrayUtility.Add(ref ch, new ChildMotion { motion = m, directBlendParameter = weightParam, threshold = threshold, timeScale = 1 });
                parent.children = ch;
            }

            public AnimationClip Aap(string param, float value)
            {
                var clip = new AnimationClip { name = $"{param}={value:0.###}" };
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), param),
                    AnimationCurve.Constant(0, 1f / 60f, value));
                Save(ctx, clip);
                return clip;
            }

            /// <summary>dst = scale × src (src は -1..1)。Direct ツリーに並べると和になる。</summary>
            public BlendTree Scaled(string src, string dst, float scale)
            {
                var bt = new BlendTree { name = $"{dst}←{scale:0.##}×{src}", blendType = BlendTreeType.Simple1D, blendParameter = src,
                    useAutomaticThresholds = false, hideFlags = HideFlags.HideInHierarchy };
                Save(ctx, bt);
                bt.AddChild(Aap(dst, -scale), -1);
                bt.AddChild(Aap(dst, scale), 1);
                return bt;
            }

            /// <summary>dst = f(src) を n 点の折れ線で近似 (src は -1..1)。</summary>
            public BlendTree Sample(string src, string dst, System.Func<float, float> f, int n = 33)
            {
                var bt = new BlendTree { name = $"{dst}=f({src})", blendType = BlendTreeType.Simple1D, blendParameter = src,
                    useAutomaticThresholds = false, hideFlags = HideFlags.HideInHierarchy };
                Save(ctx, bt);
                for (int i = 0; i < n; i++)
                {
                    float x = Mathf.Lerp(-1, 1, i / (float)(n - 1));
                    bt.AddChild(Aap(dst, f(x)), x);
                }
                return bt;
            }

            /// <summary>src → dst をそのままコピーする 1D ツリー (-1..1)。</summary>
            public BlendTree Identity(string src, string dst)
            {
                var bt = new BlendTree { name = $"{dst}←{src}", blendType = BlendTreeType.Simple1D, blendParameter = src,
                    useAutomaticThresholds = false, hideFlags = HideFlags.HideInHierarchy };
                Save(ctx, bt);
                bt.AddChild(Aap(dst, -1), -1);
                bt.AddChild(Aap(dst, 1), 1);
                return bt;
            }

            public AnimatorStateMachine Layer(string name, Motion motion = null)
            {
                var sm = new AnimatorStateMachine { name = name, hideFlags = HideFlags.HideInHierarchy };
                if (EditorUtility.IsPersistent(ctrl)) AssetDatabase.AddObjectToAsset(sm, ctrl);
                if (motion != null)
                {
                    var st = sm.AddState(name);
                    st.motion = motion;
                    st.writeDefaultValues = true;
                }
                ctrl.AddLayer(new AnimatorControllerLayer { name = name, stateMachine = sm, defaultWeight = 1 });
                return sm;
            }

            public AnimationClip Empty()
            {
                var c = new AnimationClip { name = "VRCFT Empty" };
                AnimationUtility.SetEditorCurve(c, EditorCurveBinding.FloatCurve("", typeof(Animator), One), AnimationCurve.Constant(0, 1f / 60f, 1));
                Save(ctx, c);
                return c;
            }
        }

        public static void Generate(BuildContext ctx, VRCFTMapper mapper)
        {
            var root = ctx.AvatarRootTransform;
            var maps = mapper.mappings.Where(m => m.enabled && m.renderer != null && !string.IsNullOrEmpty(m.blendShape)).ToList();
            var gazeParams = mapper.GazeParams().ToList();
            if (maps.Count == 0 && gazeParams.Count == 0) return;

            var ctrl = new AnimatorController { name = "VRCFT Mapper (generated)" };
            Save(ctx, ctrl);
            var g = new Gen { ctx = ctx, ctrl = ctrl };
            g.Decl(One, 1);
            g.Decl(EyeActive); g.Decl(LipTrackingActive); g.Decl(ExprActive); g.Decl(LipActive);
            g.Decl("IsLocal");
            g.Decl(K, mapper.smoothRemote); g.Decl(InvK, 1 - mapper.smoothRemote);

            // ===== ① Gate: LipActive = L + E - L*E / ④ K = IsLocal ? kL : kR =====
            var gate = g.Direct("Gate");
            g.Child(gate, g.Aap(LipActive, 1), LipTrackingActive);
            g.Child(gate, g.Aap(LipActive, 1), ExprActive);
            var le = g.Direct("L*E");
            g.Child(le, g.Aap(LipActive, -1), ExprActive);
            g.Child(gate, le, LipTrackingActive);
            if (mapper.enableSmoothing)
            {
                float kL = mapper.smoothLocal, kR = mapper.smoothRemote;
                g.Child(gate, g.Aap(K, kR), One);
                g.Child(gate, g.Aap(K, kL - kR), "IsLocal");
                g.Child(gate, g.Aap(InvK, 1 - kR), One);
                g.Child(gate, g.Aap(InvK, -(kL - kR)), "IsLocal");
            }
            g.Layer("VRCFT Gate", gate);

            // ===== ③ Tracking Control =====
            if (mapper.trackingControl)
            {
                var hasEye = maps.Any(m => GateOf(m) == FTGate.Eye) || gazeParams.Count > 0;
                var hasLip = maps.Any(m => GateOf(m) == FTGate.Lip);
                if (hasEye) ToggleLayer(g, "VRCFT Eye Control", EyeActive, (st, on) =>
                {
                    var tc = st.AddStateMachineBehaviour<VRCAnimatorTrackingControl>();
                    tc.trackingEyes = on ? VRC_AnimatorTrackingControl.TrackingType.Animation : VRC_AnimatorTrackingControl.TrackingType.Tracking;
                });
                if (hasLip) ToggleLayer(g, "VRCFT Lip Control", LipActive, (st, on) =>
                {
                    var tc = st.AddStateMachineBehaviour<VRCAnimatorTrackingControl>();
                    tc.trackingMouth = on ? VRC_AnimatorTrackingControl.TrackingType.Animation : VRC_AnimatorTrackingControl.TrackingType.Tracking;
                });
            }

            // ===== ② Gesture Control (FacialExpressionsDisabled) =====
            if (mapper.gestureMode != GestureDisableMode.None)
            {
                g.Decl(GestureTools.DisabledParam, 0, AnimatorControllerParameterType.Bool);
                var sm = g.Layer(BuildNames.GestureControlLayer);
                var empty = g.Empty();
                var idle = sm.AddState("Idle"); idle.motion = empty; idle.writeDefaultValues = true;
                var trk = sm.AddState("Tracking"); trk.motion = empty; trk.writeDefaultValues = true;
                sm.defaultState = idle;
                foreach (var p in new[] { EyeActive, LipActive })
                {
                    var t = idle.AddTransition(trk);
                    t.hasExitTime = false; t.duration = 0;
                    t.AddCondition(AnimatorConditionMode.Greater, 0.5f, p);
                }
                var back = trk.AddTransition(idle);
                back.hasExitTime = false; back.duration = 0;
                back.AddCondition(AnimatorConditionMode.Less, 0.5f, EyeActive);
                back.AddCondition(AnimatorConditionMode.Less, 0.5f, LipActive);
                foreach (var (st, v) in new[] { (idle, 0f), (trk, 1f) })
                {
                    var d = st.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
                    d.localOnly = false; // 入力 (Active) は同期されるので全員で実行
                    d.parameters.Add(new VRC_AvatarParameterDriver.Parameter
                    { type = VRC_AvatarParameterDriver.ChangeType.Set, name = GestureTools.DisabledParam, value = v });
                }
                // Layer Control は MA マージ後 (Optimizing) に GestureTools.Inject で追加
            }

            // ===== パラメータ単位の情報 =====
            var paramInfo = new Dictionary<string, FTMapping>();
            // 視線系マッピングは目の加工結果を使う (視線方式が「なし」のときは無効)
            var gazeMaps = maps.Where(m => VRCFTMapper.IsGaze(m.parameter)).ToList();
            maps = maps.Where(m => !VRCFTMapper.IsGaze(m.parameter) || mapper.gazeMode != GazeMode.None).ToList();
            foreach (var m in maps)
            {
                if (VRCFTMapper.IsGaze(m.parameter)) continue;
                if (!paramInfo.TryGetValue(m.parameter, out var p)) paramInfo[m.parameter] = m.Clone();
                else
                {
                    if (m.type == FTParamType.Binary && p.type == FTParamType.Binary)
                    { p.binaryBits = Mathf.Max(p.binaryBits, m.binaryBits); p.signed |= m.signed; }
                    if (m.smoothing >= 0) p.smoothing = m.smoothing;
                }
            }

            // ===== Decode (Binary → float) =====
            var decode = g.Direct("Decode");
            var raw = new Dictionary<string, string>();
            bool anyBinary = false;
            foreach (var kv in paramInfo)
            {
                var p = kv.Value;
                var full = mapper.FullName(kv.Key);
                if (p.type == FTParamType.Float) { g.Decl(full); raw[kv.Key] = full; continue; }
                anyBinary = true;
                var dec = "VRCFTM/Decoded/" + kv.Key;
                g.Decl(dec); raw[kv.Key] = dec;
                int steps = (1 << p.binaryBits) - 1;
                if (!p.signed)
                {
                    for (int b = 0; b < p.binaryBits; b++)
                    { var bit = full + (1 << b); g.Decl(bit); g.Child(decode, g.Aap(dec, (1 << b) / (float)steps), bit); }
                }
                else
                {
                    var neg = full + "Negative";
                    var notNeg = "VRCFTM/NotNeg/" + kv.Key;
                    g.Decl(neg); g.Decl(notNeg, 1);
                    g.Child(decode, g.Aap(notNeg, 1), One);
                    g.Child(decode, g.Aap(notNeg, -1), neg);
                    var pos = g.Direct(kv.Key + "+"); var ng = g.Direct(kv.Key + "-");
                    for (int b = 0; b < p.binaryBits; b++)
                    {
                        var bit = full + (1 << b); g.Decl(bit);
                        g.Child(pos, g.Aap(dec, (1 << b) / (float)steps), bit);
                        g.Child(ng, g.Aap(dec, -(1 << b) / (float)steps), bit);
                    }
                    g.Child(decode, pos, notNeg);
                    g.Child(decode, ng, neg);
                }
            }
            if (anyBinary) g.Layer("VRCFT Decode", decode);

            // ===== ④ Smooth: S ← (1-k)S + kT =====
            var input = new Dictionary<string, string>(raw);
            var smooth = g.Direct("Smooth");
            bool anySmooth = false;
            foreach (var kv in raw)
            {
                var p = paramInfo[kv.Key];
                if (!mapper.enableSmoothing && p.smoothing < 0) continue;
                bool useGlobal = p.smoothing < 0;
                float k = useGlobal ? mapper.smoothLocal : p.smoothing;
                if (k >= 0.999f) continue; // 1 = 平滑なし
                string kp = K, ikp = InvK;
                if (!useGlobal)
                {
                    kp = $"VRCFTM/K/{kv.Key}"; ikp = $"VRCFTM/1-K/{kv.Key}";
                    g.Decl(kp, k); g.Decl(ikp, 1 - k); // 定数 (アニメーションしない)
                }
                var s = "VRCFTM/Smooth/" + kv.Key;
                g.Decl(s);
                g.Child(smooth, g.Identity(s, s), ikp);
                g.Child(smooth, g.Identity(kv.Value, s), kp);
                input[kv.Key] = s;
                anySmooth = true;
            }
            if (anySmooth) g.Layer("VRCFT Smooth", smooth);

            // ===== 目の加工 (FX 側: シェイプキー視線 / まぶた追従で使用) =====
            var eye = mapper.eye;
            bool lidFollow = mapper.gazeMode != GazeMode.None && eye.lidRenderer != null &&
                             (eye.lidFollowDown > 0 || eye.lidFollowUp > 0);
            bool fxEye = mapper.gazeMode != GazeMode.None && (gazeMaps.Count > 0 || lidFollow);
            (string l, string r, string y) eyeOut = default;
            if (fxEye) eyeOut = EyePipeline(g, mapper);

            // ===== Mapping =====
            if (maps.Count > 0 || lidFollow)
            {
                var mapTree = g.Direct("Mapping");
                foreach (var m in maps)
                {
                    string inParam = !VRCFTMapper.IsGaze(m.parameter) ? input[m.parameter]
                        : EyeMath.IsYParam(m.parameter) ? eyeOut.y : EyeMath.IsLeft(m) ? eyeOut.l : eyeOut.r;
                    var path = AnimationUtility.CalculateTransformPath(m.renderer.transform, root);
                    var binding = EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + m.blendShape);
                    int n = m.useCurve ? Mathf.Max(3, mapper.curveSamples) : 2;
                    float lo = Mathf.Min(m.inMin, m.inMax), hi = Mathf.Max(m.inMin, m.inMax);
                    if (hi - lo < 1e-4f) hi = lo + 1e-4f;
                    var bt = new BlendTree
                    {
                        name = m.parameter + " → " + m.blendShape, blendType = BlendTreeType.Simple1D,
                        blendParameter = inParam, useAutomaticThresholds = false, hideFlags = HideFlags.HideInHierarchy,
                    };
                    Save(ctx, bt);
                    for (int i = 0; i < n; i++)
                    {
                        float x = Mathf.Lerp(lo, hi, i / (float)(n - 1));
                        float y = FTMath.Evaluate(m, x);
                        var clip = new AnimationClip { name = $"{m.blendShape}={y:0.###}" };
                        AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, 1f / 60f, y * 100f));
                        Save(ctx, clip);
                        bt.AddChild(clip, x);
                    }
                    // ① トラッキング無効時は重み 0 → 出力しない (WD でデフォルト値)
                    g.Child(mapTree, bt, VRCFTMapper.IsGaze(m.parameter) ? EyeActive : GateParam(m));
                }
                if (lidFollow)
                {
                    var lidPath = AnimationUtility.CalculateTransformPath(eye.lidRenderer.transform, root);
                    foreach (bool left in new[] { true, false })
                    {
                        var down = left ? eye.lidDownLeft : eye.lidDownRight;
                        var up = left ? eye.lidUpLeft : eye.lidUpRight;
                        if (string.IsNullOrEmpty(down) && string.IsNullOrEmpty(up)) continue;
                        string lidIn = input.ContainsKey(left ? "EyeLidLeft" : "EyeLidRight") ? input[left ? "EyeLidLeft" : "EyeLidRight"]
                                     : input.ContainsKey("EyeLid") ? input["EyeLid"] : null;
                        g.Child(mapTree, LidTree(ctx, eye, lidPath, down, up, eyeOut.y, lidIn), EyeActive);
                    }
                }
                g.Layer("VRCFT Mapping", mapTree);
            }

            // ===== Modular Avatar =====
            var go = new GameObject("VRCFT Mapper (generated)");
            go.transform.SetParent(root, false);
            var merge = go.AddComponent<ModularAvatarMergeAnimator>();
            merge.animator = ctrl;
            merge.layerType = VRCAvatarDescriptor.AnimLayerType.FX;
            merge.pathMode = MergeAnimatorPathMode.Absolute;
            merge.matchAvatarWriteDefaults = false;

            var maParams = go.AddComponent<ModularAvatarParameters>();
            void Sync(string n, ParameterSyncType t, bool saved = false, float def = 0)
                => maParams.parameters.Add(new ParameterConfig { nameOrPrefix = n, syncType = t, saved = saved, defaultValue = def });

            foreach (var kv in paramInfo)
            {
                var p = kv.Value; var full = mapper.FullName(kv.Key);
                if (p.type == FTParamType.Float) Sync(full, ParameterSyncType.Float);
                else
                {
                    for (int b = 0; b < p.binaryBits; b++) Sync(full + (1 << b), ParameterSyncType.Bool);
                    if (p.signed) Sync(full + "Negative", ParameterSyncType.Bool);
                }
            }
            foreach (var gp in gazeParams) Sync(mapper.FullName(gp), ParameterSyncType.Float);

            // ① 有効フラグ (メニューなしでも VRCFT からの受信用に宣言)
            Sync(EyeActive, ParameterSyncType.Bool);
            Sync(LipTrackingActive, ParameterSyncType.Bool);
            Sync(ExprActive, ParameterSyncType.Bool);
            if (mapper.gestureMode != GestureDisableMode.None)
                maParams.parameters.Add(new ParameterConfig { nameOrPrefix = GestureTools.DisabledParam, syncType = ParameterSyncType.NotSynced, localOnly = true });

            if (mapper.generateTrackingMenu) BuildMenu(go, mapper);
            if (mapper.gazeMode == GazeMode.Bone) BuildGaze(ctx, mapper, go);
        }

        static FTGate GateOf(FTMapping m) => m.gate == FTGate.Auto ? FTCatalog.AutoGate(m.parameter) : m.gate;

        static string GateParam(FTMapping m) => GateOf(m) switch
        {
            FTGate.Eye => EyeActive,
            FTGate.Lip => LipActive,
            _ => One,
        };

        static void ToggleLayer(Gen g, string name, string param, System.Action<AnimatorState, bool> setup)
        {
            var sm = g.Layer(name);
            var empty = g.Empty();
            var off = sm.AddState("Off"); off.motion = empty; off.writeDefaultValues = true;
            var on = sm.AddState("On"); on.motion = empty; on.writeDefaultValues = true;
            sm.defaultState = off;
            var a = off.AddTransition(on); a.hasExitTime = false; a.duration = 0; a.AddCondition(AnimatorConditionMode.Greater, 0.5f, param);
            var b = on.AddTransition(off); b.hasExitTime = false; b.duration = 0; b.AddCondition(AnimatorConditionMode.Less, 0.5f, param);
            setup(off, false);
            setup(on, true);
        }

        static void BuildMenu(GameObject parent, VRCFTMapper mapper)
        {
            var menuGo = new GameObject(mapper.menuName);
            menuGo.transform.SetParent(parent.transform, false);
            menuGo.AddComponent<ModularAvatarMenuInstaller>();
            var sub = menuGo.AddComponent<ModularAvatarMenuItem>();
            sub.Control = new VRCExpressionsMenu.Control { name = mapper.menuName, type = VRCExpressionsMenu.Control.ControlType.SubMenu };
            sub.MenuSource = SubmenuSource.Children;

            void Toggle(string label, string param)
            {
                var t = new GameObject(label);
                t.transform.SetParent(menuGo.transform, false);
                var mi = t.AddComponent<ModularAvatarMenuItem>();
                mi.Control = new VRCExpressionsMenu.Control
                {
                    name = label, type = VRCExpressionsMenu.Control.ControlType.Toggle, value = 1,
                    parameter = new VRCExpressionsMenu.Control.Parameter { name = param },
                };
            }
            Toggle("Eye Tracking", EyeActive);
            Toggle("Lip Tracking", LipTrackingActive);
        }

        // ===== 目の加工パイプライン =====
        // AAP はプレイアブルレイヤー (FX / Additive) 間で共有されないので、必要なコントローラごとに同じものを生成する。
        // 各段は別レイヤー (AAP は次のレイヤーで読める) → 合計で数フレームの遅れがある。
        static (string l, string r, string y) EyePipeline(Gen g, VRCFTMapper mapper)
        {
            var e = mapper.eye;
            bool comb = mapper.gazeCombined;
            string P(string n) => "VRCFTM/Eye/" + n;

            string rl = mapper.FullName(comb ? "EyeX" : "EyeLeftX");
            string rr = mapper.FullName(comb ? "EyeX" : "EyeRightX");
            string ry = mapper.FullName("EyeY");
            g.Decl(One, 1); g.Decl(rl); g.Decl(rr); g.Decl(ry);

            // 1) 平滑化
            string il = rl, ir = rr, iy = ry;
            float k = mapper.gazeSmoothing;
            if (k < 0.999f)
            {
                g.Decl(P("K"), k); g.Decl(P("1-K"), 1 - k);
                var sm = g.Direct("Eye Smooth");
                string Smooth(string src, string name)
                {
                    var d = P(name); g.Decl(d);
                    g.Child(sm, g.Identity(d, d), P("1-K"));
                    g.Child(sm, g.Identity(src, d), P("K"));
                    return d;
                }
                il = Smooth(rl, "SmL");
                ir = comb ? il : Smooth(rr, "SmR");
                iy = Smooth(ry, "SmY");
                g.Layer("VRCFT Eye Smooth", sm);
            }

            // 2) 整形 (オフセット / 倍率 / デッドゾーン / カーブ)
            var shape = g.Direct("Eye Shape");
            string sl = P("SL"), sr = P("SR"), sy = P("SY");
            g.Decl(sl); g.Decl(sy);
            g.Child(shape, g.Sample(il, sl, x => EyeMath.Shape(e, x, false)), One);
            if (!comb) { g.Decl(sr); g.Child(shape, g.Sample(ir, sr, x => EyeMath.Shape(e, x, false)), One); }
            g.Child(shape, g.Sample(iy, sy, x => EyeMath.Shape(e, x, true)), One);
            g.Layer("VRCFT Eye Shape", shape);

            // 3) 揺らぎ (30 秒ループのクリップ)
            string jx = P("JX"), jy = P("JY");
            bool jitter = e.tremor > 0 || e.saccade > 0;
            if (jitter)
            {
                g.Decl(jx); g.Decl(jy);
                var (cx, cy) = EyeMath.BakeJitter(e);
                var clip = new AnimationClip { name = "VRCFT Eye Jitter" };
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), jx), cx);
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), jy), cy);
                var st = AnimationUtility.GetAnimationClipSettings(clip);
                st.loopTime = true;
                AnimationUtility.SetAnimationClipSettings(clip, st);
                Save(g.ctx, clip);
                g.Layer("VRCFT Eye Jitter", clip);
            }

            // 4) 輻輳 (寄り目) 制御: c = (L+R)/2, v = (L−R)/2, v' = soft(v)
            string c = sl, vp = null;
            if (!comb)
            {
                c = P("C"); var v = P("V"); vp = P("VP");
                g.Decl(c); g.Decl(v); g.Decl(vp);
                var verge = g.Direct("Eye Verge");
                g.Child(verge, g.Scaled(sl, c, 0.5f), One);
                g.Child(verge, g.Scaled(sr, c, 0.5f), One);
                g.Child(verge, g.Scaled(sl, v, 0.5f), One);
                g.Child(verge, g.Scaled(sr, v, -0.5f), One);
                g.Layer("VRCFT Eye Verge", verge);

                var soft = g.Direct("Eye Soft Limit");
                g.Child(soft, g.Sample(v, vp, x => EyeMath.SoftVerge(e, x)), One);
                g.Layer("VRCFT Eye Soft Limit", soft);
            }

            // 5) 出力 L = c + v' + jx, R = c − v' + jx, Y = sy + jy
            string ol = P("OL"), orr = P("OR"), oy = P("OY");
            g.Decl(ol); g.Decl(orr); g.Decl(oy);
            var outT = g.Direct("Eye Out");
            g.Child(outT, g.Identity(c, ol), One);
            g.Child(outT, g.Identity(c, orr), One);
            if (vp != null)
            {
                g.Child(outT, g.Identity(vp, ol), One);
                g.Child(outT, g.Scaled(vp, orr, -1), One);
            }
            g.Child(outT, g.Identity(sy, oy), One);
            if (jitter)
            {
                g.Child(outT, g.Identity(jx, ol), One);
                g.Child(outT, g.Identity(jx, orr), One);
                g.Child(outT, g.Identity(jy, oy), One);
            }
            g.Layer("VRCFT Eye Out", outT);
            return (ol, orr, oy);
        }

        /// <summary>
        /// まぶた追従: 下を見ると上まぶたを下げる / 上を見ると見開く。
        /// まぶたの開き具合 (EyeLid 入力: 0 閉 .. 0.75 通常) が分かる場合は、閉じているほど弱める (2D ブレンド)。
        /// Mapping の Direct ツリーに入れるので、まばたきのシェイプキーと加算される。
        /// </summary>
        static Motion LidTree(BuildContext ctx, EyeSettings e, string path, string down, string up, string yParam, string lidParam)
        {
            AnimationClip Clip(float d, float u)
            {
                var c = new AnimationClip { name = $"LidFollow d={d:0.##} u={u:0.##}" };
                if (!string.IsNullOrEmpty(down))
                    AnimationUtility.SetEditorCurve(c, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + down),
                        AnimationCurve.Constant(0, 1f / 60f, d * 100f));
                if (!string.IsNullOrEmpty(up))
                    AnimationUtility.SetEditorCurve(c, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + up),
                        AnimationCurve.Constant(0, 1f / 60f, u * 100f));
                Save(ctx, c);
                return c;
            }

            if (lidParam == null)
            {
                var bt = new BlendTree { name = "Lid Follow", blendType = BlendTreeType.Simple1D, blendParameter = yParam,
                    useAutomaticThresholds = false, hideFlags = HideFlags.HideInHierarchy };
                Save(ctx, bt);
                foreach (var y in new[] { -1f, 0f, 1f })
                {
                    var (d, u) = EyeMath.LidFollow(e, y, 1);
                    bt.AddChild(Clip(d, u), y);
                }
                return bt;
            }

            var bt2 = new BlendTree { name = "Lid Follow (× openness)", blendType = BlendTreeType.FreeformCartesian2D,
                blendParameter = yParam, blendParameterY = lidParam, hideFlags = HideFlags.HideInHierarchy };
            Save(ctx, bt2);
            foreach (var y in new[] { -1f, 0f, 1f })
                foreach (var lid in new[] { 0f, 0.75f, 1f })
                {
                    var (d, u) = EyeMath.LidFollow(e, y, lid / 0.75f);
                    bt2.AddChild(Clip(d, u), new Vector2(y, lid));
                }
            return bt2;
        }

        // ===== 目ボーン (Additive, マッスル) =====
        static void BuildGaze(BuildContext ctx, VRCFTMapper mapper, GameObject parent)
        {
            var anim = ctx.AvatarRootObject.GetComponent<Animator>();
            var ctrl = new AnimatorController { name = "VRCFT Gaze (generated)" };
            Save(ctx, ctrl);
            var g = new Gen { ctx = ctx, ctrl = ctrl };
            g.Decl(One, 1);
            g.Decl(EyeActive);
            var (ol, orr, oy) = EyePipeline(g, mapper);

            // 加算の基準ポーズ = マッスル 0
            var zero = new AnimationClip { name = "Gaze Zero" };
            foreach (var mu in new[] { GazeTools.LeftDownUp, GazeTools.LeftInOut, GazeTools.RightDownUp, GazeTools.RightInOut })
                AnimationUtility.SetEditorCurve(zero, EditorCurveBinding.FloatCurve("", typeof(Animator), mu), AnimationCurve.Constant(0, 1f / 60f, 0));
            Save(ctx, zero);

            AnimationClip MuscleClip(string muscle, float value)
            {
                var c = new AnimationClip { name = $"{muscle}={value:0.##}" };
                AnimationUtility.SetEditorCurve(c, EditorCurveBinding.FloatCurve("", typeof(Animator), muscle), AnimationCurve.Constant(0, 1f / 60f, value));
                var s = AnimationUtility.GetAnimationClipSettings(c);
                s.hasAdditiveReferencePose = true;
                s.additiveReferencePoseClip = zero;
                AnimationUtility.SetAnimationClipSettings(c, s);
                Save(ctx, c);
                return c;
            }

            BlendTree Axis(string param, string muscle, System.Func<float, float> angleOf)
            {
                var range = GazeTools.MuscleRange(anim, muscle);
                var bt = new BlendTree { name = muscle, blendType = BlendTreeType.Simple1D, blendParameter = param,
                    useAutomaticThresholds = false, hideFlags = HideFlags.HideInHierarchy };
                Save(ctx, bt);
                foreach (var x in new[] { -1f, 0f, 1f })
                    bt.AddChild(MuscleClip(muscle, GazeTools.AngleToMuscle(angleOf(x), range)), x);
                return bt;
            }

            var tree = g.Direct("Gaze");
            g.Child(tree, Axis(ol, GazeTools.LeftInOut, x => GazeTools.YawAngle(mapper, true, x)), EyeActive);
            g.Child(tree, Axis(orr, GazeTools.RightInOut, x => GazeTools.YawAngle(mapper, false, x)), EyeActive);
            g.Child(tree, Axis(oy, GazeTools.LeftDownUp, v => GazeTools.PitchAngle(mapper, v)), EyeActive);
            g.Child(tree, Axis(oy, GazeTools.RightDownUp, v => GazeTools.PitchAngle(mapper, v)), EyeActive);
            g.Layer("VRCFT Gaze", tree);

            var child = new GameObject("VRCFT Gaze (generated)");
            child.transform.SetParent(parent.transform, false);
            var merge = child.AddComponent<ModularAvatarMergeAnimator>();
            merge.animator = ctrl;
            merge.layerType = VRCAvatarDescriptor.AnimLayerType.Additive;
            merge.pathMode = MergeAnimatorPathMode.Absolute;
            merge.matchAvatarWriteDefaults = false;
        }
    }
}
