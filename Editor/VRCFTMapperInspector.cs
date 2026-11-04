using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace VRCFTMapper.Editor
{
    [CustomEditor(typeof(VRCFTMapper))]
    public class VRCFTMapperInspector : UnityEditor.Editor
    {
        static bool showAdvanced;

        static void ShowImportResult(DescriptorFT.ImportResult r)
        {
            var msg = $"マッピング {r.mappings} 件、上限・下限 {r.limits} 件を読み込みました。";
            if (r.duplicates > 0) msg += $"\n重複 {r.duplicates} 件はスキップしました。";
            foreach (var n in r.notes) msg += "\n" + n;
            EditorUtility.DisplayDialog("VRCFT Mapper", msg + "\n\n(Ctrl+Z で元に戻せます)", "OK");
        }

        void DrawExistingFT(VRCFTMapper m, VRCAvatarDescriptor desc)
        {
            if (!DescriptorFT.HasSettings(desc)) return;
            bool merge = m.existingFaceTracking == FTExistingMode.Merge;
            EditorGUILayout.HelpBox(
                "VRC Avatar Descriptor の Face Tracking に設定があります (" + DescriptorFT.Summary(desc) + ")。\n" +
                (merge ? "合成モードのため、ビルド時にこの設定へ VRCFT Mapper の設定が追加されます。"
                       : "ビルド時にこの設定は無視され、VRCFT Mapper の設定で上書きされます。") +
                "\n必要なら VRCFT Mapper に読み込むか、デスクリプタから削除してください。", MessageType.Warning);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("設定を追加する"))
                    ShowImportResult(DescriptorFT.Import(m, desc, false));
                if (GUILayout.Button("設定を上書きする") &&
                    (m.mappings.Count + m.limits.Count == 0 || EditorUtility.DisplayDialog("VRCFT Mapper",
                        $"現在のマッピング {m.mappings.Count} 件と上限・下限 {m.limits.Count} 件を置き換えます。よろしいですか？\n(Ctrl+Z で元に戻せます)", "上書きする", "キャンセル")))
                    ShowImportResult(DescriptorFT.Import(m, desc, true));
            }
            if (GUILayout.Button("VRC Avatar Descriptor の Face Tracking 設定を削除") &&
                EditorUtility.DisplayDialog("Face Tracking 設定の削除",
                    $"{desc.name} の VRC Avatar Descriptor から Face Tracking の設定 (Meshes / Blendshapes Set / Parameter Behaviors など) をすべて削除します。\n" +
                    "Face Tracking が無効 (Disable) でも削除されます。本当に削除しますか？\n(Ctrl+Z で元に戻せます)", "削除する", "キャンセル"))
            {
                DescriptorFT.Clear(desc);
                GUIUtility.ExitGUI();
            }
        }

        public override void OnInspectorGUI()
        {
            var m = (VRCFTMapper)target;
            if (GUILayout.Button("VRCFT Mapper エディタを開く", GUILayout.Height(32))) VRCFTMapperWindow.Open(m);

            var desc = m.GetComponentInParent<VRCAvatarDescriptor>();
            if (desc == null) EditorGUILayout.HelpBox("アバター (VRC Avatar Descriptor) の子に配置してください。", MessageType.Warning);
            else
            {
                var b = ParamBudget.Compute(desc, m);
                EditorGUILayout.HelpBox($"マッピング {m.mappings.Count} 件 / FT 同期 {b.ours} bit / 合計 {b.total}/{b.limit} bit",
                    b.total > b.limit ? MessageType.Error : MessageType.Info);
                DrawExistingFT(m, desc);
            }

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("prefix"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("curveSamples"));
            showAdvanced = EditorGUILayout.Foldout(showAdvanced, "上級者向け設定", true);
            if (showAdvanced)
            {
                EditorGUI.indentLevel++;
                var ex = serializedObject.FindProperty("existingFaceTracking");
                ex.enumValueIndex = EditorGUILayout.Popup(new GUIContent("既存の Face Tracking 設定", "ビルド時、VRC Avatar Descriptor に既にある Face Tracking 設定の扱い"),
                    ex.enumValueIndex, new[] { new GUIContent("上書き (既定)"), new GUIContent("合成") });
                if (ex.enumValueIndex == (int)FTExistingMode.Merge)
                    EditorGUILayout.HelpBox("合成: 既存の Face Tracking 設定に VRCFT Mapper の設定を追加します。同じシェイプキーが複数の設定から動かされるなど、予期しない動作が起きる場合があります。", MessageType.Warning);
                EditorGUI.indentLevel--;
            }
            serializedObject.ApplyModifiedProperties(); // SerializedObject 経由なので Undo 対応
            EditorGUILayout.HelpBox("ビルド時 (NDMF) に FX レイヤーと MA Parameters を生成します。アバター本体は変更されません。", MessageType.None);
        }
    }
}
