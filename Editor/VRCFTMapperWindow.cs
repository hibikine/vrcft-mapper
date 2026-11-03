using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace VRCFTMapper.Editor
{
    public partial class VRCFTMapperWindow : EditorWindow
    {
        [SerializeField] VRCAvatarDescriptor avatar;
        VRCFTMapper mapper;

        readonly HeadPreview preview = new HeadPreview();
        readonly OscInput osc = new OscInput();
        readonly OscQueryHost oscQuery = new OscQueryHost();
        bool showOscDebug;
        Vector2 oscDebugScroll;
        string lastQuerySignature;
        double avatarChangeAt = -1; // 遅延送信 (mDNS 広告が VRCFT に届いてから)
        readonly Dictionary<string, float> manual = new Dictionary<string, float>();
        readonly HashSet<int> foldout = new HashSet<int>();
        readonly HashSet<int> detail = new HashSet<int>();

        bool liveScene;          // シーン上のアバターにも反映 (AnimationMode / 非破壊)
        bool quantize = true;    // 同期量子化を再現
        bool showBudgetDetail;
        bool showTracking = true, showGestureLayers;
        bool previewEyeActive = true, previewLipActive = true; // 手動時の有効フラグ
        bool previewSmoothing = true;                          // OSC/再生時に平滑化を再現
        readonly Dictionary<string, float> smoothState = new Dictionary<string, float>();
        readonly Dictionary<string, float> manualGaze = new Dictionary<string, float>();
        int rightTab;                 // 0 = マッピング, 1 = 目・視線
        NativePlan nativePlan;        // ネイティブ出力の変換計画 (Layout ごとに更新)
        Vector2 eyeScroll;
        float manualVerge;            // 手動テスト: 片目あたりの内寄せ量
        float gazeDistance;           // 手動テスト: 注視距離 (m)。0 = 使わない
        bool jitterPreview = true;
        double lastJitterRepaint;
        bool showEyeAdjust = true, showEyeCurve = true, showEyeVerge = true, showEyeLid = true, showEyeJitter = true;
        double lastSmoothTick;
        List<GestureTools.LayerInfo> gestureScan;
        string filter = "";
        Vector2 scroll, leftScroll;
        AnimationClip sampleClip;

        [MenuItem("Tools/VRCFT Mapper")]
        public static void Open() => GetWindow<VRCFTMapperWindow>("VRCFT Mapper");

        public static void Open(VRCFTMapper m)
        {
            var w = GetWindow<VRCFTMapperWindow>("VRCFT Mapper");
            w.avatar = m.GetComponentInParent<VRCAvatarDescriptor>();
            w.mapper = m;
            w.preview.Cleanup();
        }

        void OnEnable()
        {
            EditorApplication.update += Tick;
            Undo.undoRedoPerformed += OnUndo;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            minSize = new Vector2(900, 520);
        }

        void OnDisable()
        {
            EditorApplication.update -= Tick;
            Undo.undoRedoPerformed -= OnUndo;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            DestroyPlayModeMapper();
            StopLiveScene();
            osc.Dispose();
            oscQuery.Dispose();
            preview.Dispose();
            if (sampleClip != null) DestroyImmediate(sampleClip);
        }

        void OnUndo() { gestureScan = null; ApplyPreview(); Repaint(); }

        void Tick()
        {
            bool changed = osc.Tick();
            if (avatarChangeAt > 0 && EditorApplication.timeSinceStartup >= avatarChangeAt)
            {
                avatarChangeAt = -1;
                if (oscQuery.Running && oscQuery.vrcftPortDetected) { oscQuery.SendAvatarChange(); Repaint(); }
            }
            double now = EditorApplication.timeSinceStartup;
            float dt = Mathf.Clamp((float)(now - lastSmoothTick), 0, 0.2f);
            lastSmoothTick = now;
            if (mapper != null && previewSmoothing && osc.source != OscInput.Source.Manual)
                changed |= StepSmoothing(dt);
            if (mapper != null && jitterPreview && mapper.gazeMode != GazeMode.None && EyeOn &&
                (mapper.eye.tremor > 0 || mapper.eye.saccade > 0) && now - lastJitterRepaint > 1 / 30.0)
            {
                lastJitterRepaint = now;
                changed = true;
            }
            if (EditorApplication.isPlaying && now - lastPlaySend > 1 / 60.0 && ResolveTargets())
            {
                lastPlaySend = now;
                SendToPlayMode();
            }
            if (changed) { ApplyPreview(); Repaint(); }
            else if (osc.Listening || osc.playing) Repaint();
        }

        /// <summary>実機の Smooth レイヤーと同じ式 S ← S + k(T − S) を 90fps 相当で進める。</summary>
        bool StepSmoothing(float dt)
        {
            bool moved = false;
            float frames = dt * 90f;
            foreach (var m in mapper.mappings)
            {
                if (!RawInput(m, out float target)) continue;
                if (quantize) target = FTMath.Quantize(m, target);
                float k = m.smoothing >= 0 ? m.smoothing : (mapper.enableSmoothing ? mapper.smoothLocal : 1f);
                smoothState.TryGetValue(m.parameter, out var cur);
                float next = target + (cur - target) * Mathf.Pow(1 - Mathf.Clamp01(k), frames);
                if (Mathf.Abs(next - cur) > 1e-5f) moved = true;
                smoothState[m.parameter] = next;
            }
            foreach (var gp in GazeNames())
            {
                float target = GazeRaw(gp);
                smoothState.TryGetValue("gaze:" + gp, out var cur);
                float next = target + (cur - target) * Mathf.Pow(1 - mapper.gazeSmoothing, frames);
                if (Mathf.Abs(next - cur) > 1e-5f) moved = true;
                smoothState["gaze:" + gp] = next;
            }
            return moved;
        }

        IEnumerable<string> GazeNames()
            => mapper.gazeMode == GazeMode.None ? new string[0]
             : mapper.gazeCombined ? new[] { "EyeX", "EyeY" } : new[] { "EyeLeftX", "EyeRightX", "EyeY" };

        float GazeRaw(string gp)
        {
            if (osc.source != OscInput.Source.Manual && osc.TryFind(mapper, gp, out var v)) return v;
            if (gp == "EyeLeftX" || gp == "EyeRightX") { manualGaze.TryGetValue("EyeX", out v); return v; }
            manualGaze.TryGetValue(gp, out v); return v;
        }

        /// <summary>現在の視線 (入力 → 目の加工 → 揺らぎ)。実機の Eye パイプラインと同じ計算。</summary>
        EyeMath.Result EyeNow(out float rawL, out float rawR, out float rawY)
        {
            bool comb = mapper.gazeCombined;
            rawL = GazeInput(comb ? "EyeX" : "EyeLeftX");
            rawR = GazeInput(comb ? "EyeX" : "EyeRightX");
            rawY = GazeInput("EyeY");
            if (osc.source == OscInput.Source.Manual && !comb)
            {
                manualGaze.TryGetValue("EyeX", out var cx);
                float u = gazeDistance > 0 ? EyeMath.VergenceForDistance(gazeDistance, preview.EyeDistance, mapper.gazeYawIn) : manualVerge;
                rawL = cx + u; rawR = cx - u;
            }
            var j = jitterPreview && EyeOn ? EyeMath.Jitter(mapper.eye, (float)EditorApplication.timeSinceStartup) : Vector2.zero;
            return EyeMath.Process(mapper.eye, rawL, rawR, rawY, comb, j);
        }

        EyeMath.Result EyeNow() => EyeNow(out _, out _, out _);

        float GazeInput(string gp)
        {
            if (previewSmoothing && osc.source != OscInput.Source.Manual && smoothState.TryGetValue("gaze:" + gp, out var s)) return s;
            return GazeRaw(gp);
        }

        bool ActiveFlag(string name, bool manualValue)
        {
            if (osc.source != OscInput.Source.Manual && mapper != null && osc.TryFind(mapper, name, out var v)) return v > 0.5f;
            return manualValue;
        }

        bool EyeOn => ActiveFlag(VRCFTMapperPlugin.EyeActive, previewEyeActive);
        bool LipOn => ActiveFlag(VRCFTMapperPlugin.LipTrackingActive, previewLipActive) || ActiveFlag(VRCFTMapperPlugin.ExprActive, false);

        bool Gated(FTMapping m)
        {
            if (m.useExpression) return m.gate == FTGate.Always || (m.gate == FTGate.Auto ? (NativeKeys.UsesEyeKeys(m.expression) ? EyeOn : LipOn) : m.gate == FTGate.Eye ? EyeOn : LipOn);
            if (VRCFTMapper.IsGaze(m.parameter)) return EyeOn && mapper.gazeMode != GazeMode.None;
            var g = m.gate == FTGate.Auto ? FTCatalog.AutoGate(m.parameter) : m.gate;
            return g == FTGate.Eye ? EyeOn : g == FTGate.Lip ? LipOn : true;
        }

        bool RawInput(FTMapping m, out float v)
        {
            if (osc.source != OscInput.Source.Manual && osc.TryGetInput(mapper, m, out v)) return true;
            v = Rest(m);
            return false;
        }

        static string ManualKey(FTMapping m) => m.useExpression ? "expr:" + m.expression : m.parameter;

        float Rest(FTMapping m)
            => manual.TryGetValue(ManualKey(m), out var mv) ? mv : (m.useExpression ? 0f : FTCatalog.Find(m.parameter)?.rest ?? 0f);

        // ------------------------------------------------------------
        float InputFor(FTMapping m, out bool external)
        {
            external = false;
            float v;
            if (mapper != null && !m.useExpression && VRCFTMapper.IsGaze(m.parameter) && mapper.gazeMode != GazeMode.None)
            {
                // 視線は「目・視線」タブで入力し、加工後の値を使う
                external = true;
                var r = EyeNow();
                return EyeMath.IsYParam(m.parameter) ? r.y : EyeMath.IsLeft(m) ? r.left : r.right;
            }
            if (osc.source != OscInput.Source.Manual && mapper != null && osc.TryGetInput(mapper, m, out v))
            {
                external = true;
                if (quantize) v = FTMath.Quantize(m, v);
                // 実機と同じく、量子化 (同期) → デコード → 平滑化 の順
                if (previewSmoothing && smoothState.TryGetValue(m.parameter, out var sm)) v = sm;
                return v;
            }
            v = Rest(m);
            return quantize ? FTMath.Quantize(m, v) : v;
        }

        Dictionary<(SkinnedMeshRenderer, string), float> ComputeWeights()
        {
            var w = new Dictionary<(SkinnedMeshRenderer, string), float>();
            if (mapper == null) return w;
            foreach (var m in mapper.mappings)
            {
                if (!m.enabled || m.renderer == null || string.IsNullOrEmpty(m.blendShape)) continue;
                if (!Gated(m)) continue; // トラッキング無効 → 出力しない (元の値)
                var key = (m.renderer, m.blendShape);
                w.TryGetValue(key, out var cur);
                w[key] = cur + FTMath.Evaluate(m, InputFor(m, out _)) * 100f * StyleWeightPreview(m);
            }
            // まぶた追従 (実機の Lid Follow ツリーと同じ計算)
            var e = mapper.eye;
            if (mapper.gazeMode != GazeMode.None && EyeOn && e.lidRenderer != null && (e.lidFollowDown > 0 || e.lidFollowUp > 0))
            {
                float y = EyeNow().y;
                foreach (bool left in new[] { true, false })
                {
                    var lidMap = mapper.mappings.FirstOrDefault(x => x.enabled && x.parameter == (left ? "EyeLidLeft" : "EyeLidRight"))
                              ?? mapper.mappings.FirstOrDefault(x => x.enabled && x.parameter == "EyeLid");
                    float open = lidMap != null ? InputFor(lidMap, out _) / 0.75f : 1f;
                    var (d, u) = EyeMath.LidFollow(e, Mathf.Clamp(y, -1, 1), open);
                    void Add(string shape, float v)
                    {
                        if (string.IsNullOrEmpty(shape)) return;
                        var k2 = (e.lidRenderer, shape);
                        w.TryGetValue(k2, out var c2);
                        w[k2] = c2 + v * 100f;
                    }
                    Add(left ? e.lidDownLeft : e.lidDownRight, d);
                    Add(left ? e.lidUpLeft : e.lidUpRight, u);
                }
            }
            return w;
        }

        void ApplyPreview()
        {
            if (avatar == null) return;
            var weights = ComputeWeights();
            if (preview.IsValid)
            {
                preview.ResetWeights();
                foreach (var kv in weights) preview.SetWeight(kv.Key.Item1, kv.Key.Item2, kv.Value);
                if (mapper != null && mapper.gazeMode == GazeMode.Bone && EyeOn)
                {
                    var r = EyeNow();
                    float cl(float v) => Mathf.Clamp(v, -1, 1); // 実機の 1D ブレンドツリーも端で止まる
                    preview.SetGaze(GazeTools.YawAngle(mapper, true, cl(r.left)), GazeTools.YawAngle(mapper, false, cl(r.right)), GazeTools.PitchAngle(mapper, cl(r.y)));
                }
                else preview.SetGaze(0, 0, 0);
            }
            if (liveScene) SampleScene(weights);
        }

        void SampleScene(Dictionary<(SkinnedMeshRenderer, string), float> weights)
        {
            if (!AnimationMode.InAnimationMode()) AnimationMode.StartAnimationMode();
            if (sampleClip == null) sampleClip = new AnimationClip { hideFlags = HideFlags.HideAndDontSave };
            sampleClip.ClearCurves();
            foreach (var kv in weights)
            {
                var path = AnimationUtility.CalculateTransformPath(kv.Key.Item1.transform, avatar.transform);
                AnimationUtility.SetEditorCurve(sampleClip,
                    EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + kv.Key.Item2),
                    AnimationCurve.Constant(0, 1, kv.Value));
            }
            AnimationMode.BeginSampling();
            AnimationMode.SampleAnimationClip(avatar.gameObject, sampleClip, 0);
            AnimationMode.EndSampling();
            SceneView.RepaintAll();
        }

        void StopLiveScene()
        {
            if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
        }

        // ------------------------------------------------------------
        void OnGUI()
        {
            ResolveTargets();
            DrawToolbar();
            if (avatar == null)
            {
                EditorGUILayout.HelpBox("VRC Avatar Descriptor を持つアバターを指定してください。", MessageType.Info);
                return;
            }
            if (mapper == null && EditorApplication.isPlaying)
            {
                EditorGUILayout.HelpBox("プレイモード中です。VRCFT Mapper の設定はビルド時に取り除かれるため表示できません。\n編集モードでこのウィンドウにアバターを表示してからプレイモードに入ると、その設定でテストできます。", MessageType.Info);
                return;
            }
            if (mapper == null)
            {
                EditorGUILayout.HelpBox("このアバターに VRCFT Mapper がありません。", MessageType.Info);
                if (GUILayout.Button("VRCFT Mapper を追加 (子オブジェクト)", GUILayout.Height(30)))
                {
                    var go = new GameObject("VRCFT Mapper");
                    Undo.RegisterCreatedObjectUndo(go, "VRCFT Mapper を追加");
                    Undo.SetTransformParent(go.transform, avatar.transform, "VRCFT Mapper を追加");
                    mapper = Undo.AddComponent<VRCFTMapper>(go);
                }
                return;
            }

            preview.Setup(avatar.gameObject);
            preview.ApplySplits(mapper);
            if (Event.current.type == EventType.Layout)
                nativePlan = mapper.outputMode == FTOutputMode.Native ? NativePlan.Build(mapper) : null;

            using (new EditorGUILayout.HorizontalScope())
            {
                using (var s = new EditorGUILayout.ScrollViewScope(leftScroll, GUILayout.Width(336)))
                {
                    leftScroll = s.scrollPosition;
                    DrawLeft();
                }
                using (new EditorGUILayout.VerticalScope())
                    DrawRight();
            }
        }

        void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUI.BeginChangeCheck();
                avatar = (VRCAvatarDescriptor)EditorGUILayout.ObjectField(avatar, typeof(VRCAvatarDescriptor), true, GUILayout.Width(260));
                if (EditorGUI.EndChangeCheck()) { mapper = null; preview.Cleanup(); StopLiveScene(); }
                if (GUILayout.Button("選択から", EditorStyles.toolbarButton, GUILayout.Width(60)) && Selection.activeGameObject != null)
                {
                    avatar = Selection.activeGameObject.GetComponentInParent<VRCAvatarDescriptor>();
                    mapper = null; preview.Cleanup();
                }
                GUILayout.FlexibleSpace();
                if (mapper != null)
                {
                    EditorGUI.BeginChangeCheck();
                    GUILayout.Label("出力", GUILayout.Width(28));
                    var om = (FTOutputMode)EditorGUILayout.Popup((int)mapper.outputMode,
                        new[] { "ネイティブ (VRChat FT)", "従来 (VRCFT パラメータ)" }, EditorStyles.toolbarPopup, GUILayout.Width(150));
                    if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(mapper, "出力方式の変更"); mapper.outputMode = om; Dirty(); }
                }
                if (mapper != null && mapper.outputMode == FTOutputMode.Legacy)
                {
                    EditorGUI.BeginChangeCheck();
                    GUILayout.Label("Prefix", GUILayout.Width(38));
                    var pre = EditorGUILayout.DelayedTextField(mapper.prefix, EditorStyles.toolbarTextField, GUILayout.Width(90));
                    if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(mapper, "Prefix 変更"); mapper.prefix = pre; Dirty(); }
                }
            }
        }

        // ================= 左: プレビュー / OSC / 容量 =================
        void DrawLeft()
        {
            var r = GUILayoutUtility.GetRect(320, 320, GUILayout.ExpandWidth(false));
            if (Event.current.type == EventType.Repaint) ApplyPreviewIfManual();
            preview.Draw(r);
            EditorGUILayout.LabelField("左ドラッグ:回転 右/中ドラッグ:移動 ホイール:ズーム", EditorStyles.centeredGreyMiniLabel);

            EditorGUI.BeginChangeCheck();
            liveScene = EditorGUILayout.ToggleLeft("シーンにライブプレビュー (非破壊 / AnimationMode)", liveScene);
            quantize = EditorGUILayout.ToggleLeft("同期の量子化を再現 (Binary 段差 / 8bit float)", quantize);
            if (EditorGUI.EndChangeCheck()) { if (!liveScene) StopLiveScene(); ApplyPreview(); }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("入力ソース", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            osc.source = (OscInput.Source)GUILayout.Toolbar((int)osc.source, new[] { "手動", "OSC ライブ", "録画再生" });
            if (EditorGUI.EndChangeCheck())
            {
                osc.values.Clear();
                if (osc.source == OscInput.Source.OscLive && !osc.Listening) osc.StartListen();
                if (osc.source == OscInput.Source.Playback) osc.Seek(osc.playhead);
                ApplyPreview();
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(osc.Listening))
                        osc.port = EditorGUILayout.IntField("受信ポート", osc.port);
                    if (GUILayout.Button(osc.Listening ? "停止" : "受信開始", GUILayout.Width(70)))
                    { if (osc.Listening) osc.StopListen(); else osc.StartListen(); }
                }
                EditorGUILayout.LabelField(osc.status + $"  (受信 {osc.receivedCount})", EditorStyles.miniLabel);

                // VRChat を起動せずに VRCFT から送らせる (OSCQuery で VRChat を装う)
                EditorGUI.BeginChangeCheck();
                bool q = EditorGUILayout.ToggleLeft(new GUIContent("VRChat なしで VRCFT に接続 (OSCQuery)",
                    "このアバターのパラメータを OSCQuery で公開し、VRCFT の送信先を受信ポートにさせます。VRChat 起動中は OFF にしてください。"), oscQuery.Running);
                if (EditorGUI.EndChangeCheck())
                {
                    if (q)
                    {
                        if (!osc.Listening) osc.StartListen();
                        if (osc.source == OscInput.Source.Manual) { osc.source = OscInput.Source.OscLive; osc.values.Clear(); }
                        lastQuerySignature = null;
                        UpdateOscQueryParams();
                        oscQuery.Start(osc.port);
                        // VRCFT は mDNS で Unity を見つけた時点でパラメータを読みに来るので、ここでは /avatar/change を送らない
                    }
                    else oscQuery.Stop();
                }
                if (oscQuery.Running || !string.IsNullOrEmpty(oscQuery.status))
                    EditorGUILayout.LabelField($"{oscQuery.status}  mDNS 応答 {oscQuery.queriesAnswered} / HTTP {oscQuery.httpRequests}", EditorStyles.miniLabel);
                if (oscQuery.Running)
                {
                    UpdateOscQueryParams();
                    EditorGUILayout.LabelField(oscQuery.httpRequests > 0
                        ? "✓ VRCFT がパラメータを取得しました" : "VRCFT からの取得待ち… (VRCFT を再起動すると確実)", EditorStyles.miniLabel);
                    if (osc.port != 9000)
                        EditorGUILayout.HelpBox($"VRCFT は設定の「OSC 送信ポート」(既定 9000) に送ります。VRCFT 側を {osc.port} にするか、受信ポートを 9000 にしてください (VRChat 未起動なら 9000 は空いています)。", MessageType.Info);
                    EditorGUILayout.LabelField($"/avatar/change 送信 {oscQuery.avatarChangeSent} 回", EditorStyles.miniLabel);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        oscQuery.vrcftPort = EditorGUILayout.IntField(new GUIContent(oscQuery.vrcftPortDetected ? "VRCFT ポート (検出)" : "VRCFT ポート",
                            "VRCFT の OSC 受信ポート。OSCQuery 接続後は mDNS から自動検出"), oscQuery.vrcftPort);
                        if (GUILayout.Button(new GUIContent("再通知", "/avatar/change を送り直して VRCFT にパラメータを再取得させる"), GUILayout.Width(56)))
                            oscQuery.SendAvatarChange();
                    }
                    if (GUILayout.Button(new GUIContent("旧形式の OSC 設定ファイルも書き出す",
                        "OSCQuery で取得できない場合の保険。VRChat の OSC フォルダにこのテスト用アバター ID の JSON を作成します")))
                    {
                        var files = oscQuery.WriteLegacyConfig(QueryParams(), avatar.name);
                        oscQuery.SendAvatarChange();
                        Debug.Log("[VRCFT Mapper] OSC 設定ファイルを書き出しました:\n" + string.Join("\n", files));
                        ShowNotification(new GUIContent($"{files.Count} 件書き出し → 再通知しました"));
                    }
                }

                showOscDebug = EditorGUILayout.Foldout(showOscDebug, $"受信アドレス ({osc.lastSeen.Count})", true);
                if (showOscDebug)
                {
                    using (var sv = new EditorGUILayout.ScrollViewScope(oscDebugScroll, GUILayout.Height(140)))
                    {
                        oscDebugScroll = sv.scrollPosition;
                        foreach (var kv in osc.lastSeen)
                            EditorGUILayout.LabelField(kv.Key, kv.Value.ToString("0.###"), EditorStyles.miniLabel);
                    }
                    if (GUILayout.Button("クリア", EditorStyles.miniButton)) osc.lastSeen.Clear();
                }

                var c = GUI.backgroundColor;
                if (osc.recording) GUI.backgroundColor = FTControls.Red;
                if (GUILayout.Button(osc.recording ? "● 録画停止して保存" : "● OSC を録画"))
                {
                    if (osc.recording) { var rec = osc.EndRecord(); if (rec != null) osc.clip = rec; }
                    else osc.BeginRecord();
                }
                GUI.backgroundColor = c;
            }

            if (osc.source == OscInput.Source.Playback)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUI.BeginChangeCheck();
                    osc.clip = (OscRecording)EditorGUILayout.ObjectField("録画", osc.clip, typeof(OscRecording), false);
                    if (EditorGUI.EndChangeCheck()) { osc.Seek(0); ApplyPreview(); }
                    if (osc.clip != null)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button(osc.playing ? "❚❚" : "▶", GUILayout.Width(36))) osc.playing = !osc.playing;
                            if (GUILayout.Button("■", GUILayout.Width(28))) { osc.playing = false; osc.Seek(0); ApplyPreview(); }
                            osc.loop = GUILayout.Toggle(osc.loop, "ループ", GUILayout.Width(56));
                        }
                        EditorGUI.BeginChangeCheck();
                        float t = EditorGUILayout.Slider(osc.playhead, 0, Mathf.Max(0.01f, osc.clip.length));
                        if (EditorGUI.EndChangeCheck()) { osc.Seek(t); ApplyPreview(); }
                        EditorGUILayout.LabelField($"{osc.clip.samples.Count} サンプル / {osc.clip.length:0.0}s", EditorStyles.miniLabel);
                    }
                }
            }

            EditorGUILayout.Space();
            DrawPlayModePanel();
            EditorGUILayout.Space();
            DrawTrackingPanel();
            EditorGUILayout.Space();
            DrawBudget();
        }

        float lastManualHash;
        void ApplyPreviewIfManual()
        {
            // 手動入力・設定変更は OnGUI 中に反映 (軽量)
            float h = 0;
            if (mapper != null)
                foreach (var m in mapper.mappings) h += m.inMin * 3 + m.inMax * 5 + m.outMin * 7 + m.outMax * 11 + (m.useCurve ? 13 : 0) + (m.enabled ? 17 : 0);
            foreach (var kv in manual) h += kv.Value * (kv.Key.GetHashCode() % 97);
            foreach (var kv in manualGaze) h += kv.Value * (kv.Key.GetHashCode() % 89);
            if (mapper != null) h += (int)mapper.gazeMode * 19 + mapper.gazeYawIn + mapper.gazeYawOut * 2 + mapper.gazeUp * 3 + mapper.gazeDown * 4
                + (mapper.gazeInvertX ? 23 : 0) + (mapper.gazeInvertY ? 29 : 0) + (mapper.gazeCombined ? 31 : 0);
            h += (previewEyeActive ? 37 : 0) + (previewLipActive ? 41 : 0);
            if (mapper != null) h += (JsonUtility.ToJson(mapper.eye).GetHashCode() & 0xffff) * 0.001f;
            h += manualVerge * 43 + gazeDistance * 47;
            if (!Mathf.Approximately(h, lastManualHash)) { lastManualHash = h; ApplyPreview(); }
        }

        /// <summary>OSCQuery で公開するパラメータ (ビルド時に MA Parameters で宣言するものと同じ)。</summary>
        void UpdateOscQueryParams()
        {
            if (mapper == null) return;
            var ps = QueryParams();
            var sig = string.Join(",", ps.Select(p => p.Item1 + (p.Item2 ? "b" : "f")));
            if (sig == lastQuerySignature) return;
            bool changed = lastQuerySignature != null;
            lastQuerySignature = sig;
            oscQuery.SetParameters(ps);
            if (changed)
            {
                oscQuery.BumpAvatar();        // 構成変更 → 別アバター扱い
                oscQuery.SetParameters(ps);   // 新 ID を反映
                if (oscQuery.vrcftPortDetected) oscQuery.SendAvatarChange();  // VRCFT に再取得させる
            }
        }

        List<(string, bool)> QueryParams()
        {
            var ps = new List<(string, bool)>();
            if (mapper == null) return ps;
            foreach (var g in mapper.mappings.Where(m => m.enabled && !VRCFTMapper.IsGaze(m.parameter)).GroupBy(m => m.parameter))
            {
                var full = mapper.FullName(g.Key);
                var m = g.First();
                if (g.All(x => x.type == FTParamType.Float)) { ps.Add((full, false)); continue; }
                int bits = g.Max(x => x.binaryBits);
                for (int b = 0; b < bits; b++) ps.Add((full + (1 << b), true));
                if (g.Any(x => x.signed)) ps.Add((full + "Negative", true));
            }
            foreach (var gp in mapper.GazeParams()) ps.Add((mapper.FullName(gp), false));
            foreach (var a in new[] { "EyeTrackingActive", "LipTrackingActive", "ExpressionTrackingActive" }) ps.Add((a, true));
            return ps;
        }

        // ================= ①〜⑤ トラッキング制御 =================
        void DrawTrackingPanel()
        {
            showTracking = EditorGUILayout.Foldout(showTracking, "トラッキング制御", true, EditorStyles.foldoutHeader);
            if (!showTracking) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                // プレビュー用の有効フラグ (OSC 受信時は受信値が優先)
                EditorGUILayout.LabelField("プレビュー", EditorStyles.miniBoldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    previewEyeActive = GUILayout.Toggle(previewEyeActive, EyeOn ? "Eye ●" : "Eye ○", EditorStyles.miniButtonLeft);
                    previewLipActive = GUILayout.Toggle(previewLipActive, LipOn ? "Lip ●" : "Lip ○", EditorStyles.miniButtonRight);
                    if (EditorGUI.EndChangeCheck()) ApplyPreview();
                }
                if (osc.source != OscInput.Source.Manual)
                    EditorGUILayout.LabelField("● = 受信中の EyeTrackingActive / LipTrackingActive", EditorStyles.miniLabel);

                EditorGUI.BeginChangeCheck();
                var so = new SerializedObject(mapper);
                so.Update();

                if (mapper.outputMode == FTOutputMode.Native)
                {
                    EditorGUILayout.LabelField("ネイティブ出力", EditorStyles.miniBoldLabel);
                    EditorGUILayout.PropertyField(so.FindProperty("nativeToggle"), new GUIContent("FT / 表情トグル", "メニューから FT モードと手動表情モードを切替 (同期 1bit)"));
                    EditorGUILayout.PropertyField(so.FindProperty("nativeLerpSpeed"), new GUIContent("切替の速さ"));
                    EditorGUILayout.PropertyField(so.FindProperty("nativeCurveSegments"), new GUIContent("カーブの区間数"));
                    EditorGUILayout.LabelField("有効判定は VRChat 標準の UserEyeTracking / UserFaceTracking を使います", EditorStyles.wordWrappedMiniLabel);
                }

                // ① メニュー
                EditorGUILayout.LabelField("① 有効フラグ / メニュー", EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(so.FindProperty("generateTrackingMenu"), new GUIContent("メニューにトグル追加"));
                using (new EditorGUI.DisabledScope(!mapper.generateTrackingMenu))
                    EditorGUILayout.PropertyField(so.FindProperty("menuName"), new GUIContent("メニュー名"));

                // ② ジェスチャー
                EditorGUILayout.LabelField("② ジェスチャー表情の停止", EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(so.FindProperty("gestureMode"), new GUIContent("方式"));
                EditorGUILayout.PropertyField(so.FindProperty("gestureBlendDuration"), new GUIContent("切替時間 (秒)"));

                // ③
                EditorGUILayout.LabelField("③ 標準まばたき / リップシンク", EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(so.FindProperty("trackingControl"), new GUIContent("トラッキング中は無効化"));

                // ④
                EditorGUILayout.LabelField("④ スムージング (小さいほど滑らか)", EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(so.FindProperty("enableSmoothing"), new GUIContent("有効"));
                using (new EditorGUI.DisabledScope(!mapper.enableSmoothing))
                {
                    EditorGUILayout.PropertyField(so.FindProperty("smoothLocal"), new GUIContent("自分 (k)"));
                    EditorGUILayout.PropertyField(so.FindProperty("smoothRemote"), new GUIContent("他人 (k)"));
                }

                // ⑤
                EditorGUILayout.LabelField("⑤ 視線 → 右の「目・視線」タブ", EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(so.FindProperty("gazeMode"), new GUIContent("方式"));
                // SerializedObject 経由なので Undo/Redo 対応
                if (so.ApplyModifiedProperties()) { gestureScan = null; Dirty(); }
                if (EditorGUI.EndChangeCheck()) ApplyPreview();

                previewSmoothing = EditorGUILayout.ToggleLeft("OSC/再生時にスムージングを再現 (90fps 相当)", previewSmoothing);

                if (mapper.gestureMode != GestureDisableMode.None) DrawGestureLayers();
            }
        }

        void DrawGestureLayers()
        {
            showGestureLayers = EditorGUILayout.Foldout(showGestureLayers, "対象ジェスチャーレイヤー", true);
            if (!showGestureLayers) return;
            // ボタンは常に描画する (|| の短絡で描画有無が Layout と Repaint で変わるとコントロール数がずれる)
            bool rescan = GUILayout.Button("再検出", EditorStyles.miniButton);
            if (gestureScan == null || rescan) gestureScan = GestureTools.Scan(avatar, mapper);
            if (gestureScan.Count == 0) { EditorGUILayout.LabelField("FX レイヤーがありません", EditorStyles.miniLabel); return; }
            foreach (var l in gestureScan)
            {
                bool target = !mapper.gestureExcluded.Contains(l.name) && (l.Detected || mapper.gestureForced.Contains(l.name));
                EditorGUI.BeginChangeCheck();
                bool nt = EditorGUILayout.ToggleLeft(new GUIContent($"{l.name}  [{l.source}]" + (l.Detected ? "  (自動)" : ""),
                    $"ジェスチャー参照: {l.usesGesture} / 顔シェイプキー: {l.touchesFace}"), target);
                if (!EditorGUI.EndChangeCheck()) continue;
                Undo.RecordObject(mapper, "ジェスチャーレイヤー対象変更");
                mapper.gestureExcluded.Remove(l.name);
                mapper.gestureForced.Remove(l.name);
                if (nt && !l.Detected) mapper.gestureForced.Add(l.name);
                if (!nt && l.Detected) mapper.gestureExcluded.Add(l.name);
                Dirty();
            }
            EditorGUILayout.LabelField("ビルド時は MA マージ後の最終 FX で再判定します", EditorStyles.miniLabel);
        }

        void DrawBudget()
        {
            var b = ParamBudget.Compute(avatar, mapper);
            EditorGUILayout.LabelField("同期パラメータ容量", EditorStyles.boldLabel);
            var r = GUILayoutUtility.GetRect(10, 20);
            EditorGUI.DrawRect(r, new Color(0, 0, 0, 0.4f));
            float W(int bits) => r.width * Mathf.Clamp01(bits / (float)b.limit);
            EditorGUI.DrawRect(new Rect(r.x, r.y, W(b.others), r.height), new Color(0.45f, 0.45f, 0.5f));
            EditorGUI.DrawRect(new Rect(r.x + W(b.others), r.y, Mathf.Min(W(b.ours), r.width - W(b.others)), r.height),
                b.total > b.limit ? FTControls.Red : new Color(0.55f, 0.45f, 1f));
            GUI.Label(r, $" {b.total} / {b.limit} bit   (FT {b.ours} bit・{b.ourParamCount} 個 / その他 {b.others})", EditorStyles.whiteMiniLabel);
            if (b.total > b.limit)
                EditorGUILayout.HelpBox($"{b.total - b.limit} bit 超過しています。Binary 化やビット数削減、両側パラメータへの統合を検討してください。", MessageType.Error);
            if (mapper != null && mapper.outputMode == FTOutputMode.Native && nativePlan != null)
            {
                var np = nativePlan;
                EditorGUILayout.LabelField("ネイティブ Face Tracking (ランク予測)", EditorStyles.boldLabel);
                void Row(string label, int v, int[] limits)
                {
                    var rank = NativePlan.Rank(v, limits);
                    var c = GUI.color;
                    GUI.color = rank == "Excellent" || rank == "Good" ? new Color(0.6f, 1f, 0.6f) : rank == "Medium" ? new Color(1f, 0.9f, 0.5f) : new Color(1f, 0.6f, 0.5f);
                    EditorGUILayout.LabelField(label, $"{v}  ({rank})", EditorStyles.miniLabel);
                    GUI.color = c;
                }
                Row("Face Tracking Operations", np.Operations, NativePlan.OpsLimits);
                Row("Forwarded Parameters", np.Forwarded, NativePlan.ForwardLimits);
                EditorGUILayout.LabelField("式の命令数", $"{np.Instructions} / {NativePlan.MaxTotalInstructions}", EditorStyles.miniLabel);
                EditorGUILayout.LabelField("直結 / 式 / 生成シェイプ", $"{np.setEntries.Count} / {np.behaviors.Count(x => x.shape != null)} / {np.generated.Count}", EditorStyles.miniLabel);
                foreach (var w in np.warnings) EditorGUILayout.HelpBox(w, MessageType.Warning);
            }
            showBudgetDetail = EditorGUILayout.Foldout(showBudgetDetail, "内訳");
            if (showBudgetDetail)
                foreach (var (n, bits) in b.breakdown)
                    EditorGUILayout.LabelField(n, bits + " bit", EditorStyles.miniLabel);
        }

        // ================= 右: マッピング =================
        void DrawRight()
        {
            rightTab = GUILayout.Toolbar(rightTab, new[] { "表情マッピング", "目・視線" }, GUILayout.Height(22));
            if (rightTab == 1) { DrawEyeTab(); return; }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("自動設定 (標準セット)", "VRChat で一般的な簡易パラメータをシェイプキー名から自動割り当て (既存を置換)"), GUILayout.Height(26)))
                {
                    if (mapper.mappings.Count == 0 || EditorUtility.DisplayDialog("自動設定", "既存のマッピングを置き換えます (Undo 可能)。", "OK", "キャンセル"))
                        ShowResult(AutoSetup.Run(mapper, avatar.gameObject, true, true));
                }
                if (GUILayout.Button(new GUIContent("自動設定 (全パラメータ)", "Unified Expressions の全パラメータを対象に検索 (既存を置換)"), GUILayout.Height(26)))
                {
                    if (mapper.mappings.Count == 0 || EditorUtility.DisplayDialog("自動設定", "既存のマッピングを置き換えます (Undo 可能)。", "OK", "キャンセル"))
                        ShowResult(AutoSetup.Run(mapper, avatar.gameObject, true, false));
                }
                if (GUILayout.Button(new GUIContent("未設定分を追加", "既存を残して見つかったものだけ追加"), GUILayout.Height(26)))
                    ShowResult(AutoSetup.Run(mapper, avatar.gameObject, false, false));
                if (GUILayout.Button("＋", GUILayout.Width(28), GUILayout.Height(26)))
                {
                    Undo.RecordObject(mapper, "マッピング追加");
                    mapper.mappings.Add(new FTMapping { renderer = AutoSetup.FaceRenderers(avatar.gameObject).FirstOrDefault() });
                    foldout.Add(mapper.mappings.Count - 1);
                    Dirty();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                filter = EditorGUILayout.TextField(filter, EditorStyles.toolbarSearchField);
                if (GUILayout.Button("全展開", EditorStyles.miniButtonLeft, GUILayout.Width(50))) for (int i = 0; i < mapper.mappings.Count; i++) foldout.Add(i);
                if (GUILayout.Button("全閉", EditorStyles.miniButtonRight, GUILayout.Width(40))) foldout.Clear();
                if (GUILayout.Button("入力リセット", EditorStyles.miniButton, GUILayout.Width(70))) { manual.Clear(); ApplyPreview(); }
            }

            using (var s = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = s.scrollPosition;
                for (int i = 0; i < mapper.mappings.Count; i++)
                {
                    var m = mapper.mappings[i];
                    if (!string.IsNullOrEmpty(filter) &&
                        !(m.parameter ?? "").ToLowerInvariant().Contains(filter.ToLowerInvariant()) &&
                        !(m.blendShape ?? "").ToLowerInvariant().Contains(filter.ToLowerInvariant())) continue;
                    if (DrawMapping(i, m)) break; // 削除時
                }
                EditorGUILayout.Space();
                DrawStyleGroup();
                DrawSplits();
                DrawLimits();
            }
        }

        void ShowResult(int n)
        {
            ShowNotification(new GUIContent($"{n} 件のマッピングを設定しました"));
            for (int i = 0; i < mapper.mappings.Count; i++) foldout.Add(i);
            ApplyPreview();
        }

        bool DrawMapping(int i, FTMapping m)
        {
            var spec = FTCatalog.Find(m.parameter);
            bool signedView = m.signed || (spec != null && spec.signed);
            float viewIn0 = signedView ? -1 : 0;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                // ---- ヘッダ ----
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool open = foldout.Contains(i);
                    bool nopen = EditorGUILayout.Foldout(open, "", true);
                    if (nopen != open) { if (nopen) foldout.Add(i); else foldout.Remove(i); }
                    GUILayout.Space(-38);

                    EditorGUI.BeginChangeCheck();
                    bool en = EditorGUILayout.Toggle(m.enabled, GUILayout.Width(16));
                    if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(mapper, "有効切替"); m.enabled = en; Dirty(); }

                    // パラメータ選択 (カタログ) + 自由入力
                    if (EditorGUILayout.DropdownButton(new GUIContent(m.useExpression ? "ƒ " + m.expression : string.IsNullOrEmpty(m.parameter) ? "(パラメータ)" : m.parameter,
                        spec != null ? spec.label : "カタログ外の名前"), FocusType.Keyboard, GUILayout.Width(170)))
                        ParamMenu(m);
                    EditorGUI.BeginChangeCheck();
                    var pn = EditorGUILayout.DelayedTextField(m.parameter, GUILayout.Width(120));
                    if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(mapper, "パラメータ名変更"); m.parameter = pn; Dirty(); }

                    GUILayout.Label("→", GUILayout.Width(16));
                    // シェイプキー選択
                    if (EditorGUILayout.DropdownButton(new GUIContent(string.IsNullOrEmpty(m.blendShape) ? "(シェイプキー)" : m.blendShape),
                        FocusType.Keyboard, GUILayout.MinWidth(120)))
                        ShapeMenu(m);

                    bool missing = m.renderer == null || m.renderer.sharedMesh == null ||
                        (m.renderer.sharedMesh.GetBlendShapeIndex(m.blendShape) < 0 && !SplitTools.IsSplitName(mapper, m.blendShape));
                    if (missing) GUILayout.Label(EditorGUIUtility.IconContent("console.warnicon.sml", "シェイプキーが見つかりません"), GUILayout.Width(20));
                    if (nativePlan != null && nativePlan.status.TryGetValue(m, out var ns))
                    {
                        var c0 = GUI.color;
                        GUI.color = ns.error ? FTControls.Red : ns.warning ? new Color(1f, 0.85f, 0.3f) : ns.text.StartsWith("直結") ? new Color(0.5f, 1f, 0.6f) : new Color(0.6f, 0.8f, 1f);
                        GUILayout.Label(new GUIContent(ns.error ? "✕" : ns.text.StartsWith("直結") ? "●" : "ƒ", ns.text), EditorStyles.miniBoldLabel, GUILayout.Width(14));
                        GUI.color = c0;
                    }

                    float input = InputFor(m, out bool ext);
                    GUILayout.Label($"{FTMath.Evaluate(m, input) * 100f:0}", EditorStyles.miniLabel, GUILayout.Width(30));

                    if (GUILayout.Button("⧉", EditorStyles.miniButtonLeft, GUILayout.Width(22)))
                    {
                        Undo.RecordObject(mapper, "マッピング複製");
                        mapper.mappings.Insert(i + 1, m.Clone());
                        Dirty();
                        return true;
                    }
                    if (GUILayout.Button("✕", EditorStyles.miniButtonRight, GUILayout.Width(22)))
                    {
                        Undo.RecordObject(mapper, "マッピング削除");
                        mapper.mappings.RemoveAt(i);
                        foldout.Remove(i);
                        Dirty();
                        ApplyPreview();
                        return true;
                    }
                }

                if (!foldout.Contains(i)) return false;

                if (nativePlan != null && nativePlan.status.TryGetValue(m, out var nst))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label("ネイティブ: " + nst.text, nst.error || nst.warning ? EditorStyles.boldLabel : EditorStyles.miniLabel);
                        GUILayout.FlexibleSpace();
                        EditorGUI.BeginChangeCheck();
                        bool os = EditorGUILayout.ToggleLeft(new GUIContent("範囲外はシェイプキー生成", "out が 0..1 を超える / 負になるとき、変形量を拡大・反転したシェイプキーをビルド時に生成"), m.overshootShape, GUILayout.Width(150));
                        if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(mapper, "シェイプキー生成の切替"); m.overshootShape = os; Dirty(); }
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    bool ue = EditorGUILayout.ToggleLeft(new GUIContent("式入力", "Tracking Key の式を入力にする (ネイティブ出力のみ)。使える関数: abs min max clamp"), m.useExpression, GUILayout.Width(64));
                    string ex = m.expression;
                    using (new EditorGUI.DisabledScope(!ue))
                        ex = EditorGUILayout.DelayedTextField(m.expression ?? "");
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(mapper, "式入力の変更");
                        m.useExpression = ue; m.expression = ex;
                        if (ue && mapper.outputMode == FTOutputMode.Legacy) ShowNotification(new GUIContent("式入力はネイティブ出力でのみ使えます"));
                        Dirty(); ApplyPreview();
                    }
                }

                // ---- 型 / 同期 ----
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    var rend = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(m.renderer, typeof(SkinnedMeshRenderer), true, GUILayout.Width(170));
                    var type = (FTParamType)EditorGUILayout.EnumPopup(m.type, GUILayout.Width(70));
                    int bits = m.binaryBits;
                    using (new EditorGUI.DisabledScope(m.type != FTParamType.Binary))
                        bits = EditorGUILayout.IntPopup(m.binaryBits, new[] { "1bit", "2bit", "3bit", "4bit", "5bit", "6bit", "7bit", "8bit" }, new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, GUILayout.Width(56));
                    bool sg = EditorGUILayout.ToggleLeft(new GUIContent("負値", "-1..0 を受ける (Binary は Negative ビット追加)"), m.signed, GUILayout.Width(48));
                    bool cv = EditorGUILayout.ToggleLeft("詳細(カーブ)", m.useCurve, GUILayout.Width(90));
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(mapper, "マッピング設定変更");
                        m.renderer = rend; m.type = type; m.binaryBits = bits; m.signed = sg; m.useCurve = cv;
                        if (cv) detail.Add(i);
                        Dirty();
                    }
                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"{FTMath.SyncBits(m)} bit", EditorStyles.miniLabel);
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    if (VRCFTMapper.IsGaze(m.parameter))
                    {
                        GUILayout.Label("使う目", GUILayout.Width(40));
                        var side = (EyeSide)EditorGUILayout.Popup((int)m.eyeSide,
                            new[] { $"自動 ({(EyeMath.IsLeft(m) ? "左" : "右")})", "左", "右" }, GUILayout.Width(70));
                        if (side != m.eyeSide) { Undo.RecordObject(mapper, "使う目の変更"); m.eyeSide = side; Dirty(); ApplyPreview(); }
                    }
                    if (mapper.styleGroup.enabled)
                    {
                        var opts = new[] { "スタイル: 常に" }.Concat(mapper.styleGroup.options.Select(o => "スタイル: " + o)).ToArray();
                        int sel = EditorGUILayout.Popup(Mathf.Clamp(m.style, 0, opts.Length - 1), opts, GUILayout.Width(110));
                        if (sel != m.style) { Undo.RecordObject(mapper, "スタイルの変更"); m.style = sel; Dirty(); ApplyPreview(); }
                    }
                    GUILayout.Label(new GUIContent("有効フラグ", "どのトラッキング有効フラグで ON/OFF するか"), GUILayout.Width(60));
                    var autoG = FTCatalog.AutoGate(m.parameter);
                    var gate = (FTGate)EditorGUILayout.Popup((int)m.gate,
                        new[] { $"自動 ({(autoG == FTGate.Eye ? "Eye" : "Lip")})", "Eye", "Lip", "常に有効" }, GUILayout.Width(90));
                    bool own = EditorGUILayout.ToggleLeft(new GUIContent("個別スムージング", "OFF = 全体設定 (自分/他人で係数切替)"), m.smoothing >= 0, GUILayout.Width(110));
                    float sm = m.smoothing;
                    using (new EditorGUI.DisabledScope(!own))
                        sm = EditorGUILayout.Slider(own ? Mathf.Max(0.01f, m.smoothing) : mapper.smoothLocal, 0.01f, 1f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(mapper, "有効フラグ/スムージング変更");
                        m.gate = gate; m.smoothing = own ? sm : -1f;
                        Dirty(); ApplyPreview();
                    }
                }

                // ---- in / out 範囲 (青▼ 下 / 赤▼ 上) ----
                float inp = InputFor(m, out bool external);
                float outv = FTMath.Evaluate(m, inp);

                float lo = m.inMin, hi = m.inMax;
                var r1 = GUILayoutUtility.GetRect(10, 26, GUILayout.ExpandWidth(true));
                if (FTControls.RangeArrows(r1, "in", ref lo, ref hi, viewIn0, 1, viewIn0, 1, false, mapper, inp))
                { m.inMin = lo; m.inMax = hi; Dirty(); ApplyPreview(); }

                lo = m.outMin; hi = m.outMax;
                var r2 = GUILayoutUtility.GetRect(10, 26, GUILayout.ExpandWidth(true));
                if (FTControls.RangeArrows(r2, "out", ref lo, ref hi, -1f, 2f, 0, 1, true, mapper, outv))
                { m.outMin = lo; m.outMax = hi; Dirty(); ApplyPreview(); }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent("⇅ out 反転", "out の上下を入れ替え"), EditorStyles.miniButton, GUILayout.Width(80)))
                    {
                        Undo.RecordObject(mapper, "out 反転");
                        (m.outMin, m.outMax) = (m.outMax, m.outMin);
                        Dirty(); ApplyPreview();
                    }
                    if (GUILayout.Button("範囲リセット", EditorStyles.miniButton, GUILayout.Width(80)))
                    {
                        Undo.RecordObject(mapper, "範囲リセット");
                        m.inMin = 0; m.inMax = 1; m.outMin = 0; m.outMax = 1;
                        Dirty(); ApplyPreview();
                    }
                    GUILayout.Label("Shift+ドラッグで 0.05 スナップ", EditorStyles.miniLabel);
                }

                // ---- テスト入力 & グラフ ----
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUILayout.VerticalScope())
                    {
                        using (new EditorGUI.DisabledScope(external))
                        {
                            EditorGUI.BeginChangeCheck();
                            float mv = Rest(m);
                            float nv = EditorGUILayout.Slider(VRCFTMapper.IsGaze(m.parameter) ? "加工後 (目・視線タブ)" : external ? "テスト入力 (OSC)" : "テスト入力", external ? inp : mv, viewIn0, 1);
                            if (EditorGUI.EndChangeCheck()) { manual[ManualKey(m)] = nv; ApplyPreview(); }
                        }
                        EditorGUILayout.LabelField($"in {inp:0.000}  →  out {outv:0.000}  (ウェイト {outv * 100f:0.0})", EditorStyles.miniLabel);
                    }
                    var g = GUILayoutUtility.GetRect(110, 46, GUILayout.Width(110));
                    FTControls.MappingGraph(g, m, viewIn0, 1, inp);
                }

                // ---- 詳細: ベジエカーブ ----
                if (m.useCurve)
                {
                    bool d = detail.Contains(i);
                    bool nd = EditorGUILayout.Foldout(d, "詳細モード: ベジエカーブ (in 範囲 → out 範囲)", true);
                    if (nd != d) { if (nd) detail.Add(i); else detail.Remove(i); }
                    if (nd)
                    {
                        var cr = GUILayoutUtility.GetRect(10, 200, GUILayout.ExpandWidth(true));
                        float? t = Mathf.Approximately(m.inMin, m.inMax) ? (float?)null : Mathf.Clamp01((inp - m.inMin) / (m.inMax - m.inMin));
                        var curve = m.curve;
                        if (FTControls.BezierEditor(cr, ref curve, mapper, t)) { m.curve = curve; Dirty(); ApplyPreview(); }
                        EditorGUILayout.LabelField($"ビルド時は {mapper.curveSamples} 点で折れ線近似されます (Alt+ドラッグでハンドル分離)", EditorStyles.miniLabel);
                    }
                }
            }
            return false;
        }

        void ParamMenu(FTMapping m)
        {
            var menu = new GenericMenu();
            foreach (var p in FTCatalog.All)
            {
                var pp = p;
                menu.AddItem(new GUIContent($"{p.group}/{p.name}  ({p.label})"), m.parameter == p.name, () =>
                {
                    Undo.RecordObject(mapper, "パラメータ変更");
                    m.parameter = pp.name;
                    m.signed = pp.signed;
                    Dirty(); ApplyPreview();
                });
            }
            foreach (var k in NativeKeys.All)
            {
                var kk = k;
                string grp = new[] { "EyeLook", "Eye", "Brow", "Nasal", "Nose", "Cheek", "Jaw", "Lip", "Mouth", "Tongue" }.FirstOrDefault(pfx => k.StartsWith(pfx)) ?? "Other";
                menu.AddItem(new GUIContent($"ネイティブキー/{grp}/{k}"), m.parameter == k, () =>
                {
                    Undo.RecordObject(mapper, "パラメータ変更");
                    m.parameter = kk;
                    m.signed = false;
                    Dirty(); ApplyPreview();
                });
            }
            menu.AddSeparator("");
            if (!string.IsNullOrEmpty(m.blendShape))
            {
                var guess = AutoSetup.GuessParameter(m.blendShape);
                if (guess != null)
                    menu.AddItem(new GUIContent($"シェイプキー名から推定: {guess}"), false, () =>
                    {
                        Undo.RecordObject(mapper, "パラメータ推定");
                        m.parameter = guess;
                        m.signed = FTCatalog.Find(guess)?.signed ?? false;
                        Dirty(); ApplyPreview();
                    });
            }
            menu.ShowAsContext();
        }

        void ShapeMenu(FTMapping m)
        {
            var menu = new GenericMenu();
            var rs = AutoSetup.FaceRenderers(avatar.gameObject);
            bool multi = rs.Count > 1;
            foreach (var smr in rs)
            {
                var mesh = smr.sharedMesh;
                int count = mesh.blendShapeCount;
                for (int k = 0; k < count; k++)
                {
                    var name = mesh.GetBlendShapeName(k);
                    var s = smr;
                    // 100 個以上は頭文字でサブメニュー化
                    string group = count > 100 ? (char.ToUpperInvariant(name.Length > 0 ? name[0] : '_') + "/") : "";
                    string path = (multi ? smr.name + "/" : "") + group + name.Replace("/", "∕");
                    menu.AddItem(new GUIContent(path), m.renderer == smr && m.blendShape == name, () =>
                    {
                        Undo.RecordObject(mapper, "シェイプキー変更");
                        m.renderer = s; m.blendShape = name;
                        Dirty(); ApplyPreview();
                    });
                }
            }
            foreach (var sp in mapper.splits.Where(x => x.enabled && x.renderer != null && !string.IsNullOrEmpty(x.source)))
            {
                var (ln, rn) = SplitTools.Names(sp.renderer.sharedMesh, sp.source);
                foreach (var nm in new[] { ln, rn })
                {
                    var spr = sp.renderer; var name2 = nm;
                    menu.AddItem(new GUIContent("左右分離/" + nm), m.renderer == spr && m.blendShape == nm, () =>
                    {
                        Undo.RecordObject(mapper, "シェイプキー変更");
                        m.renderer = spr; m.blendShape = name2;
                        Dirty(); ApplyPreview();
                    });
                }
            }
            menu.ShowAsContext();
        }

        void Dirty()
        {
            EditorUtility.SetDirty(mapper);
            PrefabUtility.RecordPrefabInstancePropertyModifications(mapper);
            Repaint();
        }
    }
}
