using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace VRCFTMapper.Editor
{
    /// <summary>
    /// プレイモード中のアバター (Av3Emulator / Gesture Manager / Animator) に、トラッキング有効フラグと
    /// フェイストラッキング入力 (手動・OSC・録画) を送ってテストする。
    /// </summary>
    public partial class VRCFTMapperWindow
    {
        [SerializeField] string playSnapshotJson;   // プレイモード直前の設定 (NDMF がビルド時にコンポーネントを取り除くため)
        [SerializeField] string avatarScenePath;    // プレイモードの切替で参照が外れても探し直せるように
        [SerializeField] bool playSend = true;
        [SerializeField] bool playSendInputs = true;
        VRCFTMapper playModeMapper;                 // スナップショットから復元した一時コンポーネント
        List<PlayModeBridge.Target> playTargets = new List<PlayModeBridge.Target>();
        int playTargetIndex;
        double lastPlaySend, lastPlayScan;
        int playSentCount;
        readonly List<string> playMissing = new List<string>();
        bool showPlayMissing;

        static string PathOf(Transform t) => t == null ? null : (t.parent == null ? t.gameObject.scene.name + ":" + t.name : PathOf(t.parent) + "/" + t.name);

        static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int c = path.IndexOf(':');
            string scene = path.Substring(0, c);
            var parts = path.Substring(c + 1).Split('/');
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var sc = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!sc.isLoaded || sc.name != scene) continue;
                foreach (var root in sc.GetRootGameObjects())
                {
                    if (root.name != parts[0]) continue;
                    var tr = root.transform;
                    for (int k = 1; k < parts.Length && tr != null; k++) tr = tr.Find(parts[k]);
                    if (tr != null) return tr.gameObject;
                }
            }
            return null;
        }

        /// <summary>アバターと設定を (必要なら探し直して) 用意する。どちらも揃えば true。</summary>
        bool ResolveTargets()
        {
            if (avatar == null && !string.IsNullOrEmpty(avatarScenePath))
            {
                var go = FindByPath(avatarScenePath);
                if (go != null) avatar = go.GetComponent<VRCAvatarDescriptor>();
            }
            if (avatar == null) return false;
            avatarScenePath = PathOf(avatar.transform);
            if (mapper == null) mapper = avatar.GetComponentInChildren<VRCFTMapper>(true);
            if (mapper == null && EditorApplication.isPlaying) mapper = PlayModeMapper();
            return mapper != null;
        }

        void OnPlayModeChanged(PlayModeStateChange s)
        {
            switch (s)
            {
                case PlayModeStateChange.ExitingEditMode:
                    if (avatar != null) avatarScenePath = PathOf(avatar.transform);
                    var m = mapper != null ? mapper : (avatar != null ? avatar.GetComponentInChildren<VRCFTMapper>(true) : null);
                    playSnapshotJson = m != null ? EditorJsonUtility.ToJson(m) : null;
                    break;
                case PlayModeStateChange.EnteredPlayMode:
                    mapper = null; playTargets.Clear(); lastPlayScan = 0;
                    preview.Cleanup();
                    break;
                case PlayModeStateChange.ExitingPlayMode:
                    DestroyPlayModeMapper();
                    mapper = null;
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    mapper = null; playTargets.Clear();
                    preview.Cleanup();
                    break;
            }
            Repaint();
        }

        VRCFTMapper PlayModeMapper()
        {
            if (playModeMapper != null) return playModeMapper;
            if (string.IsNullOrEmpty(playSnapshotJson)) return null;
            var go = new GameObject("VRCFT Mapper (play mode snapshot)") { hideFlags = HideFlags.HideAndDontSave };
            playModeMapper = go.AddComponent<VRCFTMapper>();
            EditorJsonUtility.FromJsonOverwrite(playSnapshotJson, playModeMapper);
            return playModeMapper;
        }

        void DestroyPlayModeMapper()
        {
            if (playModeMapper != null) DestroyImmediate(playModeMapper.gameObject);
            playModeMapper = null;
        }

        PlayModeBridge.Target PlayTarget
        {
            get
            {
                if (!EditorApplication.isPlaying) return null;
                double now = EditorApplication.timeSinceStartup;
                if (playTargets.Count == 0 && now - lastPlayScan > 1.0 || playTargets.Any(t => t.avatar == null))
                {
                    lastPlayScan = now;
                    playTargets = PlayModeBridge.FindTargets();
                    // ウィンドウで選んでいるアバターと同じ名前を優先
                    int i = avatar != null ? playTargets.FindIndex(t => t.avatar == avatar || t.avatar.name == avatar.name) : -1;
                    if (i >= 0) playTargetIndex = i;
                }
                return playTargetIndex >= 0 && playTargetIndex < playTargets.Count ? playTargets[playTargetIndex] : null;
            }
        }

        // ---------------- 送信 ----------------
        void SendToPlayMode()
        {
            var t = PlayTarget;
            if (!playSend || t == null) return;
            playSentCount = 0;
            playMissing.Clear();
            void Send(string name, float v)
            {
                if (PlayModeBridge.Set(t, name, v)) playSentCount++;
                else if (!playMissing.Contains(name)) playMissing.Add(name);
            }

            // 有効フラグ: OSC 受信中は受信値、それ以外は Eye / Lip ボタン
            Send(VRCFTMapperPlugin.EyeActive, EyeOn ? 1 : 0);
            Send(VRCFTMapperPlugin.LipTrackingActive, ActiveFlag(VRCFTMapperPlugin.LipTrackingActive, previewLipActive) ? 1 : 0);
            Send(VRCFTMapperPlugin.ExprActive, ActiveFlag(VRCFTMapperPlugin.ExprActive, false) ? 1 : 0);
            if (!playSendInputs) return;

            // 表情パラメータ: 同期される形 (Float / Binary のビット) で送る。平滑化や加工はアバター側のアニメーターが行う
            foreach (var g in mapper.mappings.Where(x => x.enabled && !VRCFTMapper.IsGaze(x.parameter)).GroupBy(x => x.parameter))
            {
                var m = g.First();
                RawInput(m, out float v);
                var full = mapper.FullName(g.Key);
                if (g.All(x => x.type == FTParamType.Float)) { Send(full, Mathf.Clamp(v, -1, 1)); continue; }
                int bits = g.Max(x => x.binaryBits);
                bool signed = g.Any(x => x.signed);
                int steps = (1 << bits) - 1;
                int n = Mathf.RoundToInt(Mathf.Clamp01(Mathf.Abs(signed ? v : Mathf.Max(0, v))) * steps);
                for (int b = 0; b < bits; b++) Send(full + (1 << b), (n >> b) & 1);
                if (signed) Send(full + "Negative", v < 0 ? 1 : 0);
            }

            // 視線: 加工前の入力を送る (寄り目制限・カーブ・揺らぎはアバター側で処理される)
            if (mapper.gazeMode != GazeMode.None)
            {
                EyeNow(out float rawL, out float rawR, out float rawY);
                if (mapper.gazeCombined) Send(mapper.FullName("EyeX"), Mathf.Clamp((rawL + rawR) * 0.5f, -1, 1));
                else
                {
                    Send(mapper.FullName("EyeLeftX"), Mathf.Clamp(rawL, -1, 1));
                    Send(mapper.FullName("EyeRightX"), Mathf.Clamp(rawR, -1, 1));
                }
                Send(mapper.FullName("EyeY"), Mathf.Clamp(rawY, -1, 1));
            }
        }

        // ---------------- UI ----------------
        void DrawPlayModePanel()
        {
            EditorGUILayout.LabelField("プレイモードでテスト", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!EditorApplication.isPlaying)
                {
                    EditorGUILayout.LabelField("プレイモード中に、トラッキングの ON/OFF と入力値をアバターへ送れます (Av3Emulator / Gesture Manager 対応)。", EditorStyles.wordWrappedMiniLabel);
                    if (GUILayout.Button("プレイモードに入る")) EditorApplication.isPlaying = true;
                    return;
                }
                if (playModeMapper != null)
                    EditorGUILayout.LabelField("プレイモード直前の設定を使用中 (ここでの変更は保存されません)", EditorStyles.wordWrappedMiniLabel);

                var target = PlayTarget;
                using (new EditorGUILayout.HorizontalScope())
                {
                    var labels = playTargets.Select(x => x.Label).ToArray();
                    if (labels.Length == 0) EditorGUILayout.LabelField("アバターが見つかりません", EditorStyles.miniLabel);
                    else playTargetIndex = EditorGUILayout.Popup(Mathf.Clamp(playTargetIndex, 0, labels.Length - 1), labels);
                    if (GUILayout.Button("再検索", EditorStyles.miniButton, GUILayout.Width(50))) { playTargets.Clear(); lastPlayScan = 0; }
                }
                playSend = EditorGUILayout.ToggleLeft("このアバターに送る", playSend);
                using (new EditorGUI.DisabledScope(!playSend))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        previewEyeActive = GUILayout.Toggle(previewEyeActive, EyeOn ? "Eye Tracking ON" : "Eye Tracking OFF", EditorStyles.miniButtonLeft);
                        previewLipActive = GUILayout.Toggle(previewLipActive, LipOn ? "Lip Tracking ON" : "Lip Tracking OFF", EditorStyles.miniButtonRight);
                    }
                    if (osc.source != OscInput.Source.Manual)
                        EditorGUILayout.LabelField("OSC で有効フラグを受信している場合はそちらが優先されます", EditorStyles.miniLabel);
                    playSendInputs = EditorGUILayout.ToggleLeft(new GUIContent("入力値も送る (手動 / OSC / 録画)",
                        "OFF にすると有効フラグだけを送ります。Av3Emulator / Gesture Manager の画面で値を動かしたいとき用"), playSendInputs);
                }

                if (target != null)
                {
                    float? e = PlayModeBridge.Get(target, VRCFTMapperPlugin.EyeActive);
                    float? l = PlayModeBridge.Get(target, VRCFTMapperPlugin.LipTrackingActive);
                    string F(float? v) => v.HasValue ? (v.Value > 0.5f ? "ON" : "OFF") : "なし";
                    EditorGUILayout.LabelField($"アバター側: EyeTrackingActive {F(e)} / LipTrackingActive {F(l)}   送信 {playSentCount} 個", EditorStyles.miniLabel);
                    if (playMissing.Count > 0)
                    {
                        EditorGUILayout.HelpBox($"{playMissing.Count} 個のパラメータがアバターにありません。VRCFT Mapper が付いていないか、NDMF の「Apply on Play」が無効の可能性があります。", MessageType.Warning);
                        showPlayMissing = EditorGUILayout.Foldout(showPlayMissing, "見つからないパラメータ", true);
                        if (showPlayMissing) foreach (var n in playMissing.Take(30)) EditorGUILayout.LabelField(n, EditorStyles.miniLabel);
                    }
                }
            }
        }
    }
}
