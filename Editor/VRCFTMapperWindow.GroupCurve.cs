using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRCFTMapper.Editor
{
    /// <summary>
    /// 統合グラフ上でマッピングのベジエカーブ (FTMapping.curve) を直接編集する。
    /// ・曲線をダブルクリック: そのマッピングをカーブ編集対象にする (編集中ならその位置に点を追加)
    /// ・点 / ハンドルをドラッグ: 移動 (Shift: スナップ, Alt: ハンドル分離)
    /// ・Ctrl + 端の点をドラッグ: 両端の出力値を変更 (通常は in 範囲のドラッグが優先)
    /// ・右クリック: 点の削除 / プリセット / リセット / カーブ無効化 / 編集終了
    /// </summary>
    public partial class VRCFTMapperWindow
    {
        const float CurveVMin = -0.25f, CurveVMax = 1.25f;
        FTMapping curveEdit;   // カーブ編集中のマッピング
        int cvKey = -1, cvPart; // part: 0 点, 1 in ハンドル, 2 out ハンドル

        static Keyframe[] CurveKeys(FTMapping m)
        {
            if (m.curve == null || m.curve.length < 2) m.curve = AnimationCurve.Linear(0, 0, 1, 1);
            var keys = m.curve.keys;
            for (int i = 0; i < keys.Length; i++) keys[i].weightedMode = WeightedMode.Both;
            return keys;
        }

        static Vector2 CvHandle(Keyframe[] keys, int i, bool outSide)
        {
            var k = keys[i];
            if (outSide)
            {
                if (i >= keys.Length - 1) return new Vector2(k.time, k.value);
                float dx = k.outWeight * (keys[i + 1].time - k.time);
                return new Vector2(k.time + dx, k.value + k.outTangent * dx);
            }
            if (i == 0) return new Vector2(k.time, k.value);
            float dxi = k.inWeight * (k.time - keys[i - 1].time);
            return new Vector2(k.time - dxi, k.value - k.inTangent * dxi);
        }

        // 正規化カーブ空間 (t, v) → グラフ GUI 座標
        static Vector2 CvToGUI(Rect r, FTMapping m, Vector2 tv, float x0, float y0, float y1)
        {
            float x = Mathf.LerpUnclamped(m.inMin, m.inMax, tv.x);
            float y = Mathf.LerpUnclamped(m.outMin, m.outMax, tv.y);
            return P(r, x, y, x0, y0, y1);
        }

        // グラフ GUI 座標 → 正規化カーブ空間 (t, v)
        static Vector2 GUIToCv(Rect r, FTMapping m, Vector2 g, float x0, float y0, float y1)
        {
            float x = Mathf.Lerp(x0, 1, Mathf.InverseLerp(r.xMin, r.xMax, g.x));
            float y = Mathf.LerpUnclamped(y0, y1, (g.y - r.yMax) / (r.yMin - r.yMax));
            float t = Mathf.Approximately(m.inMin, m.inMax) ? 0.5f : (x - m.inMin) / (m.inMax - m.inMin);
            float v = Mathf.Approximately(m.outMin, m.outMax) ? 0f : (y - m.outMin) / (m.outMax - m.outMin);
            return new Vector2(t, v);
        }

        bool CurveEditable(List<FTMapping> ms) => curveEdit != null && ms.Contains(curveEdit) && Vis(curveEdit) > 0.001f;

        void DrawCurveHandles(Rect r, List<FTMapping> ms, float x0, float y0, float y1)
        {
            if (!CurveEditable(ms)) return;
            var m = curveEdit;
            var keys = CurveKeys(m);
            int col = ms.IndexOf(m);
            Handles.BeginGUI();
            for (int i = 0; i < keys.Length; i++)
            {
                var kp = CvToGUI(r, m, new Vector2(keys[i].time, keys[i].value), x0, y0, y1);
                if (m.useCurve)
                {
                    if (i > 0) { var h = CvToGUI(r, m, CvHandle(keys, i, false), x0, y0, y1); Handles.color = FTControls.Blue; Handles.DrawAAPolyLine(1.5f, kp, h); Handles.DrawSolidDisc(h, Vector3.forward, 4); }
                    if (i < keys.Length - 1) { var h = CvToGUI(r, m, CvHandle(keys, i, true), x0, y0, y1); Handles.color = FTControls.Red; Handles.DrawAAPolyLine(1.5f, kp, h); Handles.DrawSolidDisc(h, Vector3.forward, 4); }
                }
                if (i == 0 || i == keys.Length - 1) continue; // 端は in ハンドルと重なるので描かない
                var fill = cvKey == i && curveEdit == m ? Color.white : new Color(0.9f, 0.9f, 0.9f);
                Handles.DrawSolidRectangleWithOutline(new Rect(kp.x - 4, kp.y - 4, 8, 8), fill, Palette[col % Palette.Length]);
            }
            Handles.EndGUI();
            var lab = m.useCurve
                ? $"カーブ編集: {m.blendShape}   ダブルクリック:点追加  右クリック:メニュー  Ctrl+端:出力値  Alt:ハンドル分離"
                : $"カーブ編集: {m.blendShape} (カーブ無効 / ダブルクリックで点を追加すると有効化)";
            GUI.Label(new Rect(r.xMin + 4, r.yMax - 16, r.width - 8, 16), lab, EditorStyles.miniLabel);
        }

        int PickCurve(Rect r, FTMapping m, Keyframe[] keys, Vector2 mp, bool ctrl, float x0, float y0, float y1, out int part)
        {
            part = 0;
            int best = -1; float bd = 7;
            for (int i = 0; i < keys.Length; i++)
            {
                bool end = i == 0 || i == keys.Length - 1;
                if (!end || ctrl)
                {
                    float d = Vector2.Distance(mp, CvToGUI(r, m, new Vector2(keys[i].time, keys[i].value), x0, y0, y1));
                    if (d < bd) { bd = d; best = i; part = 0; }
                }
                if (!m.useCurve) continue;
                if (i > 0) { float d = Vector2.Distance(mp, CvToGUI(r, m, CvHandle(keys, i, false), x0, y0, y1)); if (d < bd) { bd = d; best = i; part = 1; } }
                if (i < keys.Length - 1) { float d = Vector2.Distance(mp, CvToGUI(r, m, CvHandle(keys, i, true), x0, y0, y1)); if (d < bd) { bd = d; best = i; part = 2; } }
            }
            return best;
        }

        FTMapping NearestCurve(Rect r, List<FTMapping> ms, Vector2 mp, float x0, float y0, float y1)
        {
            float x = Mathf.Lerp(x0, 1, Mathf.InverseLerp(r.xMin, r.xMax, mp.x));
            FTMapping best = null; float bd = 8;
            foreach (var m in ms)
            {
                if (Vis(m) <= 0.001f) continue;
                float d = Mathf.Abs(P(r, x, FTMath.Evaluate(m, x), x0, y0, y1).y - mp.y);
                if (d < bd) { bd = d; best = m; }
            }
            return best;
        }

        void SetCurveKeys(FTMapping m, Keyframe[] keys, bool enable = true)
        {
            m.curve = new AnimationCurve(keys);
            if (enable) m.useCurve = true;
            Dirty(); ApplyPreview(); Repaint();
        }

        /// <summary>カーブ編集の入力を処理したら true (以降のグラフ処理をスキップ)。</summary>
        bool HandleCurveInput(Rect r, List<FTMapping> ms, float x0, float y0, float y1, int id)
        {
            var ev = Event.current;
            if (curveEdit != null && !mapper.mappings.Contains(curveEdit)) curveEdit = null;
            bool editable = CurveEditable(ms);

            switch (ev.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                {
                    if (!r.Contains(ev.mousePosition)) return false;
                    var near = NearestCurve(r, ms, ev.mousePosition, x0, y0, y1);

                    if (ev.button == 1)
                    {
                        var target = editable ? curveEdit : near;
                        if (target == null) return false;
                        var keys0 = CurveKeys(target);
                        int k0 = editable ? PickCurve(r, target, keys0, ev.mousePosition, true, x0, y0, y1, out int p0) : -1;
                        ShowCurveMenu(target, editable, k0);
                        ev.Use();
                        return true;
                    }
                    if (ev.button != 0) return false;

                    if (editable)
                    {
                        var m = curveEdit;
                        var keys = CurveKeys(m);
                        int k = PickCurve(r, m, keys, ev.mousePosition, ev.control || ev.command, x0, y0, y1, out int part);
                        if (k >= 0)
                        {
                            Undo.RecordObject(mapper, "カーブ編集 (グラフ)");
                            cvKey = k; cvPart = part;
                            GUIUtility.hotControl = id;
                            ev.Use();
                            return true;
                        }
                        if (ev.clickCount == 2 && near == m)
                        {
                            var tv = GUIToCv(r, m, ev.mousePosition, x0, y0, y1);
                            if (tv.x > 0.001f && tv.x < 0.999f)
                            {
                                Undo.RecordObject(mapper, "カーブ点を追加");
                                if (!m.useCurve) { m.useCurve = true; }
                                var c = new AnimationCurve(keys);
                                float slope = (c.Evaluate(tv.x + 0.01f) - c.Evaluate(tv.x - 0.01f)) / 0.02f;
                                int ni = c.AddKey(new Keyframe(tv.x, Mathf.Clamp(tv.y, CurveVMin, CurveVMax), slope, slope, 1f / 3, 1f / 3) { weightedMode = WeightedMode.Both });
                                SetCurveKeys(m, c.keys);
                                cvKey = ni; cvPart = 0;
                                GUIUtility.hotControl = id;
                                ev.Use();
                                return true;
                            }
                        }
                    }
                    if (ev.clickCount == 2)
                    {
                        if (near != null && near != curveEdit) { curveEdit = near; cvKey = -1; ev.Use(); Repaint(); return true; }
                        if (near == null && curveEdit != null && ms.Contains(curveEdit)) { curveEdit = null; ev.Use(); Repaint(); return true; }
                    }
                    return false;
                }

                case EventType.MouseDrag:
                {
                    if (GUIUtility.hotControl != id || cvKey < 0 || !editable) return false;
                    var m = curveEdit;
                    var keys = CurveKeys(m);
                    if (cvKey >= keys.Length) { cvKey = -1; return false; }
                    var p = GUIToCv(r, m, ev.mousePosition, x0, y0, y1);
                    if (ev.shift) { p.x = Mathf.Round(p.x * 20) / 20; p.y = Mathf.Round(p.y * 20) / 20; }
                    var k = keys[cvKey];
                    bool last = cvKey == keys.Length - 1;
                    if (cvPart == 0)
                    {
                        if (cvKey != 0 && !last)
                            k.time = Mathf.Clamp(p.x, keys[cvKey - 1].time + 0.01f, keys[cvKey + 1].time - 0.01f);
                        k.value = Mathf.Clamp(p.y, CurveVMin, CurveVMax);
                    }
                    else if (cvPart == 2)
                    {
                        float seg = keys[cvKey + 1].time - k.time;
                        float dx = Mathf.Clamp(p.x - k.time, 0.001f, seg);
                        k.outTangent = (p.y - k.value) / dx;
                        k.outWeight = dx / seg;
                        if (!ev.alt && cvKey > 0) k.inTangent = k.outTangent;
                    }
                    else
                    {
                        float seg = k.time - keys[cvKey - 1].time;
                        float dx = Mathf.Clamp(k.time - p.x, 0.001f, seg);
                        k.inTangent = (k.value - p.y) / dx;
                        k.inWeight = dx / seg;
                        if (!ev.alt && !last) k.outTangent = k.inTangent;
                    }
                    k.weightedMode = WeightedMode.Both;
                    keys[cvKey] = k;
                    SetCurveKeys(m, keys, enable: m.useCurve || cvPart == 0);
                    ev.Use();
                    return true;
                }

                case EventType.MouseUp:
                    if (GUIUtility.hotControl != id || cvKey < 0) return false;
                    GUIUtility.hotControl = 0; cvKey = -1;
                    ev.Use();
                    return true;
            }
            return false;
        }

        void ShowCurveMenu(FTMapping m, bool editing, int key)
        {
            var menu = new GenericMenu();
            void Set(string undo, Keyframe[] keys)
            {
                Undo.RecordObject(mapper, undo);
                for (int i = 0; i < keys.Length; i++) keys[i].weightedMode = WeightedMode.Both;
                SetCurveKeys(m, keys);
                curveEdit = m;
            }
            if (!editing) menu.AddItem(new GUIContent($"{m.blendShape} のカーブを編集"), false, () => { curveEdit = m; Repaint(); });
            else menu.AddItem(new GUIContent("カーブ編集を終了"), false, () => { curveEdit = null; Repaint(); });
            var cur = CurveKeys(m);
            if (editing && key > 0 && key < cur.Length - 1)
            {
                int kk = key;
                menu.AddItem(new GUIContent("点を削除"), false, () =>
                {
                    var c = new AnimationCurve(CurveKeys(m)); c.RemoveKey(kk);
                    Set("カーブ点を削除", c.keys);
                });
            }
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("プリセット/直線"), false, () => Set("カーブをリセット", AnimationCurve.Linear(0, 0, 1, 1).keys));
            menu.AddItem(new GUIContent("プリセット/イーズイン"), false, () => Set("カーブプリセット", new[] { new Keyframe(0, 0, 0, 0), new Keyframe(1, 1, 2, 2) }));
            menu.AddItem(new GUIContent("プリセット/イーズアウト"), false, () => Set("カーブプリセット", new[] { new Keyframe(0, 0, 2, 2), new Keyframe(1, 1, 0, 0) }));
            menu.AddItem(new GUIContent("プリセット/S字"), false, () => Set("カーブプリセット", AnimationCurve.EaseInOut(0, 0, 1, 1).keys));
            menu.AddItem(new GUIContent("カーブを使う"), m.useCurve, () =>
            {
                Undo.RecordObject(mapper, "カーブの有効/無効");
                m.useCurve = !m.useCurve;
                Dirty(); ApplyPreview(); Repaint();
            });
            menu.ShowAsContext();
        }
    }
}
