using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRCFTMapper.Editor
{
    /// <summary>
    /// 統合グラフ上でマッピングのベジエカーブ (FTMapping.curve) を直接編集する。
    /// ・曲線 (またはリストの行) にホバー: その曲線の制御点とハンドルを表示
    /// ・点 / ハンドルをドラッグ: 移動 (Shift: スナップ, Alt: ハンドル分離)。ハンドルを動かすとカーブが有効になる
    /// ・Ctrl + 端の点をドラッグ: 両端の出力値を変更 (通常は in 範囲のドラッグが優先)
    /// ・ホバー中の曲線をダブルクリック: 点を追加
    /// ・右クリック: 点の削除 / プリセット / カーブ有効・無効
    ///
    /// 重なり時の優先度:
    ///  1. ドラッグ中の曲線
    ///  2. 制御点 (ホバー中の曲線の点 → 最も近い点)。ただし in 範囲ハンドルの方が近ければそちら
    ///  3. 曲線本体 (ホバー中の曲線は判定に余裕を持たせて維持、同距離なら上に描かれている曲線)
    /// </summary>
    public partial class VRCFTMapperWindow
    {
        const float CurveVMin = -0.25f, CurveVMax = 1.25f;
        const float PointPick = 7f, LinePick = 8f, LineSticky = 4f;
        FTMapping cvDrag;       // ドラッグ中の曲線
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

        /// <summary>
        /// 曲線の制御点・ハンドルを拾う。カーブ無効の曲線も直線カーブとしてハンドルを出す
        /// (ハンドルを動かすとカーブが有効になる)。端の点は in ハンドルと重なるので ctrl 時のみ。
        /// </summary>
        int PickCurve(Rect r, FTMapping m, Keyframe[] keys, Vector2 mp, bool ctrl, float x0, float y0, float y1, out int part, out float dist)
        {
            part = 0; dist = PointPick;
            int best = -1;
            for (int i = 0; i < keys.Length; i++)
            {
                bool end = i == 0 || i == keys.Length - 1;
                if (!end || ctrl)
                {
                    float d = Vector2.Distance(mp, CvToGUI(r, m, new Vector2(keys[i].time, keys[i].value), x0, y0, y1));
                    if (d < dist) { dist = d; best = i; part = 0; }
                }
                if (i > 0) { float d = Vector2.Distance(mp, CvToGUI(r, m, CvHandle(keys, i, false), x0, y0, y1)); if (d < dist) { dist = d; best = i; part = 1; } }
                if (i < keys.Length - 1) { float d = Vector2.Distance(mp, CvToGUI(r, m, CvHandle(keys, i, true), x0, y0, y1)); if (d < dist) { dist = d; best = i; part = 2; } }
            }
            return best;
        }

        float RangeHandleDist(Rect r, List<FTMapping> ms, Vector2 mp, float x0, float y0, float y1)
        {
            float bd = float.MaxValue;
            foreach (var m in ms)
            {
                if (Vis(m) <= 0.001f) continue;
                for (int e = 0; e < 2; e++)
                {
                    float x = e == 0 ? m.inMin : m.inMax;
                    bd = Mathf.Min(bd, Vector2.Distance(P(r, x, FTMath.Evaluate(m, x), x0, y0, y1), mp));
                }
            }
            return bd;
        }

        /// <summary>制御点の当たり判定 (優先度 2)。ホバー中の曲線の点を優先。</summary>
        FTMapping PointTarget(Rect r, List<FTMapping> ms, Vector2 mp, bool ctrl, float x0, float y0, float y1, out int key, out int part)
        {
            key = -1; part = 0;
            FTMapping best = null; float bd = float.MaxValue;
            var order = ms.Where(m => Vis(m) > 0.001f).ToList();
            if (hotMapping != null && order.Remove(hotMapping)) order.Insert(0, hotMapping);
            foreach (var m in order)
            {
                int k = PickCurve(r, m, CurveKeys(m), mp, ctrl, x0, y0, y1, out int pt, out float d);
                if (k < 0) continue;
                if (m == hotMapping) { best = m; key = k; part = pt; bd = d; break; } // ホバー中の曲線の点は最優先
                if (d < bd) { bd = d; best = m; key = k; part = pt; }
            }
            if (best != null && RangeHandleDist(r, ms, mp, x0, y0, y1) < bd) { key = -1; return null; } // in ハンドルの方が近い
            return best;
        }

        /// <summary>曲線本体の当たり判定 (優先度 3)。ホバー中は余裕を持たせて維持、同距離なら上に描かれた曲線。</summary>
        FTMapping LineTarget(Rect r, List<FTMapping> ms, Vector2 mp, float x0, float y0, float y1)
        {
            float x = Mathf.Lerp(x0, 1, Mathf.InverseLerp(r.xMin, r.xMax, mp.x));
            FTMapping best = null; float bd = float.MaxValue;
            for (int k = 0; k < ms.Count; k++) // 後に描かれた (上にある) 曲線が同距離で勝つよう <=
            {
                var m = ms[k];
                if (Vis(m) <= 0.001f) continue;
                float d = Mathf.Abs(P(r, x, FTMath.Evaluate(m, x), x0, y0, y1).y - mp.y);
                if (m == hotMapping) d -= LineSticky;
                if (d <= bd) { bd = d; best = m; }
            }
            return bd < LinePick ? best : null;
        }

        void DrawCurveHandles(Rect r, List<FTMapping> ms, float x0, float y0, float y1)
        {
            var m = cvDrag ?? hotMapping;
            if (m == null || !ms.Contains(m) || Vis(m) <= 0.001f) return;
            var keys = CurveKeys(m);
            var pal = Palette[ms.IndexOf(m) % Palette.Length];
            float a = m.useCurve ? 1f : 0.45f; // カーブ無効時は控えめに
            Handles.BeginGUI();
            for (int i = 0; i < keys.Length; i++)
            {
                var kp = CvToGUI(r, m, new Vector2(keys[i].time, keys[i].value), x0, y0, y1);
                if (i > 0) DrawTangent(kp, CvToGUI(r, m, CvHandle(keys, i, false), x0, y0, y1), FTControls.Blue, a, cvDrag == m && cvKey == i && cvPart == 1);
                if (i < keys.Length - 1) DrawTangent(kp, CvToGUI(r, m, CvHandle(keys, i, true), x0, y0, y1), FTControls.Red, a, cvDrag == m && cvKey == i && cvPart == 2);
                if (i == 0 || i == keys.Length - 1) continue; // 端は in ハンドルと重なるので描かない
                var fill = cvDrag == m && cvKey == i && cvPart == 0 ? Color.white : new Color(0.85f, 0.85f, 0.85f);
                Handles.DrawSolidRectangleWithOutline(new Rect(kp.x - 4, kp.y - 4, 8, 8), fill, pal);
            }
            Handles.EndGUI();
            if (cvDrag == null && r.Contains(Event.current.mousePosition))
            {
                var lab = m.useCurve ? "ダブルクリック:点追加  右クリック:メニュー  Ctrl+端:出力値  Alt:ハンドル分離"
                                     : "ハンドルをドラッグ / ダブルクリックでカーブを有効化";
                GUI.Label(new Rect(r.xMin + 4, r.yMax - 16, r.width - 8, 16), lab, EditorStyles.miniLabel);
            }
        }

        static void DrawTangent(Vector2 from, Vector2 to, Color c, float a, bool active)
        {
            c.a *= a;
            Handles.color = c;
            Handles.DrawAAPolyLine(1.5f, from, to);
            Handles.DrawSolidDisc(to, Vector3.forward, active ? 5 : 4);
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
            if (cvDrag != null && !mapper.mappings.Contains(cvDrag)) { cvDrag = null; cvKey = -1; }

            switch (ev.GetTypeForControl(id))
            {
                case EventType.MouseMove:
                case EventType.Repaint:
                {
                    if (cvDrag != null && ms.Contains(cvDrag)) { hotNext = cvDrag; return true; }
                    if (GUIUtility.hotControl != 0 || !r.Contains(ev.mousePosition)) return false;
                    var t = PointTarget(r, ms, ev.mousePosition, ev.control || ev.command, x0, y0, y1, out _, out _)
                            ?? LineTarget(r, ms, ev.mousePosition, x0, y0, y1);
                    if (t == null) return false;
                    hotNext = t;
                    if (ev.type == EventType.MouseMove) Repaint();
                    return true;
                }

                case EventType.MouseDown:
                {
                    if (!r.Contains(ev.mousePosition)) return false;
                    var pt = PointTarget(r, ms, ev.mousePosition, ev.control || ev.command, x0, y0, y1, out int key, out int part);

                    if (ev.button == 1)
                    {
                        var target = pt ?? LineTarget(r, ms, ev.mousePosition, x0, y0, y1);
                        if (target == null) return false;
                        ShowCurveMenu(target, pt != null && part == 0 ? key : -1);
                        ev.Use();
                        return true;
                    }
                    if (ev.button != 0) return false;

                    if (pt != null)
                    {
                        Undo.RecordObject(mapper, "カーブ編集 (グラフ)");
                        cvDrag = pt; cvKey = key; cvPart = part;
                        GUIUtility.hotControl = id;
                        ev.Use();
                        return true;
                    }
                    if (ev.clickCount == 2)
                    {
                        var m = LineTarget(r, ms, ev.mousePosition, x0, y0, y1);
                        if (m == null) return false;
                        var tv = GUIToCv(r, m, ev.mousePosition, x0, y0, y1);
                        if (tv.x <= 0.001f || tv.x >= 0.999f) return false;
                        Undo.RecordObject(mapper, "カーブ点を追加");
                        var keys = CurveKeys(m);
                        var c = new AnimationCurve(keys);
                        // 点は曲線上 (マウスの x 位置) に置き、形を変えない
                        float v = m.useCurve ? c.Evaluate(tv.x) : tv.x;
                        float slope = m.useCurve ? (c.Evaluate(tv.x + 0.01f) - c.Evaluate(tv.x - 0.01f)) / 0.02f : 1f;
                        int ni = c.AddKey(new Keyframe(tv.x, v, slope, slope, 1f / 3, 1f / 3) { weightedMode = WeightedMode.Both });
                        SetCurveKeys(m, c.keys);
                        cvDrag = m; cvKey = ni; cvPart = 0;
                        GUIUtility.hotControl = id;
                        ev.Use();
                        return true;
                    }
                    return false; // 空き領域 / 曲線本体のシングルクリック → スクラブ
                }

                case EventType.MouseDrag:
                {
                    if (GUIUtility.hotControl != id || cvDrag == null || cvKey < 0) return false;
                    var m = cvDrag;
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
                    SetCurveKeys(m, keys);
                    ev.Use();
                    return true;
                }

                case EventType.MouseUp:
                    if (GUIUtility.hotControl != id || cvDrag == null) return false;
                    GUIUtility.hotControl = 0; cvDrag = null; cvKey = -1;
                    ev.Use();
                    return true;
            }
            return false;
        }

        void ShowCurveMenu(FTMapping m, int key)
        {
            var menu = new GenericMenu();
            void Set(string undo, Keyframe[] keys)
            {
                Undo.RecordObject(mapper, undo);
                for (int i = 0; i < keys.Length; i++) keys[i].weightedMode = WeightedMode.Both;
                SetCurveKeys(m, keys);
            }
            menu.AddDisabledItem(new GUIContent(m.blendShape));
            var cur = CurveKeys(m);
            if (key > 0 && key < cur.Length - 1)
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
