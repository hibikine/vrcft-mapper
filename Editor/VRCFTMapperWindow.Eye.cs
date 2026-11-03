using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRCFTMapper.Editor
{
    /// <summary>「目・視線」タブ。設定値を変えながら、顔プレビューで実際の見え方を確認する。</summary>
    public partial class VRCFTMapperWindow
    {
        static readonly Color PadBg = new Color(0.11f, 0.11f, 0.13f);
        static readonly Color RawCol = new Color(1, 1, 1, 0.55f);

        void DrawEyeTab()
        {
            var e = mapper.eye;
            var so = new SerializedObject(mapper);
            so.Update();
            SerializedProperty P(string n) => so.FindProperty(n);
            SerializedProperty E(string n) => so.FindProperty("eye." + n);

            using (var sv = new EditorGUILayout.ScrollViewScope(eyeScroll))
            {
                eyeScroll = sv.scrollPosition;
                EditorGUI.BeginChangeCheck();

                // ---- 方式 ----
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PropertyField(P("gazeMode"), new GUIContent("視線の方式"));
                    if (mapper.gazeMode != GazeMode.None)
                        EditorGUILayout.PropertyField(P("gazeCombined"), new GUIContent("両目共通", "ON: EyeX/EyeY (16bit) / OFF: 左右別 X + 共通 Y (24bit)。寄り目は左右別のときだけ発生"));
                }
                if (mapper.gazeMode == GazeMode.None)
                {
                    EditorGUI.EndChangeCheck();
                    ApplyEye(so);
                    EditorGUILayout.HelpBox("視線の方式を「Bone (目ボーン)」または「BlendShape (eyeLook 系シェイプキー)」にすると、目周りの調整が使えます。\nまぶた追従・揺らぎも視線入力を使うため、方式が「なし」のときは無効です。", MessageType.Info);
                    return;
                }
                EditorGUILayout.PropertyField(P("gazeSmoothing"), new GUIContent("視線スムージング (k)", "小さいほど滑らか"));
                var gp = mapper.GazeParams().ToList();
                EditorGUILayout.LabelField($"同期: {string.Join(", ", gp)} (Float × {gp.Count} = {gp.Count * 8} bit)", EditorStyles.miniLabel);
                if (mapper.gazeMode == GazeMode.Bone && !preview.HasEyeBones)
                    EditorGUILayout.HelpBox("Humanoid の目ボーンが見つかりません。", MessageType.Warning);
                if (mapper.gazeMode == GazeMode.BlendShape && !mapper.mappings.Any(m => VRCFTMapper.IsGaze(m.parameter)))
                    EditorGUILayout.HelpBox("eyeLook 系のマッピングがありません。表情マッピングタブで「自動設定」するか、EyeX / EyeY に手動で割り当ててください。", MessageType.Warning);

                // ---- テスト & 可視化 ----
                var res = EyeNow(out float rawL, out float rawR, out float rawY);
                using (new EditorGUILayout.HorizontalScope())
                {
                    var pad = GUILayoutUtility.GetRect(190, 190, GUILayout.Width(190), GUILayout.Height(190));
                    DrawGazePad(pad, rawL, rawR, rawY, res);
                    using (new EditorGUILayout.VerticalScope())
                        DrawEyeTestControls(rawL, rawR, rawY, res);
                }

                // ---- 視線調整 ----
                showEyeAdjust = EditorGUILayout.Foldout(showEyeAdjust, "視線調整 (オフセット / 倍率 / 角度)", true, EditorStyles.foldoutHeader);
                if (showEyeAdjust)
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.PropertyField(E("offsetX"), new GUIContent("オフセット X"));
                            EditorGUILayout.PropertyField(E("offsetY"), new GUIContent("Y"));
                        }
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.PropertyField(E("gainX"), new GUIContent("倍率 X", "変化量スケーリング。1 より大きいと小さな目の動きでも大きく動く"));
                            EditorGUILayout.PropertyField(E("gainY"), new GUIContent("Y"));
                        }
                        EditorGUILayout.PropertyField(E("deadzone"), new GUIContent("デッドゾーン"));
                        if (GUILayout.Button(new GUIContent("今の視線を正面にする", "現在の入力 (OSC / 手動) を打ち消すようにオフセットを設定")))
                        {
                            E("offsetX").floatValue = -(rawL + rawR) * 0.5f;
                            E("offsetY").floatValue = -rawY;
                        }
                        if (mapper.gazeMode == GazeMode.Bone)
                        {
                            EditorGUILayout.LabelField("目ボーンの最大角度 (入力 ±1 のとき)", EditorStyles.miniBoldLabel);
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                EditorGUILayout.PropertyField(P("gazeYawIn"), new GUIContent("内側°"));
                                EditorGUILayout.PropertyField(P("gazeYawOut"), new GUIContent("外側°"));
                            }
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                EditorGUILayout.PropertyField(P("gazeUp"), new GUIContent("上°"));
                                EditorGUILayout.PropertyField(P("gazeDown"), new GUIContent("下°"));
                            }
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                EditorGUILayout.PropertyField(P("gazeInvertX"), new GUIContent("X 反転"));
                                EditorGUILayout.PropertyField(P("gazeInvertY"), new GUIContent("Y 反転"));
                            }
                        }
                    }

                // ---- カーブ ----
                showEyeCurve = EditorGUILayout.Foldout(showEyeCurve, "視線変化カーブ", true, EditorStyles.foldoutHeader);
                if (showEyeCurve)
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        EditorGUILayout.PropertyField(E("useCurve"), new GUIContent("カーブを使う", "|入力| 0..1 → 0..1 の対応を曲線で変える (符号は保持)"));
                        var gr = GUILayoutUtility.GetRect(10, 90, GUILayout.ExpandWidth(true));
                        Plot(gr, x => EyeMath.Shape(e, x, false), -1, 1, -1.1f, 1.1f, (rawL + rawR) * 0.5f,
                            "X: 入力 → 整形後 (オフセット・倍率・デッドゾーン・カーブ込み)");
                        if (e.useCurve)
                        {
                            ApplyEye(so); // カーブは Undo.RecordObject で直接編集するので先に確定
                            var cr = GUILayoutUtility.GetRect(10, 170, GUILayout.ExpandWidth(true));
                            var curve = e.curve;
                            float t = Mathf.Clamp01(Mathf.Abs((rawL + rawR) * 0.5f));
                            if (FTControls.BezierEditor(cr, ref curve, mapper, t)) { e.curve = curve; Dirty(); ApplyPreview(); }
                            so.Update();
                            EditorGUILayout.LabelField("例: 中央付近を緩やかに → イーズイン / 端まで素早く → イーズアウト (右クリックでプリセット)", EditorStyles.miniLabel);
                        }
                    }

                // ---- 寄り目 ----
                showEyeVerge = EditorGUILayout.Foldout(showEyeVerge, "寄り目 (輻輳) 制御", true, EditorStyles.foldoutHeader);
                if (showEyeVerge)
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        if (mapper.gazeCombined)
                            EditorGUILayout.HelpBox("両目共通 (EyeX) では左右の目が常に同じ向きなので寄り目は起きません。近くを見たときの自然な寄りも出ません。", MessageType.None);
                        using (new EditorGUI.DisabledScope(mapper.gazeCombined))
                        {
                            EditorGUILayout.PropertyField(E("vergenceScale"), new GUIContent("反映率", "左右差をどれだけ反映するか。0 = 常に平行"));
                            EditorGUILayout.PropertyField(E("maxConverge"), new GUIContent("内寄せ上限", "片目あたり (入力単位)。デフォルメ顔は 0.1〜0.2 程度がおすすめ"));
                            EditorGUILayout.PropertyField(E("maxDiverge"), new GUIContent("外開き上限"));
                            var gr = GUILayoutUtility.GetRect(10, 80, GUILayout.ExpandWidth(true));
                            Plot(gr, v => EyeMath.SoftVerge(e, v), -1, 1, -1, 1, (EyeMath.Shape(e, rawL, false) - EyeMath.Shape(e, rawR, false)) * 0.5f,
                                "左右差 v (+ = 寄り目) → 制限後");
                            float deg = e.maxConverge * mapper.gazeYawIn;
                            float ipd = preview.EyeDistance;
                            float dist = deg > 0.01f ? ipd * 0.5f / Mathf.Tan(deg * Mathf.Deg2Rad) : float.PositiveInfinity;
                            EditorGUILayout.LabelField($"内寄せ上限 ≈ {deg:0.0}°/片目 (目の間隔 {ipd * 100:0.0}cm → 約 {dist * 100:0} cm より近くは寄らない)", EditorStyles.miniLabel);
                            if (GUILayout.Button("デフォルメ向けプリセット (反映 0.6 / 内寄せ 0.15 / 外開き 0.02)"))
                            {
                                E("vergenceScale").floatValue = 0.6f; E("maxConverge").floatValue = 0.15f; E("maxDiverge").floatValue = 0.02f;
                            }
                        }
                    }

                // ---- まぶた追従 ----
                showEyeLid = EditorGUILayout.Foldout(showEyeLid, "まぶた追従 (視線に合わせて上まぶたが動く)", true, EditorStyles.foldoutHeader);
                if (showEyeLid)
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        EditorGUILayout.PropertyField(E("lidFollowDown"), new GUIContent("下を見る → まぶたを下げる"));
                        EditorGUILayout.PropertyField(E("lidFollowUp"), new GUIContent("上を見る → 見開く"));
                        EditorGUILayout.PropertyField(E("lidRenderer"), new GUIContent("メッシュ"));
                        ApplyEye(so);
                        LidShapeRow("下げる (左 / 右)", nameof(EyeSettings.lidDownLeft), nameof(EyeSettings.lidDownRight));
                        LidShapeRow("見開く (左 / 右)", nameof(EyeSettings.lidUpLeft), nameof(EyeSettings.lidUpRight));
                        if (GUILayout.Button("マッピングから自動検出 (EyeLid の閉じ / 見開きシェイプキー)"))
                        {
                            Undo.RecordObject(mapper, "まぶた追従シェイプの自動検出");
                            EyeMath.AutoDetectLids(mapper);
                            Dirty(); ApplyPreview();
                        }
                        so.Update();
                        EditorGUILayout.LabelField("まばたきと加算されます。目を閉じているほど追従は弱まります (EyeLid 入力がある場合)。", EditorStyles.miniLabel);
                    }

                // ---- 揺らぎ ----
                showEyeJitter = EditorGUILayout.Foldout(showEyeJitter, "自然な揺らぎ (トラッキング中に加算)", true, EditorStyles.foldoutHeader);
                if (showEyeJitter)
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        EditorGUILayout.PropertyField(E("tremor"), new GUIContent("微細な震え", "固視微動。0.005〜0.015 程度"));
                        EditorGUILayout.PropertyField(E("saccade"), new GUIContent("小さな跳び", "マイクロサッカード。0.02〜0.05 程度"));
                        EditorGUILayout.PropertyField(E("saccadeInterval"), new GUIContent("跳びの間隔 (秒)"));
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.PropertyField(E("seed"), new GUIContent("シード"));
                            if (GUILayout.Button("別パターン", GUILayout.Width(80))) E("seed").intValue = UnityEngine.Random.Range(1, 99999);
                        }
                        var jr = GUILayoutUtility.GetRect(10, 70, GUILayout.ExpandWidth(true));
                        DrawJitterTrack(jr, e);
                        EditorGUILayout.LabelField($"30 秒ループのアニメーションとしてビルドされます (全員の画面で同じ揺れ方)", EditorStyles.miniLabel);
                    }

                ApplyEye(so);
                if (EditorGUI.EndChangeCheck()) ApplyPreview();
            }
        }

        void ApplyEye(SerializedObject so)
        {
            if (so.ApplyModifiedProperties()) { Dirty(); ApplyPreview(); }
        }

        // ---------------- テスト入力 ----------------
        void DrawEyeTestControls(float rawL, float rawR, float rawY, EyeMath.Result res)
        {
            bool manualSrc = osc.source == OscInput.Source.Manual;
            EditorGUILayout.LabelField(manualSrc ? "テスト入力 (パッドをドラッグでも可)" : "OSC / 録画の入力を表示中", EditorStyles.miniBoldLabel);
            using (new EditorGUI.DisabledScope(!manualSrc))
            {
                EditorGUI.BeginChangeCheck();
                manualGaze.TryGetValue("EyeX", out var mx);
                manualGaze.TryGetValue("EyeY", out var my);
                mx = EditorGUILayout.Slider("X (右 +)", mx, -1, 1);
                my = EditorGUILayout.Slider("Y (上 +)", my, -1, 1);
                if (!mapper.gazeCombined)
                {
                    using (new EditorGUI.DisabledScope(gazeDistance > 0))
                        manualVerge = EditorGUILayout.Slider(new GUIContent("寄せ (片目)", "左右の目を内側へ寄せる量 (入力単位)"), manualVerge, -0.3f, 1f);
                    gazeDistance = EditorGUILayout.Slider(new GUIContent("注視距離 (m)", "0 = 使わない。近くを見たときの寄り目を再現"), gazeDistance, 0, 1.5f);
                }
                if (EditorGUI.EndChangeCheck()) { manualGaze["EyeX"] = mx; manualGaze["EyeY"] = my; ApplyPreview(); }
                if (GUILayout.Button("正面に戻す", EditorStyles.miniButton)) { manualGaze["EyeX"] = 0; manualGaze["EyeY"] = 0; manualVerge = 0; gazeDistance = 0; ApplyPreview(); }
            }

            EditorGUI.BeginChangeCheck();
            jitterPreview = EditorGUILayout.ToggleLeft("揺らぎをプレビュー", jitterPreview);
            if (EditorGUI.EndChangeCheck()) ApplyPreview();
            if (!EyeOn) EditorGUILayout.HelpBox("Eye トラッキングが OFF のため目は動きません (左パネルの Eye ボタン)。", MessageType.None);

            var mini = EditorStyles.miniLabel;
            EditorGUILayout.LabelField($"入力   L {rawL:+0.00;-0.00}  R {rawR:+0.00;-0.00}  Y {rawY:+0.00;-0.00}", mini);
            EditorGUILayout.LabelField($"出力   L {res.left:+0.00;-0.00}  R {res.right:+0.00;-0.00}  Y {res.y:+0.00;-0.00}", mini);
            if (mapper.gazeMode == GazeMode.Bone)
            {
                float c(float v) => Mathf.Clamp(v, -1, 1);
                float yl = GazeTools.YawAngle(mapper, true, c(res.left)), yr = GazeTools.YawAngle(mapper, false, c(res.right));
                EditorGUILayout.LabelField($"角度   左目 {(yl >= 0 ? "外" : "内")}{Mathf.Abs(yl):0.0}°  右目 {(yr >= 0 ? "外" : "内")}{Mathf.Abs(yr):0.0}°  上下 {GazeTools.PitchAngle(mapper, c(res.y)):+0.0;-0.0}°", mini);
            }
        }

        // ---------------- 視線パッド ----------------
        void DrawGazePad(Rect r, float rawL, float rawR, float rawY, EyeMath.Result res)
        {
            const float range = 1.25f;
            Vector2 ToG(float x, float y) => new Vector2(r.center.x + x / range * r.width * 0.5f, r.center.y - y / range * r.height * 0.5f);
            var ev = Event.current;
            int id = GUIUtility.GetControlID(FocusType.Passive);
            bool manualSrc = osc.source == OscInput.Source.Manual;

            if (manualSrc)
            {
                switch (ev.GetTypeForControl(id))
                {
                    case EventType.MouseDown when ev.button == 0 && r.Contains(ev.mousePosition):
                        GUIUtility.hotControl = id; SetPad(); ev.Use(); break;
                    case EventType.MouseDrag when GUIUtility.hotControl == id:
                        SetPad(); ev.Use(); break;
                    case EventType.MouseUp when GUIUtility.hotControl == id:
                        GUIUtility.hotControl = 0; ev.Use(); break;
                }
            }
            void SetPad()
            {
                float x = (ev.mousePosition.x - r.center.x) / (r.width * 0.5f) * range;
                float y = -(ev.mousePosition.y - r.center.y) / (r.height * 0.5f) * range;
                if (ev.shift) { x = Mathf.Round(x * 10) / 10; y = Mathf.Round(y * 10) / 10; }
                manualGaze["EyeX"] = Mathf.Clamp(x, -1, 1);
                manualGaze["EyeY"] = Mathf.Clamp(y, -1, 1);
                ApplyPreview();
                Repaint();
            }

            if (ev.type != EventType.Repaint) return;
            EditorGUI.DrawRect(r, PadBg);
            Handles.BeginGUI();
            Handles.color = new Color(1, 1, 1, 0.08f);
            for (int i = -4; i <= 4; i++)
            {
                Handles.DrawLine(ToG(i / 4f, -range), ToG(i / 4f, range));
                Handles.DrawLine(ToG(-range, i / 4f), ToG(range, i / 4f));
            }
            Handles.color = new Color(1, 1, 1, 0.3f);
            Handles.DrawWireDisc(ToG(0, 0), Vector3.forward, r.width * 0.5f / range);
            Handles.DrawLine(ToG(-1, -1), ToG(1, -1)); Handles.DrawLine(ToG(1, -1), ToG(1, 1));
            Handles.DrawLine(ToG(1, 1), ToG(-1, 1)); Handles.DrawLine(ToG(-1, 1), ToG(-1, -1));

            // 入力 (白) → 出力 (青 = 左目 / 赤 = 右目)
            var raw = ToG((rawL + rawR) * 0.5f, rawY);
            Handles.color = RawCol;
            Handles.DrawWireDisc(raw, Vector3.forward, 6);
            var pl = ToG(res.left, res.y); var pr = ToG(res.right, res.y);
            Handles.color = new Color(1, 1, 1, 0.25f);
            Handles.DrawDottedLine(raw, (pl + pr) * 0.5f, 2);
            Handles.color = FTControls.Blue; Handles.DrawSolidDisc(pl, Vector3.forward, 4.5f);
            Handles.color = FTControls.Red; Handles.DrawSolidDisc(pr, Vector3.forward, 4.5f);
            Handles.EndGUI();
            GUI.Label(new Rect(r.x + 4, r.y + 2, r.width, 14), "○入力  ●左目  ●右目", EditorStyles.miniLabel);
            GUI.Label(new Rect(r.x + 4, r.yMax - 14, r.width, 14), "Shift で 0.1 スナップ", EditorStyles.centeredGreyMiniLabel);
        }

        // ---------------- グラフ ----------------
        static void Plot(Rect r, Func<float, float> f, float x0, float x1, float y0, float y1, float? marker, string label)
        {
            if (Event.current.type != EventType.Repaint) return;
            EditorGUI.DrawRect(r, PadBg);
            Vector3 G(float x, float y) => new Vector3(Mathf.Lerp(r.x + 4, r.xMax - 4, Mathf.InverseLerp(x0, x1, x)),
                                                       Mathf.Lerp(r.yMax - 4, r.y + 4, Mathf.InverseLerp(y0, y1, y)));
            Handles.BeginGUI();
            Handles.color = new Color(1, 1, 1, 0.15f);
            Handles.DrawLine(G(x0, 0), G(x1, 0)); Handles.DrawLine(G(0, y0), G(0, y1));
            Handles.color = new Color(1, 1, 1, 0.08f);
            Handles.DrawDottedLine(G(x0, x0), G(x1, x1), 3);
            var pts = new Vector3[96];
            for (int i = 0; i < pts.Length; i++) { float x = Mathf.Lerp(x0, x1, i / (pts.Length - 1f)); pts[i] = G(x, Mathf.Clamp(f(x), y0, y1)); }
            Handles.color = new Color(0.7f, 0.6f, 1f);
            Handles.DrawAAPolyLine(2f, pts);
            if (marker.HasValue)
            {
                float mx = Mathf.Clamp(marker.Value, x0, x1);
                Handles.color = FTControls.Marker;
                Handles.DrawSolidDisc(G(mx, Mathf.Clamp(f(mx), y0, y1)), Vector3.forward, 3.5f);
            }
            Handles.EndGUI();
            GUI.Label(new Rect(r.x + 4, r.y + 2, r.width - 8, 14), label, EditorStyles.miniLabel);
        }

        static void DrawJitterTrack(Rect r, EyeSettings e)
        {
            if (Event.current.type != EventType.Repaint) return;
            EditorGUI.DrawRect(r, PadBg);
            float amp = Mathf.Max(0.005f, (e.tremor + e.saccade) * 1.4f);
            float now = (float)EditorApplication.timeSinceStartup;
            const float span = 6f;
            Vector3 G(float t, float v) => new Vector3(Mathf.Lerp(r.x + 4, r.xMax - 4, t / span), Mathf.Lerp(r.yMax - 4, r.y + 4, Mathf.InverseLerp(-amp, amp, v)));
            var px = new Vector3[180]; var py = new Vector3[180];
            for (int i = 0; i < px.Length; i++)
            {
                float t = i / (px.Length - 1f) * span;
                var j = EyeMath.Jitter(e, now - span + t);
                px[i] = G(t, j.x); py[i] = G(t, j.y);
            }
            Handles.BeginGUI();
            Handles.color = new Color(1, 1, 1, 0.15f); Handles.DrawLine(G(0, 0), G(span, 0));
            Handles.color = FTControls.Blue; Handles.DrawAAPolyLine(1.5f, px);
            Handles.color = FTControls.Red; Handles.DrawAAPolyLine(1.5f, py);
            Handles.EndGUI();
            GUI.Label(new Rect(r.x + 4, r.y + 2, r.width, 14), $"直近 {span:0} 秒  青 = X / 赤 = Y  (縦軸 ±{amp:0.000})", EditorStyles.miniLabel);
        }

        // ---------------- まぶたシェイプ選択 ----------------
        void LidShapeRow(string label, string leftField, string rightField)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(label);
                LidShapeButton(leftField);
                LidShapeButton(rightField);
            }
        }

        void LidShapeButton(string field)
        {
            var e = mapper.eye;
            var fi = typeof(EyeSettings).GetField(field);
            var cur = (string)fi.GetValue(e);
            if (!EditorGUILayout.DropdownButton(new GUIContent(string.IsNullOrEmpty(cur) ? "(なし)" : cur), FocusType.Keyboard)) return;
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("(なし)"), string.IsNullOrEmpty(cur), () => SetLid(fi, ""));
            var smr = e.lidRenderer != null ? e.lidRenderer : AutoSetup.FaceRenderers(avatar.gameObject).FirstOrDefault();
            if (smr != null && smr.sharedMesh != null)
            {
                int count = smr.sharedMesh.blendShapeCount;
                for (int k = 0; k < count; k++)
                {
                    var n = smr.sharedMesh.GetBlendShapeName(k);
                    string group = count > 100 ? (char.ToUpperInvariant(n.Length > 0 ? n[0] : '_') + "/") : "";
                    var s = smr;
                    menu.AddItem(new GUIContent(group + n.Replace("/", "∕")), cur == n, () =>
                    {
                        if (e.lidRenderer == null) { Undo.RecordObject(mapper, "まぶた追従メッシュ"); e.lidRenderer = s; }
                        SetLid(fi, n);
                    });
                }
            }
            menu.ShowAsContext();
        }

        void SetLid(System.Reflection.FieldInfo fi, string v)
        {
            Undo.RecordObject(mapper, "まぶた追従シェイプ変更");
            fi.SetValue(mapper.eye, v);
            Dirty(); ApplyPreview();
        }
    }
}
