using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRCFTMapper.Editor
{
    /// <summary>「左右分離シェイプキー」と「上限・下限」の編集欄。</summary>
    public partial class VRCFTMapperWindow
    {
        bool showSplits = true, showLimits = true, showStyle = true;
        int previewStyle;          // 0 = 自動, k+1 = 選択肢 k
        float previewStyleAuto;    // 自動のときの式の値 (プレビュー用)

        /// <summary>プレビューでのスタイルの重み (ビルド後のアニメーターと同じ 1D 補間)。</summary>
        float StyleWeightPreview(FTMapping m)
        {
            var g = mapper.styleGroup;
            if (!g.enabled || m.style <= 0) return 1;
            int opt = m.style - 1;
            if (previewStyle > 0 || !g.auto) return opt == Mathf.Max(0, previewStyle - 1) ? 1 : 0;
            var pts = Enumerable.Range(0, g.options.Count).Where(k => k < g.autoValues.Count && !float.IsNaN(g.autoValues[k])).OrderBy(k => g.autoValues[k]).ToList();
            if (pts.Count == 0) return 0;
            float x = previewStyleAuto;
            if (x <= g.autoValues[pts[0]]) return opt == pts[0] ? 1 : 0;
            if (x >= g.autoValues[pts[pts.Count - 1]]) return opt == pts[pts.Count - 1] ? 1 : 0;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                float a = g.autoValues[pts[i]], b = g.autoValues[pts[i + 1]];
                if (x < a || x > b) continue;
                float t = Mathf.InverseLerp(a, b, x);
                return opt == pts[i] ? 1 - t : opt == pts[i + 1] ? t : 0;
            }
            return 0;
        }

        void DrawStyleGroup()
        {
            var g = mapper.styleGroup;
            showStyle = EditorGUILayout.Foldout(showStyle, "スタイル切替 (メニューで選択)", true, EditorStyles.foldoutHeader);
            if (!showStyle) return;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("マッピングを選択肢ごとにまとめ、メニューで切り替えます (アニメーター経由。入力の転送が増えます)。各マッピングの「スタイル」で所属を指定します。", EditorStyles.wordWrappedMiniLabel);
                var so = new SerializedObject(mapper);
                so.Update();
                var sp = so.FindProperty("styleGroup");
                EditorGUILayout.PropertyField(sp.FindPropertyRelative("enabled"), new GUIContent("有効"));
                if (g.enabled)
                {
                    EditorGUILayout.PropertyField(sp.FindPropertyRelative("name"), new GUIContent("メニュー名"));
                    EditorGUILayout.PropertyField(sp.FindPropertyRelative("transition"), new GUIContent("切替時間 (秒)"));
                    EditorGUILayout.PropertyField(sp.FindPropertyRelative("auto"), new GUIContent("「自動」を付ける"));
                    if (g.auto) EditorGUILayout.PropertyField(sp.FindPropertyRelative("autoExpression"), new GUIContent("自動の式 (-1..1)"));
                }
                if (so.ApplyModifiedProperties()) { Dirty(); ApplyPreview(); }
                if (!g.enabled) return;

                for (int i = 0; i < g.options.Count; i++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUI.BeginChangeCheck();
                        string name = EditorGUILayout.DelayedTextField(g.options[i], GUILayout.Width(120));
                        while (g.autoValues.Count < g.options.Count) g.autoValues.Add(0);
                        float av = g.autoValues[i];
                        if (g.auto) { GUILayout.Label("自動の値", GUILayout.Width(52)); av = EditorGUILayout.FloatField(av, GUILayout.Width(50)); }
                        GUILayout.Label($"{mapper.mappings.Count(x => x.style == i + 1)} 件", EditorStyles.miniLabel, GUILayout.Width(40));
                        if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(mapper, "スタイルの変更"); g.options[i] = name; g.autoValues[i] = av; Dirty(); ApplyPreview(); }
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22)))
                        {
                            Undo.RecordObject(mapper, "スタイルの削除");
                            g.options.RemoveAt(i); if (i < g.autoValues.Count) g.autoValues.RemoveAt(i);
                            foreach (var m in mapper.mappings) { if (m.style == i + 1) m.style = 0; else if (m.style > i + 1) m.style--; }
                            Dirty(); ApplyPreview(); break;
                        }
                    }
                }
                if (GUILayout.Button("＋ 選択肢を追加", EditorStyles.miniButton))
                {
                    Undo.RecordObject(mapper, "スタイルの追加");
                    g.options.Add("選択肢" + (g.options.Count + 1)); g.autoValues.Add(0);
                    Dirty();
                }

                // プレビューでの選択
                EditorGUI.BeginChangeCheck();
                var labels = (g.auto ? new[] { "自動" } : new string[0]).Concat(g.options).ToArray();
                int idx = EditorGUILayout.Popup("プレビュー", g.auto ? previewStyle : Mathf.Max(0, previewStyle - 1), labels);
                previewStyle = g.auto ? idx : idx + 1;
                if (g.auto && previewStyle == 0) previewStyleAuto = EditorGUILayout.Slider("自動の式の値", previewStyleAuto, -1, 1);
                if (EditorGUI.EndChangeCheck()) ApplyPreview();
            }
        }

        void DrawSplits()
        {
            showSplits = EditorGUILayout.Foldout(showSplits, $"左右分離シェイプキー ({mapper.splits.Count})", true, EditorStyles.foldoutHeader);
            if (!showSplits) return;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("左右共通のシェイプキーを、ビルド時に左右別のシェイプキーへ分けます (両目ボーンの中点が境界)。分けたシェイプキーはシェイプキー選択の「左右分離」から選べます。", EditorStyles.wordWrappedMiniLabel);
                for (int i = 0; i < mapper.splits.Count; i++)
                {
                    var sp = mapper.splits[i];
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUI.BeginChangeCheck();
                        bool en = EditorGUILayout.Toggle(sp.enabled, GUILayout.Width(16));
                        var r = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(sp.renderer, typeof(SkinnedMeshRenderer), true, GUILayout.Width(120));
                        if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(mapper, "左右分離の変更"); sp.enabled = en; sp.renderer = r; Dirty(); }

                        if (EditorGUILayout.DropdownButton(new GUIContent(string.IsNullOrEmpty(sp.source) ? "(シェイプキー)" : sp.source), FocusType.Keyboard, GUILayout.MinWidth(110)))
                        {
                            var menu = new GenericMenu();
                            var mesh = sp.renderer != null ? sp.renderer.sharedMesh : null;
                            if (mesh != null)
                                for (int k = 0; k < mesh.blendShapeCount; k++)
                                {
                                    var n = mesh.GetBlendShapeName(k);
                                    if (n.EndsWith("_left") || n.EndsWith("_right") || n.EndsWith("_L") || n.EndsWith("_R")) continue;
                                    var cap = sp;
                                    string group = mesh.blendShapeCount > 100 ? (char.ToUpperInvariant(n.Length > 0 ? n[0] : '_') + "/") : "";
                                    menu.AddItem(new GUIContent(group + n.Replace("/", "∕")), n == sp.source, () =>
                                    {
                                        Undo.RecordObject(mapper, "左右分離の変更"); cap.source = n; Dirty(); ApplyPreview();
                                    });
                                }
                            menu.ShowAsContext();
                        }

                        EditorGUI.BeginChangeCheck();
                        GUILayout.Label("幅", GUILayout.Width(16));
                        float band = EditorGUILayout.Slider(sp.band, 0.01f, 1f, GUILayout.Width(130));
                        if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(mapper, "左右分離の幅"); sp.band = band; Dirty(); ApplyPreview(); }

                        if (sp.renderer != null && !string.IsNullOrEmpty(sp.source))
                        {
                            var (ln, rn) = SplitTools.Names(sp.renderer.sharedMesh, sp.source);
                            GUILayout.Label($"→ {ln} / {rn}", EditorStyles.miniLabel);
                        }
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22)))
                        {
                            Undo.RecordObject(mapper, "左右分離の削除"); mapper.splits.RemoveAt(i); Dirty(); ApplyPreview(); break;
                        }
                    }
                }
                if (GUILayout.Button("＋ 左右分離を追加", EditorStyles.miniButton))
                {
                    Undo.RecordObject(mapper, "左右分離の追加");
                    mapper.splits.Add(new FTSplit { renderer = AutoSetup.FaceRenderers(avatar.gameObject).FirstOrDefault() });
                    Dirty();
                }
            }
        }

        void DrawLimits()
        {
            showLimits = EditorGUILayout.Foldout(showLimits, $"上限・下限 ({mapper.limits.Count})", true, EditorStyles.foldoutHeader);
            if (!showLimits) return;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("シェイプキーの最終値を式で制限します (ネイティブ出力の Limit Blendshape)。例: mouth_A の上限 = 1 - TongueOut", EditorStyles.wordWrappedMiniLabel);
                if (mapper.outputMode == FTOutputMode.Legacy)
                    EditorGUILayout.HelpBox("従来出力では使えません。", MessageType.None);
                for (int i = 0; i < mapper.limits.Count; i++)
                {
                    var l = mapper.limits[i];
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUI.BeginChangeCheck();
                        bool en = EditorGUILayout.Toggle(l.enabled, GUILayout.Width(16));
                        string shape = EditorGUILayout.DelayedTextField(l.blendShape ?? "", GUILayout.Width(150));
                        int mode = EditorGUILayout.Popup(l.isMin ? 1 : 0, new[] { "上限", "下限" }, GUILayout.Width(52));
                        GUILayout.Label("=", GUILayout.Width(12));
                        string ex = EditorGUILayout.DelayedTextField(l.expression ?? "");
                        if (EditorGUI.EndChangeCheck())
                        {
                            Undo.RecordObject(mapper, "上限・下限の変更");
                            l.enabled = en; l.blendShape = shape; l.isMin = mode == 1; l.expression = ex;
                            Dirty();
                        }
                        if (nativePlan != null && nativePlan.limitStatus.TryGetValue(l, out var st) && (st.error || st.warning))
                            GUILayout.Label(new GUIContent("!", st.text), EditorStyles.boldLabel, GUILayout.Width(12));
                        if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22)))
                        {
                            Undo.RecordObject(mapper, "上限・下限の削除"); mapper.limits.RemoveAt(i); Dirty(); break;
                        }
                    }
                }
                if (GUILayout.Button("＋ 上限・下限を追加", EditorStyles.miniButton))
                {
                    Undo.RecordObject(mapper, "上限・下限の追加");
                    mapper.limits.Add(new FTLimit());
                    Dirty();
                }
            }
        }
    }
}
