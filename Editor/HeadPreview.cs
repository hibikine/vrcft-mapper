using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace VRCFTMapper.Editor
{
    /// <summary>
    /// アバターを複製し、Head ボーン基準でカメラを構えて顔だけを描画するプレビュー。
    /// 複製に対してシェイプキーを適用するので元アバターは変更されない。
    /// </summary>
    public class HeadPreview : IDisposable
    {
        PreviewRenderUtility pru;
        GameObject clone;
        GameObject source;
        Transform head;
        readonly Dictionary<SkinnedMeshRenderer, SkinnedMeshRenderer> map = new Dictionary<SkinnedMeshRenderer, SkinnedMeshRenderer>();
        Vector2 orbit = new Vector2(0, 0);
        float distance = 0.45f;
        Vector3 pivot;
        Vector3 forward = Vector3.forward;

        Transform eyeL, eyeR;
        Quaternion eyeLRest, eyeRRest;

        public bool IsValid => clone != null;
        public bool HasEyeBones => eyeL != null && eyeR != null;
        /// <summary>左右の目ボーン間の距離 (m)。不明なら一般的な 0.063。</summary>
        public float EyeDistance => HasEyeBones ? Mathf.Max(0.01f, Vector3.Distance(eyeL.position, eyeR.position)) : 0.063f;

        /// <summary>目ボーンを回転 (度)。yaw: +外側 / pitch: +上。複製に対してのみ。</summary>
        public void SetGaze(float yawLeftOut, float yawRightOut, float pitchUp)
        {
            if (!HasEyeBones) return;
            eyeL.rotation = Quaternion.AngleAxis(-yawLeftOut, Vector3.up) * Quaternion.AngleAxis(-pitchUp, Vector3.right) * eyeLRest;
            eyeR.rotation = Quaternion.AngleAxis(yawRightOut, Vector3.up) * Quaternion.AngleAxis(-pitchUp, Vector3.right) * eyeRRest;
        }

        public void Setup(GameObject avatar)
        {
            if (avatar == source && clone != null) return;
            Cleanup();
            source = avatar;
            if (avatar == null) return;

            pru = new PreviewRenderUtility();
            pru.camera.fieldOfView = 30f;
            pru.camera.nearClipPlane = 0.01f;
            pru.camera.farClipPlane = 20f;
            pru.camera.clearFlags = CameraClearFlags.SolidColor;
            pru.camera.backgroundColor = new Color(0.16f, 0.16f, 0.18f);
            pru.lights[0].intensity = 1.1f;
            pru.lights[0].transform.rotation = Quaternion.Euler(20, 160, 0);
            pru.lights[1].intensity = 0.6f;

            clone = UnityEngine.Object.Instantiate(avatar);
            clone.hideFlags = HideFlags.HideAndDontSave;
            clone.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            clone.SetActive(true);

            // 副作用のあるコンポーネントを除去 (PhysBone, 制約, スクリプト等)
            for (int pass = 0; pass < 4; pass++)
            {
                var mbs = clone.GetComponentsInChildren<MonoBehaviour>(true);
                if (mbs.Length == 0) break;
                for (int i = mbs.Length - 1; i >= 0; i--)
                    if (mbs[i] != null && CanRemove(mbs[i])) UnityEngine.Object.DestroyImmediate(mbs[i]);
            }

            var srcR = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var dstR = clone.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < srcR.Length && i < dstR.Length; i++)
            {
                map[srcR[i]] = dstR[i];
                dstR[i].updateWhenOffscreen = true;
            }

            pru.AddSingleGO(clone);

            // 顔ボーン (Humanoid Head) を探す
            var anim = clone.GetComponent<Animator>();
            head = (anim != null && anim.isHuman) ? anim.GetBoneTransform(HumanBodyBones.Head) : null;
            if (head == null)
                foreach (var t in clone.GetComponentsInChildren<Transform>(true))
                    if (t.name.ToLowerInvariant().Contains("head")) { head = t; break; }

            forward = Vector3.forward; // VRChat アバターは +Z が正面
            if (head != null)
            {
                // 目ボーンがあれば目の高さ、なければ Head から少し上
                eyeL = (anim != null && anim.isHuman) ? anim.GetBoneTransform(HumanBodyBones.LeftEye) : null;
                eyeR = (anim != null && anim.isHuman) ? anim.GetBoneTransform(HumanBodyBones.RightEye) : null;
                if (eyeL != null) eyeLRest = eyeL.rotation;
                if (eyeR != null) eyeRRest = eyeR.rotation;
                if (eyeL != null && eyeR != null)
                {
                    pivot = (eyeL.position + eyeR.position) * 0.5f - Vector3.up * 0.02f;
                    distance = Mathf.Max(0.25f, Vector3.Distance(eyeL.position, eyeR.position) * 6f);
                }
                else pivot = head.position + Vector3.up * 0.06f;
            }
            else
            {
                var b = new Bounds(Vector3.zero, Vector3.zero);
                foreach (var r in dstR) b.Encapsulate(r.bounds);
                pivot = b.center + Vector3.up * b.extents.y * 0.85f;
            }
        }

        static bool CanRemove(Component c)
        {
            foreach (var other in c.GetComponents<Component>())
            {
                if (other == c || other == null) continue;
                foreach (RequireComponent rc in other.GetType().GetCustomAttributes(typeof(RequireComponent), true))
                    if ((rc.m_Type0 != null && rc.m_Type0.IsInstanceOfType(c)) ||
                        (rc.m_Type1 != null && rc.m_Type1.IsInstanceOfType(c)) ||
                        (rc.m_Type2 != null && rc.m_Type2.IsInstanceOfType(c))) return false;
            }
            return true;
        }

        public void SetWeight(SkinnedMeshRenderer src, string shape, float weight100)
        {
            if (src == null || !map.TryGetValue(src, out var dst) || dst.sharedMesh == null) return;
            int i = dst.sharedMesh.GetBlendShapeIndex(shape);
            if (i >= 0) dst.SetBlendShapeWeight(i, weight100);
        }

        public void ResetWeights()
        {
            foreach (var kv in map)
            {
                if (kv.Key == null || kv.Value == null || kv.Value.sharedMesh == null) continue;
                for (int i = 0; i < kv.Value.sharedMesh.blendShapeCount; i++)
                    kv.Value.SetBlendShapeWeight(i, kv.Key.GetBlendShapeWeight(i));
            }
        }

        public void Draw(Rect r)
        {
            if (pru == null || clone == null)
            {
                EditorGUI.DrawRect(r, new Color(0.12f, 0.12f, 0.12f));
                GUI.Label(r, "アバター未設定", EditorStyles.centeredGreyMiniLabel);
                return;
            }
            HandleInput(r);
            if (Event.current.type != EventType.Repaint) return;

            var rot = Quaternion.Euler(orbit.y, 180f + orbit.x, 0);
            var dir = rot * Vector3.forward; // 正面から顔を見る
            pru.camera.transform.position = pivot - dir * distance;
            pru.camera.transform.LookAt(pivot);

            pru.BeginPreview(r, GUIStyle.none);
            pru.Render(true);
            var tex = pru.EndPreview();
            GUI.DrawTexture(r, tex, ScaleMode.StretchToFill, false);
        }

        void HandleInput(Rect r)
        {
            var e = Event.current;
            int id = GUIUtility.GetControlID(FocusType.Passive);
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown when r.Contains(e.mousePosition):
                    GUIUtility.hotControl = id; e.Use(); break;
                case EventType.MouseDrag when GUIUtility.hotControl == id:
                    if (e.button == 0) { orbit.x -= e.delta.x * 0.5f; orbit.y = Mathf.Clamp(orbit.y - e.delta.y * 0.5f, -80, 80); }
                    else pivot += (pru.camera.transform.right * -e.delta.x + pru.camera.transform.up * e.delta.y) * 0.0008f * distance * 2;
                    e.Use(); break;
                case EventType.MouseUp when GUIUtility.hotControl == id:
                    GUIUtility.hotControl = 0; e.Use(); break;
                case EventType.ScrollWheel when r.Contains(e.mousePosition):
                    distance = Mathf.Clamp(distance * (1 + e.delta.y * 0.05f), 0.08f, 3f); e.Use(); break;
            }
        }

        public void Cleanup()
        {
            if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
            clone = null;
            eyeL = eyeR = null;
            map.Clear();
            pru?.Cleanup();
            pru = null;
            source = null;
        }

        public void Dispose() => Cleanup();
    }
}
