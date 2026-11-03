using UnityEditor;
using UnityEngine;

namespace VRCFTMapper.Editor
{
    public static class FTControls
    {
        public static readonly Color Blue = new Color(0.25f, 0.55f, 1f);
        public static readonly Color Red = new Color(1f, 0.32f, 0.3f);
        public static readonly Color Marker = new Color(1f, 0.85f, 0.2f);

        static int dragHandle = -1; // 0 = 下(青) 1 = 上(赤)

        /// <summary>
        /// 青▼(下) と 赤▼(上) の 2 つの三角矢印で範囲を設定するバー。
        /// allowCross = true なら上下を入れ替えられる (out の反転)。
        /// </summary>
        public static bool RangeArrows(Rect r, string label, ref float lo, ref float hi,
            float viewMin, float viewMax, float validMin, float validMax, bool allowCross,
            Object undoTarget, float? marker = null)
        {
            var labelRect = new Rect(r.x, r.y, 34, r.height);
            GUI.Label(labelRect, label, EditorStyles.boldLabel);
            var valRect = new Rect(r.xMax - 112, r.y, 112, r.height);
            r = new Rect(r.x + 38, r.y, r.width - 38 - 116, r.height);

            float X(float v) => Mathf.Lerp(r.x + 6, r.xMax - 6, Mathf.InverseLerp(viewMin, viewMax, v));
            float V(float x) => Mathf.Lerp(viewMin, viewMax, Mathf.InverseLerp(r.x + 6, r.xMax - 6, x));

            float barY = r.y + r.height * 0.62f;
            int id = GUIUtility.GetControlID(FocusType.Passive);
            var e = Event.current;
            bool changed = false;

            if (e.type == EventType.Repaint)
            {
                // トラック
                EditorGUI.DrawRect(new Rect(r.x + 6, barY - 2, r.width - 12, 4), new Color(0, 0, 0, 0.45f));
                // 正規範囲 (0..1)
                EditorGUI.DrawRect(new Rect(X(validMin), barY - 2, X(validMax) - X(validMin), 4), new Color(1, 1, 1, 0.25f));
                // 選択範囲
                float a = X(Mathf.Min(lo, hi)), b = X(Mathf.Max(lo, hi));
                var fill = lo <= hi ? new Color(0.6f, 0.45f, 1f, 0.8f) : new Color(1f, 0.6f, 0.2f, 0.8f); // 反転時はオレンジ
                EditorGUI.DrawRect(new Rect(a, barY - 2, b - a, 4), fill);
                // 範囲外警告 (シェイプキー限界超え)
                if (lo < validMin || hi < validMin || lo > validMax || hi > validMax)
                    GUI.Label(new Rect(r.x, r.y - 2, r.width, 12), "限界超え", EditorStyles.miniBoldLabel);
                // 目盛り
                for (float t = Mathf.Ceil(viewMin * 4) / 4; t <= viewMax + 1e-4f; t += 0.25f)
                    EditorGUI.DrawRect(new Rect(X(t), barY + 3, 1, Mathf.Approximately(t % 1, 0) ? 6 : 3), new Color(1, 1, 1, 0.4f));
                if (marker.HasValue)
                    EditorGUI.DrawRect(new Rect(X(marker.Value) - 1, barY - 8, 2, 16), Marker);
                Triangle(X(lo), barY - 3, Blue, dragHandle == 0 && GUIUtility.hotControl == id);
                Triangle(X(hi), barY - 3, Red, dragHandle == 1 && GUIUtility.hotControl == id);
            }

            // goto case は when 付きラベルへ飛べないため、フラグでドラッグ処理を共通化
            bool applyDrag = false;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown when e.button == 0 && r.Contains(e.mousePosition):
                {
                    float dl = Mathf.Abs(e.mousePosition.x - X(lo)), dh = Mathf.Abs(e.mousePosition.x - X(hi));
                    dragHandle = dl < dh || (Mathf.Approximately(dl, dh) && e.mousePosition.x < X(lo)) ? 0 : 1;
                    GUIUtility.hotControl = id;
                    Undo.RecordObject(undoTarget, label + " 範囲変更");
                    applyDrag = true;
                    break;
                }
                case EventType.MouseDrag when GUIUtility.hotControl == id:
                    applyDrag = true;
                    break;
                case EventType.MouseUp when GUIUtility.hotControl == id:
                    GUIUtility.hotControl = 0; dragHandle = -1; e.Use(); break;
            }
            if (applyDrag)
            {
                float v = V(e.mousePosition.x);
                if (e.shift) v = Mathf.Round(v * 20) / 20; // Shift で 0.05 スナップ
                v = Mathf.Clamp(v, viewMin, viewMax);
                if (dragHandle == 0) lo = allowCross ? v : Mathf.Min(v, hi);
                else hi = allowCross ? v : Mathf.Max(v, lo);
                changed = true;
                e.Use();
            }

            // 数値入力 (青 / 赤)
            var c = GUI.color;
            EditorGUI.BeginChangeCheck();
            GUI.color = Blue;
            float nlo = EditorGUI.DelayedFloatField(new Rect(valRect.x, valRect.y + 1, 54, 16), lo);
            GUI.color = Red;
            float nhi = EditorGUI.DelayedFloatField(new Rect(valRect.x + 58, valRect.y + 1, 54, 16), hi);
            GUI.color = c;
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(undoTarget, label + " 範囲変更");
                lo = nlo; hi = nhi;
                if (!allowCross && lo > hi) { var t = lo; lo = hi; hi = t; }
                changed = true;
            }
            return changed;
        }

        static void Triangle(float x, float y, Color col, bool active)
        {
            Handles.BeginGUI();
            var old = Handles.color;
            Handles.color = active ? Color.Lerp(col, Color.white, 0.4f) : col;
            Handles.DrawAAConvexPolygon(new Vector3(x - 6, y - 10), new Vector3(x + 6, y - 10), new Vector3(x, y));
            Handles.color = old;
            Handles.EndGUI();
        }

        /// <summary>in→out の関数グラフ (サムネ)。</summary>
        public static void MappingGraph(Rect r, FTMapping m, float inView0, float inView1, float? input)
        {
            if (Event.current.type != EventType.Repaint) return;
            EditorGUI.DrawRect(r, new Color(0.1f, 0.1f, 0.12f));
            float oMin = Mathf.Min(0, m.outMin, m.outMax), oMax = Mathf.Max(1, m.outMin, m.outMax);
            Vector3 P(float x, float y) => new Vector3(
                Mathf.Lerp(r.x + 3, r.xMax - 3, Mathf.InverseLerp(inView0, inView1, x)),
                Mathf.Lerp(r.yMax - 3, r.y + 3, Mathf.InverseLerp(oMin, oMax, y)));
            Handles.BeginGUI();
            Handles.color = new Color(1, 1, 1, 0.15f);
            Handles.DrawLine(P(inView0, 0), P(inView1, 0));
            Handles.DrawLine(P(inView0, 1), P(inView1, 1));
            var pts = new Vector3[48];
            for (int i = 0; i < pts.Length; i++)
            {
                float x = Mathf.Lerp(inView0, inView1, i / (pts.Length - 1f));
                pts[i] = P(x, FTMath.Evaluate(m, x));
            }
            Handles.color = new Color(0.7f, 0.6f, 1f);
            Handles.DrawAAPolyLine(2f, pts);
            if (input.HasValue)
            {
                var p = P(input.Value, FTMath.Evaluate(m, input.Value));
                Handles.color = Marker;
                Handles.DrawSolidDisc(p, Vector3.forward, 3f);
            }
            Handles.EndGUI();
        }

        // ================= ベジエカーブエディタ =================

        static int bzKey = -1, bzPart = 0; // part: 0 点, 1 in ハンドル, 2 out ハンドル

        /// <summary>
        /// 正規化空間 (x: in 0..1, y: out 0..1) のカーブを編集。
        /// 各キーの tangent + weight (weightedMode=Both) をベジエのハンドルとして扱う。
        /// ダブルクリック: 点追加 / 右クリック: 削除・リセット / Shift: スナップ
        /// </summary>
        public static bool BezierEditor(Rect r, ref AnimationCurve curve, Object undoTarget, float? inputT)
        {
            if (curve == null || curve.length < 2) curve = AnimationCurve.Linear(0, 0, 1, 1);
            const float yMin = -0.25f, yMax = 1.25f;
            Vector2 ToGUI(Vector2 p) => new Vector2(Mathf.Lerp(r.x + 8, r.xMax - 8, p.x), Mathf.Lerp(r.yMax - 8, r.y + 8, Mathf.InverseLerp(yMin, yMax, p.y)));
            Vector2 ToCurve(Vector2 g) => new Vector2(Mathf.InverseLerp(r.x + 8, r.xMax - 8, g.x), Mathf.Lerp(yMin, yMax, Mathf.InverseLerp(r.yMax - 8, r.y + 8, g.y)));

            var keys = curve.keys;
            for (int i = 0; i < keys.Length; i++) keys[i].weightedMode = WeightedMode.Both;

            Vector2 HandlePos(int i, bool outSide)
            {
                var k = keys[i];
                if (outSide)
                {
                    if (i >= keys.Length - 1) return new Vector2(k.time, k.value);
                    float seg = keys[i + 1].time - k.time;
                    float dx = k.outWeight * seg;
                    return new Vector2(k.time + dx, k.value + k.outTangent * dx);
                }
                else
                {
                    if (i == 0) return new Vector2(k.time, k.value);
                    float seg = k.time - keys[i - 1].time;
                    float dx = k.inWeight * seg;
                    return new Vector2(k.time - dx, k.value - k.inTangent * dx);
                }
            }

            var e = Event.current;
            int id = GUIUtility.GetControlID(FocusType.Passive);
            bool changed = false;

            if (e.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(r, new Color(0.11f, 0.11f, 0.13f));
                Handles.BeginGUI();
                Handles.color = new Color(1, 1, 1, 0.07f);
                for (int i = 0; i <= 10; i++)
                {
                    Handles.DrawLine(ToGUI(new Vector2(i / 10f, yMin)), ToGUI(new Vector2(i / 10f, yMax)));
                    Handles.DrawLine(ToGUI(new Vector2(0, i / 10f)), ToGUI(new Vector2(1, i / 10f)));
                }
                Handles.color = new Color(1, 1, 1, 0.3f);
                Handles.DrawLine(ToGUI(new Vector2(0, 0)), ToGUI(new Vector2(1, 0)));
                Handles.DrawLine(ToGUI(new Vector2(0, 1)), ToGUI(new Vector2(1, 1)));
                Handles.color = new Color(1, 1, 1, 0.12f);
                Handles.DrawDottedLine(ToGUI(Vector2.zero), ToGUI(Vector2.one), 3);

                // ベジエ本体
                for (int i = 0; i < keys.Length - 1; i++)
                {
                    Handles.DrawBezier(ToGUI(new Vector2(keys[i].time, keys[i].value)), ToGUI(new Vector2(keys[i + 1].time, keys[i + 1].value)),
                        ToGUI(HandlePos(i, true)), ToGUI(HandlePos(i + 1, false)), new Color(0.7f, 0.6f, 1f), null, 2.5f);
                }
                // ハンドル
                for (int i = 0; i < keys.Length; i++)
                {
                    var kp = ToGUI(new Vector2(keys[i].time, keys[i].value));
                    if (i > 0) { var h = ToGUI(HandlePos(i, false)); Handles.color = Blue; Handles.DrawLine(kp, h); Handles.DrawSolidDisc(h, Vector3.forward, 4); }
                    if (i < keys.Length - 1) { var h = ToGUI(HandlePos(i, true)); Handles.color = Red; Handles.DrawLine(kp, h); Handles.DrawSolidDisc(h, Vector3.forward, 4); }
                    Handles.color = bzKey == i && GUIUtility.hotControl == id ? Color.white : new Color(0.9f, 0.9f, 0.9f);
                    Handles.DrawSolidRectangleWithOutline(new Rect(kp.x - 4, kp.y - 4, 8, 8), Handles.color, Color.black);
                }
                if (inputT.HasValue)
                {
                    var p = ToGUI(new Vector2(inputT.Value, curve.Evaluate(inputT.Value)));
                    Handles.color = Marker;
                    Handles.DrawLine(new Vector3(p.x, r.y), new Vector3(p.x, r.yMax));
                    Handles.DrawSolidDisc(p, Vector3.forward, 4);
                }
                Handles.EndGUI();
                GUI.Label(new Rect(r.x + 4, r.y + 2, 300, 16), "in (正規化) → out (正規化)   ダブルクリック:追加 右クリック:削除", EditorStyles.miniLabel);
            }

            int Pick(Vector2 mp, out int part)
            {
                part = 0;
                for (int i = 0; i < keys.Length; i++)
                {
                    if (Vector2.Distance(mp, ToGUI(new Vector2(keys[i].time, keys[i].value))) < 7) { part = 0; return i; }
                    if (i > 0 && Vector2.Distance(mp, ToGUI(HandlePos(i, false))) < 7) { part = 1; return i; }
                    if (i < keys.Length - 1 && Vector2.Distance(mp, ToGUI(HandlePos(i, true))) < 7) { part = 2; return i; }
                }
                return -1;
            }

            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown when r.Contains(e.mousePosition):
                {
                    int k = Pick(e.mousePosition, out int part);
                    if (e.button == 1)
                    {
                        var menu = new GenericMenu();
                        var curveCopy = curve;
                        if (k > 0 && k < keys.Length - 1 && part == 0)
                        {
                            int kk = k;
                            menu.AddItem(new GUIContent("点を削除"), false, () =>
                            {
                                Undo.RecordObject(undoTarget, "カーブ点を削除");
                                curveCopy.RemoveKey(kk); GUI.changed = true;
                                EditorUtility.SetDirty(undoTarget);
                            });
                        }
                        menu.AddItem(new GUIContent("リセット/直線"), false, () =>
                        {
                            Undo.RecordObject(undoTarget, "カーブをリセット");
                            curveCopy.keys = AnimationCurve.Linear(0, 0, 1, 1).keys; EditorUtility.SetDirty(undoTarget);
                        });
                        menu.AddItem(new GUIContent("プリセット/イーズイン"), false, () =>
                        {
                            Undo.RecordObject(undoTarget, "カーブプリセット");
                            curveCopy.keys = new[] { new Keyframe(0, 0, 0, 0), new Keyframe(1, 1, 2, 2) }; EditorUtility.SetDirty(undoTarget);
                        });
                        menu.AddItem(new GUIContent("プリセット/イーズアウト"), false, () =>
                        {
                            Undo.RecordObject(undoTarget, "カーブプリセット");
                            curveCopy.keys = new[] { new Keyframe(0, 0, 2, 2), new Keyframe(1, 1, 0, 0) }; EditorUtility.SetDirty(undoTarget);
                        });
                        menu.AddItem(new GUIContent("プリセット/S字"), false, () =>
                        {
                            Undo.RecordObject(undoTarget, "カーブプリセット");
                            curveCopy.keys = AnimationCurve.EaseInOut(0, 0, 1, 1).keys; EditorUtility.SetDirty(undoTarget);
                        });
                        menu.ShowAsContext();
                        e.Use();
                        break;
                    }
                    if (e.button != 0) break;
                    if (k < 0 && e.clickCount == 2)
                    {
                        Undo.RecordObject(undoTarget, "カーブ点を追加");
                        var p = ToCurve(e.mousePosition);
                        p.x = Mathf.Clamp(p.x, 0.001f, 0.999f);
                        float slope = (curve.Evaluate(p.x + 0.01f) - curve.Evaluate(p.x - 0.01f)) / 0.02f;
                        int ni = curve.AddKey(new Keyframe(p.x, p.y, slope, slope, 1f / 3, 1f / 3) { weightedMode = WeightedMode.Both });
                        keys = curve.keys;
                        bzKey = ni; bzPart = 0;
                        changed = true;
                    }
                    else if (k >= 0)
                    {
                        Undo.RecordObject(undoTarget, "カーブ編集");
                        bzKey = k; bzPart = part;
                    }
                    else break;
                    GUIUtility.hotControl = id;
                    e.Use();
                    break;
                }
                case EventType.MouseDrag when GUIUtility.hotControl == id && bzKey >= 0 && bzKey < keys.Length:
                {
                    var p = ToCurve(e.mousePosition);
                    if (e.shift) { p.x = Mathf.Round(p.x * 20) / 20; p.y = Mathf.Round(p.y * 20) / 20; }
                    var k = keys[bzKey];
                    if (bzPart == 0)
                    {
                        // 両端の x は固定
                        float minX = bzKey == 0 ? 0 : keys[bzKey - 1].time + 0.01f;
                        float maxX = bzKey == keys.Length - 1 ? 1 : keys[bzKey + 1].time - 0.01f;
                        k.time = (bzKey == 0 || bzKey == keys.Length - 1) ? k.time : Mathf.Clamp(p.x, minX, maxX);
                        k.value = Mathf.Clamp(p.y, yMin, yMax);
                    }
                    else if (bzPart == 2)
                    {
                        float seg = keys[bzKey + 1].time - k.time;
                        float dx = Mathf.Clamp(p.x - k.time, 0.001f, seg);
                        k.outTangent = (p.y - k.value) / dx;
                        k.outWeight = dx / seg;
                        if (!e.alt && bzKey > 0) k.inTangent = k.outTangent; // Alt で折れ線 (ハンドル分離)
                    }
                    else
                    {
                        float seg = k.time - keys[bzKey - 1].time;
                        float dx = Mathf.Clamp(k.time - p.x, 0.001f, seg);
                        k.inTangent = (k.value - p.y) / dx;
                        k.inWeight = dx / seg;
                        if (!e.alt && bzKey < keys.Length - 1) k.outTangent = k.inTangent;
                    }
                    k.weightedMode = WeightedMode.Both;
                    keys[bzKey] = k;
                    changed = true;
                    e.Use();
                    break;
                }
                case EventType.MouseUp when GUIUtility.hotControl == id:
                    GUIUtility.hotControl = 0; bzKey = -1; e.Use(); break;
            }

            if (changed)
            {
                curve = new AnimationCurve(keys);
                EditorUtility.SetDirty(undoTarget);
            }
            return changed;
        }
    }
}
