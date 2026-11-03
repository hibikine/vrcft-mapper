using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace VRCFTMapper.Editor
{
    [CustomEditor(typeof(VRCFTMapper))]
    public class VRCFTMapperInspector : UnityEditor.Editor
    {
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
            }

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("prefix"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("curveSamples"));
            serializedObject.ApplyModifiedProperties(); // SerializedObject 経由なので Undo 対応
            EditorGUILayout.HelpBox("ビルド時 (NDMF) に FX レイヤーと MA Parameters を生成します。アバター本体は変更されません。", MessageType.None);
        }
    }
}
