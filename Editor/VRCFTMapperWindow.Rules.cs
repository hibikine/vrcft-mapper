using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRCFTMapper.Editor
{
    /// <summary>「左右分離シェイプキー」と「上限・下限」の編集欄。</summary>
    public partial class VRCFTMapperWindow
    {
        bool showSplits = true, showLimits = true;

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
