using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRCFTMapper.Editor
{
    /// <summary>
    /// 同じ入力 (フェイストラッキングパラメータ / 式) を使うマッピングをグループ化し、
    /// 統合グラフ・割合バー・スタイルタブ・連結ドラッグ・録画の分布表示で一括編集する。
    /// </summary>
    public partial class VRCFTMapperWindow
    {
        const string ExprGroup = "ƒ 式入力";
        readonly HashSet<string> groupOpen = new HashSet<string>();
        readonly HashSet<string> groupLinked = new HashSet<string>();
        readonly HashSet<string> groupNoSum = new HashSet<string>();
        FTMapping hotMapping, hotNext;
        Color? rowSwatch;

        // ドラッグ状態
        FTMapping dragMapping;
        int dragEnd;          // 0 = in 下, 1 = in 上
        float dragOld;
        string dragGroup;
        bool scrubbing;

        // 録画の分布キャッシュ
        OscRecording coverageClip;
        readonly Dictionary<string, float[]> coverage = new Dictionary<string, float[]>();
        const int CoverageBins = 40;

        static readonly Color[] Palette =
        {
            new Color(0.36f, 0.70f, 1.00f), new Color(1.00f, 0.55f, 0.30f), new Color(0.45f, 0.85f, 0.45f),
            new Color(0.85f, 0.50f, 0.95f), new Color(1.00f, 0.85f, 0.30f), new Color(0.35f, 0.90f, 0.85f),
            new Color(1.00f, 0.45f, 0.60f), new Color(0.70f, 0.70f, 0.70f),
        };
        static readonly Color GraphBg = new Color(0.12f, 0.12f, 0.14f);

        static string GroupKey(FTMapping m) => m.useExpression ? ExprGroup : string.IsNullOrEmpty(m.parameter) ? "(未設定)" : m.parameter;

        IEnumerable<string> AllGroupKeys() => mapper.mappings.Select(GroupKey).Distinct();

        /// <summary>マッピング一覧。2 件以上の入力はグループ表示、1 件なら従来の行。</summary>
        void DrawGroups()
        {
            wantsMouseMove = true;
            string f = (filter ?? "").ToLowerInvariant();
            var order = new List<string>();
            var byKey = new Dictionary<string, List<int>>();
            for (int i = 0; i < mapper.mappings.Count; i++)
            {
                var m = mapper.mappings[i];
                if (f.Length > 0 && !(m.parameter ?? "").ToLowerInvariant().Contains(f) && !(m.blendShape ?? "").ToLowerInvariant().Contains(f)
                    && !(m.expression ?? "").ToLowerInvariant().Contains(f)) continue;
                var k = GroupKey(m);
                if (!byKey.TryGetValue(k, out var l)) { byKey[k] = l = new List<int>(); order.Add(k); }
                l.Add(i);
            }
            if (Event.current.type == EventType.Repaint) hotNext = null;
            foreach (var k in order)
            {
                var idx = byKey[k];
                if (idx.Count == 1 && k != ExprGroup) { rowSwatch = null; if (DrawRowTracked(idx[0])) return; }
                else if (DrawGroup(k, idx)) return;
            }
            rowSwatch = null;
            if (Event.current.type == EventType.Repaint && hotNext != hotMapping) { hotMapping = hotNext; Repaint(); }
            if (Event.current.type == EventType.MouseMove) Repaint();
        }

        bool DrawRowTracked(int i)
        {
            if (DrawMapping(i, mapper.mappings[i])) return true;
            if (Event.current.type == EventType.Repaint && GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
                hotNext = mapper.mappings[i];
            return false;
        }

        bool DrawGroup(string key, List<int> idx)
        {
            var ms = idx.Select(i => mapper.mappings[i]).ToList();
            bool isExpr = key == ExprGroup;
            var spec = isExpr ? null : FTCatalog.Find(key);
            bool signedView = ms.Any(m => m.signed) || (spec != null && spec.signed);
            float x0 = signedView ? -1 : 0;
            float input = InputFor(ms[0], out bool external);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                // ---- ヘッダ (パラメータ単位) ----
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool open = groupOpen.Contains(key);
                    bool nopen = EditorGUILayout.Foldout(open, new GUIContent(key, spec != null ? spec.label : isExpr ? "式を入力にするマッピング" : "カタログ外の名前"), true, EditorStyles.foldoutHeader);
                    if (nopen != open) { if (nopen) groupOpen.Add(key); else groupOpen.Remove(key); }
                    GUILayout.FlexibleSpace();
                    var dots = string.Concat(ms.Select(m => m.enabled ? "●" : "○"));
                    GUILayout.Label(new GUIContent($"{dots} {ms.Count}件", "有効 ● / 無効 ○"), EditorStyles.miniLabel);
                    if (!isExpr) GUILayout.Label($"{ms.Max(FTMath.SyncBits)}bit", EditorStyles.miniLabel, GUILayout.Width(30));
                    var mini = GUILayoutUtility.GetRect(96, 18, GUILayout.Width(96));
                    if (Event.current.type == EventType.Repaint) DrawGroupCurves(mini, key, ms, x0, input, true, isExpr);
                    if (!isExpr) GUILayout.Label($"{input:0.00}", EditorStyles.miniLabel, GUILayout.Width(30));
                }
                if (!groupOpen.Contains(key)) return false;

                // ---- (3) スタイルタブ ----
                var sg = mapper.styleGroup;
                if (sg.enabled && ms.Any(m => m.style > 0))
                {
                    var labels = new[] { sg.auto ? "自動" : "—" }.Concat(sg.options).ToArray();
                    int tab = GUILayout.Toolbar(Mathf.Clamp(previewStyle, 0, labels.Length - 1), labels, EditorStyles.miniButton);
                    if (tab != previewStyle) { previewStyle = tab; ApplyPreview(); }
                    if (tab == 0 && sg.auto)
                    {
                        EditorGUI.BeginChangeCheck();
                        previewStyleAuto = EditorGUILayout.Slider(new GUIContent("自動の式の値", sg.autoExpression), previewStyleAuto, -1, 1);
                        if (EditorGUI.EndChangeCheck()) ApplyPreview();
                    }
                }

                // ---- (1) 統合グラフ ----
                var gr = GUILayoutUtility.GetRect(10, 150, GUILayout.ExpandWidth(true));
                HandleGraphInput(gr, key, ms, x0, external, isExpr);
                if (Event.current.type == EventType.Repaint) DrawGroupCurves(gr, key, ms, x0, input, false, isExpr);

                // ---- (2) 割合バー ----
                var br = GUILayoutUtility.GetRect(10, 18, GUILayout.ExpandWidth(true));
                if (Event.current.type == EventType.Repaint) DrawShareBar(br, ms, isExpr);

                // ---- (4) ツール ----
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool linked = groupLinked.Contains(key);
                    bool nl = GUILayout.Toggle(linked, new GUIContent("⛓ 隣と連結", "つなぎ目 (片方の in 上と隣の in 下が近い点) を一緒にドラッグ"), EditorStyles.miniButtonLeft, GUILayout.Width(80));
                    if (nl != linked) { if (nl) groupLinked.Add(key); else groupLinked.Remove(key); }
                    bool sum = !groupNoSum.Contains(key);
                    bool ns = GUILayout.Toggle(sum, new GUIContent("合計線", "表示中のシェイプの合計 (100% 超は赤)"), EditorStyles.miniButtonMid, GUILayout.Width(50));
                    if (ns != sum) { if (ns) groupNoSum.Remove(key); else groupNoSum.Add(key); }
                    if (GUILayout.Button(new GUIContent("つなぎ目を揃える", "in 範囲の端どうしが 0.1 以内で離れている/重なっている箇所を中点に揃える"), EditorStyles.miniButtonRight, GUILayout.Width(100)))
                        AlignSeams(ms);
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(external ? "OSC/再生中: カーソルは実入力" : "グラフをクリック/ドラッグで入力を試せます", EditorStyles.miniLabel);
                    if (GUILayout.Button(new GUIContent("＋ シェイプ", "この入力にシェイプキーを追加"), EditorStyles.miniButton, GUILayout.Width(70)))
                    {
                        Undo.RecordObject(mapper, "シェイプを追加");
                        var c = ms[ms.Count - 1].Clone();
                        c.blendShape = "";
                        int at = idx[idx.Count - 1] + 1;
                        mapper.mappings.Insert(at, c);
                        foldout.Add(at);
                        Dirty();
                        return true;
                    }
                }

                // ---- 各行 (シェイプ単位) ----
                for (int j = 0; j < idx.Count; j++)
                {
                    rowSwatch = Palette[j % Palette.Length];
                    if (DrawRowTracked(idx[j])) { rowSwatch = null; return true; }
                }
                rowSwatch = null;
            }
            return false;
        }

        // ------------------------------------------------------------
        float Vis(FTMapping m) => m.enabled ? StyleWeightPreview(m) : 0;

        void YRange(List<FTMapping> ms, out float y0, out float y1)
        {
            y0 = Mathf.Min(0, ms.Min(m => Mathf.Min(m.outMin, m.outMax)));
            y1 = Mathf.Max(1, ms.Max(m => Mathf.Max(m.outMin, m.outMax)));
            if (!groupNoSum.Contains(GroupKey(ms[0]))) y1 = Mathf.Max(y1, 1.25f);
        }

        static Vector2 P(Rect r, float x, float y, float x0, float y0, float y1)
            => new Vector2(Mathf.Lerp(r.xMin, r.xMax, Mathf.InverseLerp(x0, 1, x)),
                           Mathf.Lerp(r.yMax, r.yMin, Mathf.Clamp01(Mathf.InverseLerp(y0, y1, y))));

        void DrawGroupCurves(Rect r, string key, List<FTMapping> ms, float x0, float input, bool mini, bool isExpr)
        {
            EditorGUI.DrawRect(r, GraphBg);
            YRange(ms, out float y0, out float y1);
            if (mini) { y0 = Mathf.Min(0, y0); y1 = Mathf.Max(1, Mathf.Min(y1, 1.25f)); }
            const int N = 64;

            if (!mini)
            {
                // (5) 録画の分布 (下地)
                var cov = isExpr ? null : Coverage(key);
                if (cov != null)
                {
                    float mx = cov.Max();
                    for (int b = 0; b < CoverageBins; b++)
                    {
                        if (cov[b] <= 0) continue;
                        float a = Mathf.Lerp(x0, 1, b / (float)CoverageBins), c = Mathf.Lerp(x0, 1, (b + 1) / (float)CoverageBins);
                        var pa = P(r, a, 0, x0, 0, 1); var pc = P(r, c, 0, x0, 0, 1);
                        float h = r.height * 0.9f * Mathf.Sqrt(cov[b] / mx);
                        EditorGUI.DrawRect(new Rect(pa.x, r.yMax - h, pc.x - pa.x, h), new Color(1, 1, 1, 0.07f));
                    }
                }
                // グリッド
                var grid = new Color(1, 1, 1, 0.08f);
                foreach (var y in new[] { 0f, 0.5f, 1f })
                {
                    var p = P(r, x0, y, x0, y0, y1);
                    EditorGUI.DrawRect(new Rect(r.xMin, p.y, r.width, 1), y == 1f || y == 0f ? new Color(1, 1, 1, 0.18f) : grid);
                }
                for (float x = x0; x <= 1.001f; x += 0.25f)
                    EditorGUI.DrawRect(new Rect(P(r, x, 0, x0, y0, y1).x, r.yMin, 1, r.height), grid);
                GUI.Label(new Rect(r.xMin + 2, P(r, 0, 1, x0, y0, y1).y - 14, 40, 14), "100%", EditorStyles.miniLabel);
            }

            // 表示中でない (別スタイル) → 灰色の細線、表示中 → 色付き
            var pts = new Vector3[N + 1];
            for (int pass = 0; pass < 2; pass++)
                for (int k = 0; k < ms.Count; k++)
                {
                    var m = ms[k];
                    float w = Vis(m);
                    bool vis = w > 0.001f;
                    if ((pass == 0) == vis) continue; // 非表示を先に、表示を後に描く
                    for (int s = 0; s <= N; s++)
                    {
                        float x = Mathf.Lerp(x0, 1, s / (float)N);
                        pts[s] = P(r, x, FTMath.Evaluate(m, x), x0, y0, y1);
                    }
                    var col = vis ? Palette[k % Palette.Length] : new Color(0.5f, 0.5f, 0.5f);
                    float width = mini ? 1.5f : vis ? 2.5f : 1.2f;
                    if (hotMapping != null && !mini) { if (hotMapping == m) width = 4.5f; else col.a *= 0.4f; }
                    if (vis) col.a *= Mathf.Lerp(0.35f, 1f, w);
                    else col.a *= 0.5f;
                    Handles.color = col;
                    Handles.DrawAAPolyLine(width, pts);
                }

            // 合計線
            if (!groupNoSum.Contains(key) && ms.Count(m => Vis(m) > 0) >= 2)
            {
                for (int s = 0; s < N; s++)
                {
                    float xa = Mathf.Lerp(x0, 1, s / (float)N), xb = Mathf.Lerp(x0, 1, (s + 1) / (float)N);
                    float sa = ms.Sum(m => FTMath.Evaluate(m, xa) * Vis(m)), sb = ms.Sum(m => FTMath.Evaluate(m, xb) * Vis(m));
                    Handles.color = Mathf.Max(sa, sb) > 1.02f ? new Color(1f, 0.3f, 0.3f, 0.9f) : new Color(1, 1, 1, mini ? 0.35f : 0.5f);
                    Handles.DrawAAPolyLine(mini ? 1f : 1.5f, P(r, xa, sa, x0, y0, y1), P(r, xb, sb, x0, y0, y1));
                }
            }

            // 現在の入力
            if (!isExpr)
            {
                var c = P(r, input, 0, x0, y0, y1);
                EditorGUI.DrawRect(new Rect(c.x, r.yMin, 1, r.height), FTControls.Marker);
            }

            if (mini) return;
            // in ハンドル (青 = 下, 赤 = 上)
            for (int k = 0; k < ms.Count; k++)
            {
                var m = ms[k];
                if (Vis(m) <= 0.001f) continue;
                for (int e = 0; e < 2; e++)
                {
                    float x = e == 0 ? m.inMin : m.inMax;
                    var p = P(r, x, FTMath.Evaluate(m, x), x0, y0, y1);
                    var hr = new Rect(p.x - 5, p.y - 5, 10, 10);
                    EditorGUI.DrawRect(new Rect(hr.x - 1, hr.y - 1, hr.width + 2, hr.height + 2), Palette[k % Palette.Length]);
                    EditorGUI.DrawRect(hr, e == 0 ? FTControls.Blue : FTControls.Red);
                }
            }
            // ホバー中の曲線名
            if (hotMapping != null && ms.Contains(hotMapping) && r.Contains(Event.current.mousePosition))
            {
                var mp = Event.current.mousePosition;
                var lab = new GUIContent($"{hotMapping.blendShape}  {FTMath.Evaluate(hotMapping, Mathf.Lerp(x0, 1, Mathf.InverseLerp(r.xMin, r.xMax, mp.x))) * 100:0}%");
                var sz = EditorStyles.helpBox.CalcSize(lab);
                GUI.Label(new Rect(Mathf.Min(mp.x + 12, r.xMax - sz.x), mp.y - 20, sz.x, sz.y), lab, EditorStyles.helpBox);
            }
        }

        void HandleGraphInput(Rect r, string key, List<FTMapping> ms, float x0, bool external, bool isExpr)
        {
            var ev = Event.current;
            int id = GUIUtility.GetControlID(FocusType.Passive, r);
            YRange(ms, out float y0, out float y1);
            float MouseX() => Mathf.Lerp(x0, 1, Mathf.InverseLerp(r.xMin, r.xMax, ev.mousePosition.x));

            switch (ev.GetTypeForControl(id))
            {
                case EventType.MouseMove:
                case EventType.Repaint:
                    if (!r.Contains(ev.mousePosition)) break;
                    {
                        // 最寄りの曲線をホバー
                        float x = MouseX(); FTMapping best = null; float bd = 8;
                        foreach (var m in ms)
                        {
                            float d = Mathf.Abs(P(r, x, FTMath.Evaluate(m, x), x0, y0, y1).y - ev.mousePosition.y);
                            if (Vis(m) > 0.001f && d < bd) { bd = d; best = m; }
                        }
                        if (best != null) hotNext = best;
                        if (ev.type == EventType.MouseMove) Repaint();
                    }
                    break;

                case EventType.MouseDown:
                    if (ev.button != 0 || !r.Contains(ev.mousePosition)) break;
                    {
                        dragMapping = null;
                        float bd = 8;
                        foreach (var m in ms)
                        {
                            if (Vis(m) <= 0.001f) continue;
                            for (int e = 0; e < 2; e++)
                            {
                                float x = e == 0 ? m.inMin : m.inMax;
                                float d = Vector2.Distance(P(r, x, FTMath.Evaluate(m, x), x0, y0, y1), ev.mousePosition);
                                if (d < bd) { bd = d; dragMapping = m; dragEnd = e; dragOld = x; }
                            }
                        }
                        if (dragMapping != null)
                        {
                            Undo.RecordObject(mapper, "in 範囲の変更 (グラフ)");
                            dragGroup = key;
                        }
                        else if (!external && !isExpr) { scrubbing = true; Scrub(ms, MouseX()); }
                        else break;
                        GUIUtility.hotControl = id;
                        ev.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != id) break;
                    if (dragMapping != null)
                    {
                        float x = Mathf.Clamp(MouseX(), x0, 1);
                        if (ev.shift) x = Mathf.Round(x / 0.05f) * 0.05f;
                        var m = dragMapping;
                        if (dragEnd == 0) x = Mathf.Min(x, m.inMax - 0.01f); else x = Mathf.Max(x, m.inMin + 0.01f);
                        if (groupLinked.Contains(dragGroup))
                            foreach (var o in ms)
                            {
                                if (o == m || Vis(o) <= 0.001f) continue;
                                if (dragEnd == 1 && Mathf.Abs(o.inMin - dragOld) < 0.02f) o.inMin = Mathf.Min(x, o.inMax - 0.01f);
                                if (dragEnd == 0 && Mathf.Abs(o.inMax - dragOld) < 0.02f) o.inMax = Mathf.Max(x, o.inMin + 0.01f);
                            }
                        if (dragEnd == 0) m.inMin = x; else m.inMax = x;
                        dragOld = x;
                        Dirty(); ApplyPreview();
                    }
                    else if (scrubbing) Scrub(ms, MouseX());
                    ev.Use();
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl != id) break;
                    GUIUtility.hotControl = 0;
                    dragMapping = null; scrubbing = false;
                    ev.Use();
                    break;
            }
        }

        void Scrub(List<FTMapping> ms, float x)
        {
            foreach (var m in ms) manual[ManualKey(m)] = x;
            ApplyPreview(); Repaint();
        }

        void DrawShareBar(Rect r, List<FTMapping> ms, bool isExpr)
        {
            EditorGUI.DrawRect(r, GraphBg);
            var vals = ms.Select(m => FTMath.Evaluate(m, InputFor(m, out _)) * Vis(m)).ToList();
            float sum = vals.Where(v => v > 0).Sum();
            float full = Mathf.Max(1.25f, sum);
            float scale = (r.width - 60) / full;
            float x = r.xMin;
            for (int k = 0; k < ms.Count; k++)
            {
                float v = vals[k];
                if (v <= 0.001f) continue;
                float w = v * scale;
                var seg = new Rect(x, r.yMin + 2, w, r.height - 4);
                var col = Palette[k % Palette.Length]; if (hotMapping != null && hotMapping != ms[k]) col.a = 0.5f;
                EditorGUI.DrawRect(seg, col);
                var lab = new GUIContent($"{ms[k].blendShape} {v * 100:0}");
                if (EditorStyles.miniLabel.CalcSize(lab).x < w) GUI.Label(seg, lab, EditorStyles.miniLabel);
                x += w;
            }
            EditorGUI.DrawRect(new Rect(r.xMin + scale, r.yMin, 1, r.height), Color.white); // 100%
            var st = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleRight };
            st.normal.textColor = sum > 1.02f ? FTControls.Red : sum < 0.01f ? Color.gray : Color.white;
            GUI.Label(new Rect(r.xMax - 58, r.yMin, 56, r.height), $"合計 {sum * 100:0}%", st);
        }

        void AlignSeams(List<FTMapping> ms)
        {
            var vis = ms.Where(m => Vis(m) > 0.001f).OrderBy(m => m.inMin).ToList();
            Undo.RecordObject(mapper, "つなぎ目を揃える");
            int n = 0;
            for (int i = 0; i < vis.Count - 1; i++)
            {
                var a = vis[i]; var b = vis[i + 1];
                float d = b.inMin - a.inMax;
                if (Mathf.Abs(d) < 0.0005f || Mathf.Abs(d) > 0.1f) continue;
                float mid = (a.inMax + b.inMin) * 0.5f;
                a.inMax = Mathf.Max(mid, a.inMin + 0.01f); b.inMin = Mathf.Min(mid, b.inMax - 0.01f);
                n++;
            }
            ShowNotification(new GUIContent(n > 0 ? $"{n} 箇所のつなぎ目を揃えました" : "揃える箇所はありません (0.1 以内の隙間/重なりのみ対象)"));
            if (n > 0) { Dirty(); ApplyPreview(); }
        }

        /// <summary>(5) 再生中の録画で、この入力がどの値をどれだけ取ったか。</summary>
        float[] Coverage(string param)
        {
            var clip = osc.clip;
            if (clip == null || clip.samples == null || clip.samples.Count == 0) return null;
            if (clip != coverageClip) { coverage.Clear(); coverageClip = clip; }
            if (coverage.TryGetValue(param, out var c)) return c;
            var spec = FTCatalog.Find(param);
            float x0 = spec != null && spec.signed ? -1 : 0;
            c = new float[CoverageBins];
            var sfx = "/" + param;
            bool any = false;
            foreach (var s in clip.samples)
            {
                if (s.address != param && !s.address.EndsWith(sfx)) continue;
                int b = Mathf.Clamp(Mathf.FloorToInt(Mathf.InverseLerp(x0, 1, s.value) * CoverageBins), 0, CoverageBins - 1);
                c[b]++; any = true;
            }
            coverage[param] = any ? c : null;
            return coverage[param];
        }
    }
}
