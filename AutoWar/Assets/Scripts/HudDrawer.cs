using System;
using System.Collections.Generic;
using UnityEngine;

namespace AutoWar
{
    // ========================
    // HUD DRAWER
    // The whole interface, drawn with IMGUI: top bar, mission card + minimap, hint pill,
    // camera buttons, and the bottom dock (block palette, drag-and-drop program, typed code).
    // Layout is in "logical" units (screen pixels / _ui) so it scales with the window.
    // ========================
    public class HudDrawer : MonoBehaviour
    {
        GameRunner _g;
        float _ui = 1, _w, _h;

        // ---------- palette of colours ----------
        static readonly Color Ink = Hx("#e8edf7"), Muted = Hx("#8392b3"), Gold = Hx("#ffc857"), Line = Hx("#243556");
        static Color Hx(string h, float a = 1f) { var c = TerrainPainter.Hex(h); c.a = a; return c; }

        // ---------- GUI plumbing ----------
        Texture2D _white;
        GUIStyle _txt, _mono;
        Font _font, _monoFont;
        bool _ready;

        // ---------- panel state ----------
        bool _panelOpen = true, _codeOpen, _statsOpen, _musicOn = true, _sfxOn = true;
        float _scroll, _progContentH, _progViewH;
        float _copiedUntil;
        bool _miniDrag;
        Rect _miniRect;

        // ---------- drag and drop ----------
        class Zone { public Rect rect; public List<Node> list; public int beforeId; public Node ifNode; }
        class Src { public Rect rect; public string payload, label; public bool palette; }
        List<Zone> _zones = new List<Zone>(), _zonesNext = new List<Zone>();
        List<Src> _srcs = new List<Src>(), _srcsNext = new List<Src>();
        List<Rect> _uiRects = new List<Rect>(), _uiRectsNext = new List<Rect>();
        Src _cand; Vector2 _candStart; bool _dragging;
        readonly List<Action> _pending = new List<Action>();
        Vector2 _origin; Rect _clip;   // coordinate offset and clip while drawing inside a group

        public void Init(GameRunner g) { _g = g; }

        // -------------------------------------------------------
        // IsPointerOverUi()
        // True when the mouse is over any HUD panel (so the camera shouldn't pan or zoom). Uses the last drawn frame.
        // -------------------------------------------------------
        public bool IsPointerOverUi()
        {
            var m = Input.mousePosition; m.y = Screen.height - m.y;
            var p = new Vector2(m.x / _ui, m.y / _ui);
            foreach (var r in _uiRects) if (r.Contains(p)) return true;
            return false;
        }

        // ========================
        // SMALL DRAW HELPERS
        // ========================
        void EnsureStyles()
        {
            if (_ready) return;
            _white = Texture2D.whiteTexture;
            _font = Font.CreateDynamicFontFromOSFont(new[] { "Helvetica Neue", "Segoe UI", "Arial" }, 16);
            _monoFont = Font.CreateDynamicFontFromOSFont(new[] { "Menlo", "Consolas", "Courier New" }, 13);
            _txt = new GUIStyle(GUI.skin.label) { font = _font, richText = false, clipping = TextClipping.Clip, padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0) };
            _mono = new GUIStyle(GUI.skin.textArea) { font = _monoFont, fontSize = 13, wordWrap = false, padding = new RectOffset(8, 8, 4, 4) };
            foreach (var st in new[] { _mono.normal, _mono.hover, _mono.active, _mono.focused, _mono.onNormal, _mono.onHover, _mono.onActive, _mono.onFocused })
            { st.background = null; st.textColor = Hx("#cfe3ff"); }
            GUI.skin.settings.cursorColor = Color.white;
            _ready = true;
        }

        void Fill(Rect r, Color c, float radius = 0) { GUI.DrawTexture(r, _white, ScaleMode.StretchToFill, true, 0, c, 0, radius); }
        void Outline(Rect r, Color c, float width = 1, float radius = 0) { GUI.DrawTexture(r, _white, ScaleMode.StretchToFill, true, 0, c, width, radius); }

        void Text(Rect r, string s, int size, Color col, TextAnchor a = TextAnchor.MiddleLeft, FontStyle fs = FontStyle.Normal, bool wrap = false)
        {
            _txt.fontSize = size; _txt.alignment = a; _txt.fontStyle = fs; _txt.wordWrap = wrap; _txt.normal.textColor = col;
            GUI.Label(r, s, _txt);
        }
        float TextWidth(string s, int size, FontStyle fs = FontStyle.Normal)
        {
            _txt.fontSize = size; _txt.fontStyle = fs; _txt.wordWrap = false;
            return _txt.CalcSize(new GUIContent(s)).x;
        }

        bool Hover(Rect r) { return r.Contains(Event.current.mousePosition); }

        // A clickable rounded button. Fill/border/text colours are supplied by the caller.
        bool Btn(Rect r, string label, Color bg, Color border, Color fg, int size = 13, FontStyle fs = FontStyle.Bold, float alpha = 1f)
        {
            var b = Hover(r) ? Color.Lerp(bg, Color.white, 0.12f) : bg;
            b.a *= alpha;
            Fill(r, b, 8);
            if (border.a > 0) Outline(r, new Color(border.r, border.g, border.b, border.a * alpha), 1, 8);
            Text(r, label, size, new Color(fg.r, fg.g, fg.b, fg.a * alpha), TextAnchor.MiddleCenter, fs);
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        // ---------- drop / drag recording (only on Repaint) ----------
        bool Rec { get { return Event.current.type == EventType.Repaint; } }
        Rect Abs(Rect local)
        {
            var a = new Rect(local.x + _origin.x, local.y + _origin.y, local.width, local.height);
            float x0 = Mathf.Max(a.xMin, _clip.xMin), y0 = Mathf.Max(a.yMin, _clip.yMin), x1 = Mathf.Min(a.xMax, _clip.xMax), y1 = Mathf.Min(a.yMax, _clip.yMax);
            return x1 > x0 && y1 > y0 ? new Rect(x0, y0, x1 - x0, y1 - y0) : Rect.zero;
        }
        void AddZone(Rect local, List<Node> list, int beforeId, Node ifNode)
        {
            if (!Rec) return;
            var a = Abs(local); if (a.width <= 0) return;
            _zonesNext.Add(new Zone { rect = a, list = list, beforeId = beforeId, ifNode = ifNode });
        }
        void AddSrc(Rect local, string payload, string label, bool palette)
        {
            if (!Rec) return;
            var a = Abs(local); if (a.width <= 0) return;
            _srcsNext.Add(new Src { rect = a, payload = payload, label = label, palette = palette });
        }
        void AddUiRect(Rect r) { if (Rec) _uiRectsNext.Add(r); }

        // ========================
        // MAIN DRAW
        // ========================
        void OnGUI()
        {
            if (_g == null || _g.sim == null || _g.sim.map == null) return;
            EnsureStyles();
            var e = Event.current;
            _ui = Mathf.Clamp(Screen.height / 820f, 1f, 2.2f);
            _w = Screen.width / _ui; _h = Screen.height / _ui;
            GUI.matrix = Matrix4x4.Scale(new Vector3(_ui, _ui, 1));
            _origin = Vector2.zero; _clip = new Rect(0, 0, _w, _h);
            if (e.type == EventType.Repaint) { _zonesNext = new List<Zone>(); _srcsNext = new List<Src>(); _uiRectsNext = new List<Rect>(); }

            var sim = _g.sim;
            bool narrow = _w < 640;
            const float TopH = 46, DockH = 300;
            float dockPx = _panelOpen ? DockH + 24 : 46;
            float infoW = 240;
            float dockX = narrow ? 12 : infoW + 24;
            float dockW = _w - 12 - dockX;
            float dockY = _h - 12 - DockH;

            // keep the camera's free area in sync with the HUD
            var safe = new Rect(0, (TopH - 8) * _ui, _w * _ui, Mathf.Max(200, _h - TopH - dockPx - 44) * _ui);
            _g.view.SetSafeArea(safe, _ui, _g.needsViewReset);
            _g.needsViewReset = false;

            DrawTopBar(TopH);
            if (_g.view.camEnabled) DrawCameraButtons(TopH);
            if (_statsOpen) DrawStats(TopH + (_g.view.camEnabled ? 36 : 4));
            if (!narrow) DrawInfoCard(infoW);
            DrawHint(safe.center.x / _ui, _h - dockPx - 4 - 34);

            if (_panelOpen) DrawDock(new Rect(dockX, dockY, dockW, DockH));
            var tab = new Rect(dockX + dockW / 2 - 60, _panelOpen ? dockY - 30 : _h - 30, 120, 30);
            AddUiRect(tab);
            if (Btn(tab, _panelOpen ? "v Program" : "^ Program", Hx("#14203a"), Hx("#2a4570"), Ink, 13, FontStyle.Bold)) _panelOpen = !_panelOpen;

            HandleMouse(e);
            if (_dragging && _cand != null) DrawDragFeedback(e.mousePosition);

            if (e.type == EventType.Repaint) { _zones = _zonesNext; _srcs = _srcsNext; _uiRects = _uiRectsNext; }
            if (e.type != EventType.Layout && _pending.Count > 0)
            {
                var todo = new List<Action>(_pending);
                _pending.Clear();
                foreach (var a in todo) a();
            }
        }

        // ---------- top bar ----------
        void DrawTopBar(float topH)
        {
            var sim = _g.sim;
            Fill(new Rect(0, 0, _w, topH - 6), new Color(5 / 255f, 8 / 255f, 16 / 255f, 0.9f));
            Fill(new Rect(0, topH - 6, _w, 6), new Color(5 / 255f, 8 / 255f, 16 / 255f, 0.45f));
            AddUiRect(new Rect(0, 0, _w, topH));

            float x = 16;
            string[] keys = { "field", "fog" }; string[] names = { "Secured Field", "Fog of War" };
            for (int i = 0; i < 2; i++)
            {
                bool on = _g.levelKey == keys[i];
                float tw = TextWidth(names[i], 14, FontStyle.Bold) + 52;
                var r = new Rect(x, 8, tw, 30);
                var bg = on ? Hx("#6fd0ff") : new Color(1, 1, 1, 0.05f);
                if (Btn(r, "", bg, on ? Color.clear : Line, Ink) && !on) { string k = keys[i]; _pending.Add(() => _g.SetLevel(k, false)); }
                Fill(new Rect(x + 6, 12, 22, 22), on ? new Color(6 / 255f, 18 / 255f, 31 / 255f, 0.22f) : Line, 11);
                Text(new Rect(x + 6, 12, 22, 22), (i + 1).ToString(), 12, on ? Hx("#06121f") : Ink, TextAnchor.MiddleCenter, FontStyle.Bold);
                Text(new Rect(x + 36, 8, tw - 36, 30), names[i], 14, on ? Hx("#06121f") : Muted, TextAnchor.MiddleLeft, FontStyle.Bold);
                x += tw + 8;
            }
            x += 8;
            if (Btn(new Rect(x, 6, 92, 32), sim.running ? "Stop" : "Run", Hx("#5fb9f5"), Color.clear, Hx("#05121f"), 14)) _pending.Add(_g.Run);
            x += 100;
            if (Btn(new Rect(x, 6, 78, 32), "Reset", new Color(1, 1, 1, 0.07f), Line, Ink, 14)) _pending.Add(() => _g.ResetAndRender(false));

            // right side, laid out right to left
            float rx = _w - 16;
            rx -= 84; if (Btn(new Rect(rx, 8, 84, 30), "Sound", new Color(1, 1, 1, 0.05f), Line, Ink, 13, FontStyle.Bold, _sfxOn ? 1f : 0.45f)) { _sfxOn = !_sfxOn; _g.SetSfx(_sfxOn); }
            rx -= 92; if (Btn(new Rect(rx, 8, 84, 30), "Music", new Color(1, 1, 1, 0.05f), Line, Ink, 13, FontStyle.Bold, _musicOn ? 1f : 0.45f)) { _musicOn = !_musicOn; _g.SetMusic(_musicOn); }
            rx -= 72; if (Btn(new Rect(rx, 8, 64, 30), "Stats", new Color(1, 1, 1, 0.05f), Line, Ink, 13, FontStyle.Bold)) _statsOpen = !_statsOpen;
            string chip = sim.restored ? "Beacon restored" : sim.beaconSpotted ? "Beacon located" : "Beacon unknown";
            float cw = TextWidth(chip, 12) + 44;
            rx -= cw + 8;
            var cr = new Rect(rx, 9, cw, 28);
            Fill(cr, new Color(8 / 255f, 12 / 255f, 24 / 255f, 0.8f), 14);
            Outline(cr, new Color(120 / 255f, 160 / 255f, 1f, 0.25f), 1, 14);
            Color dot = sim.restored ? Gold : sim.beaconSpotted ? Hx("#7fd7ff") : Hx("#7d8aa6");
            Fill(new Rect(cr.x + 13, cr.y + 9.5f, 9, 9), dot, 4.5f);
            Text(new Rect(cr.x + 30, cr.y, cw - 30, 28), chip, 12, Hx("#d3def5"));
        }

        void DrawCameraButtons(float topH)
        {
            float x = _w - 16;
            x -= 44; var fit = new Rect(x, topH - 6, 44, 28);
            x -= 38; var zo = new Rect(x, topH - 6, 34, 28);
            x -= 38; var zi = new Rect(x, topH - 6, 34, 28);
            string hint = "Drag to pan · Scroll to zoom";
            float hw = TextWidth(hint, 12) + 28;
            x -= hw + 8; var hr = new Rect(x, topH - 6, hw, 28);
            Fill(hr, new Color(8 / 255f, 12 / 255f, 24 / 255f, 0.74f), 14);
            Text(hr, hint, 12, Hx("#d3def5"), TextAnchor.MiddleCenter);
            var bg = new Color(8 / 255f, 12 / 255f, 24 / 255f, 0.8f); var bd = new Color(120 / 255f, 160 / 255f, 1f, 0.3f); var fg = Hx("#d3def5");
            if (Btn(zi, "+", bg, bd, fg, 15)) _g.view.ZoomCenter(1.25f);
            if (Btn(zo, "−", bg, bd, fg, 15)) _g.view.ZoomCenter(1 / 1.25f);
            if (Btn(fit, "Fit", bg, bd, fg, 12)) _g.view.FitView();
            AddUiRect(new Rect(hr.x, hr.y, fit.xMax - hr.x, 28));
        }

        void DrawStats(float y)
        {
            var sim = _g.sim;
            var r = new Rect(_w - 16 - 220, y + 30, 220, 86);
            Fill(r, Hx("#0c1222", 0.96f), 12); Outline(r, Line, 1, 12);
            AddUiRect(r);
            string[] k = { "Explored", "Squad size", "Tiles walked" };
            string[] v = { sim.fullVision ? "Full coverage" : sim.exploredPct + "%", sim.units.Count.ToString(), sim.totalMoves.ToString() };
            for (int i = 0; i < 3; i++)
            {
                Text(new Rect(r.x + 14, r.y + 8 + i * 24, 110, 22), k[i], 13, Muted);
                Text(new Rect(r.x + 100, r.y + 8 + i * 24, 106, 22), v[i], 13, Ink, TextAnchor.MiddleRight, FontStyle.Bold);
            }
        }

        // ---------- mission card + minimap ----------
        void DrawInfoCard(float w)
        {
            var sim = _g.sim;
            var def = sim.level;
            _txt.fontSize = 13; _txt.wordWrap = true; _txt.fontStyle = FontStyle.Normal;
            float textH = _txt.CalcHeight(new GUIContent(def.mission), w - 24);
            bool mini = _g.view.camEnabled && _g.view.MiniTexture != null;
            float miniH = mini ? (w - 24) * sim.rows / sim.cols : 0;
            float h = 12 + 20 + textH + (mini ? 10 + miniH : 0) + 12;
            var r = new Rect(12, _h - 12 - h, w, h);
            Fill(r, Hx("#0c1426", 0.95f), 14); Outline(r, Hx("#2a4570"), 1, 14);
            AddUiRect(r);
            Text(new Rect(r.x + 12, r.y + 10, w - 24, 18), def.kicker.ToUpperInvariant(), 11, Gold, TextAnchor.MiddleLeft, FontStyle.Bold);
            Text(new Rect(r.x + 12, r.y + 32, w - 24, textH), def.mission, 13, Hx("#d6def0"), TextAnchor.UpperLeft, FontStyle.Normal, true);
            if (mini)
            {
                _miniRect = new Rect(r.x + 12, r.y + 32 + textH + 10, w - 24, miniH);
                GUI.DrawTexture(_miniRect, _g.view.MiniTexture, ScaleMode.StretchToFill);
                Outline(_miniRect, Hx("#78a0ff", 0.35f), 1, 6);
                var vf = _g.view.ViewFractions();
                Outline(new Rect(_miniRect.x + vf.x * _miniRect.width, _miniRect.y + vf.y * _miniRect.height, vf.width * _miniRect.width, vf.height * _miniRect.height), new Color(1, 1, 1, 0.85f), 1.5f);
            }
        }

        // ---------- hint pill ----------
        void DrawHint(float cx, float y)
        {
            string s = _g.hint;
            if (string.IsNullOrEmpty(s)) return;
            float tw = Mathf.Min(TextWidth(s, 14) + 44, _w - 40);
            var r = new Rect(cx - tw / 2, y, tw, 34);
            Fill(r, Hx("#0a0e1a", 0.9f), 14);
            Outline(r, Hx("#ffc857", 0.25f), 1, 14);
            Fill(new Rect(r.x + 14, r.y + 14, 6, 6), Gold, 3);
            Text(new Rect(r.x + 28, r.y, tw - 36, 34), s, 14, Gold);
            AddUiRect(r);
        }

        // ========================
        // DOCK
        // ========================
        void DrawDock(Rect d)
        {
            Fill(d, Hx("#0c1426", 0.96f), 14); Outline(d, Hx("#2a4570"), 1, 14);
            Fill(new Rect(d.x + 16, d.y, d.width - 32, 2), Hx("#4fc3ff", 0.6f));
            AddUiRect(d);

            float pad = 14, top = d.y + 10, ch = d.height - 20;
            float palW = 170, codeW = _codeOpen ? Mathf.Max(220, (d.width - 3 * pad - palW) * 0.4f) : 46;
            var palR = new Rect(d.x + pad, top, palW, ch);
            var codeR = new Rect(d.xMax - pad - codeW, top, codeW, ch);
            var progR = new Rect(palR.xMax + pad, top, codeR.x - pad - (palR.xMax + pad), ch);

            DrawPalette(palR);
            DrawProgram(progR);
            DrawCode(codeR);
        }

        static void BlockColors(NodeKind k, out Color bg, out Color bd, out Color fg)
        {
            if (k == NodeKind.Act) { bg = Hx("#143a55"); bd = Hx("#2f89b8"); fg = Hx("#c4ecff"); }
            else if (k == NodeKind.Cond) { bg = Hx("#3f3115"); bd = Hx("#b08a30"); fg = Hx("#ffe7ae"); }
            else { bg = Hx("#221c46"); bd = Hx("#6a58b8"); fg = Hx("#e0d6ff"); }
        }

        void DrawPalette(Rect r)
        {
            Text(new Rect(r.x, r.y, r.width, 18), "BLOCKS · DRAG OR TAP", 10, Muted, TextAnchor.MiddleLeft, FontStyle.Bold);
            float y = r.y + 24;
            foreach (var key in _g.sim.LevelBlocks())
            {
                var def = ProgramModel.Defs[key];
                var br = new Rect(r.x, y, r.width, 32);
                Color bg, bd, fg; BlockColors(def.kind, out bg, out bd, out fg);
                Fill(br, Hover(br) ? Color.Lerp(bg, Color.white, 0.1f) : bg, 8); Outline(br, bd, 1, 8);
                Text(new Rect(br.x + 8, br.y, 16, 32), "::", 13, new Color(fg.r, fg.g, fg.b, 0.45f), TextAnchor.MiddleLeft, FontStyle.Bold);
                Text(new Rect(br.x + 26, br.y, br.width - 30, 32), def.label, 13, fg);
                AddSrc(br, key, def.label, true);
                y += 38;
            }
            var cr = new Rect(r.x, r.yMax - 30, r.width, 28);
            if (Btn(cr, "Clear program", new Color(1, 1, 1, 0.06f), Line, Ink, 12)) _pending.Add(_g.ClearProgram);
        }

        // ---------- program tree ----------
        float ZoneH(List<Node> l)
        {
            float h = 22;
            if (l.Count == 0) return h + 28;
            foreach (var n in l) h += NodeH(n) + 6;
            return h;
        }
        float NodeH(Node n) { return n.kind != NodeKind.If ? 32 : 8 + 24 + 4 + 36 + 6 + ZoneH(n.then) + 6 + ZoneH(n.els) + 8; }

        void DrawProgram(Rect r)
        {
            Text(new Rect(r.x, r.y, r.width, 18), "YOUR PROGRAM", 10, Muted, TextAnchor.MiddleLeft, FontStyle.Bold);
            var box = new Rect(r.x, r.y + 24, r.width, r.height - 24);
            Outline(box, Hx("#34497a"), 2, 12);
            var e = Event.current;
            _progViewH = box.height - 12;
            if (e.type == EventType.ScrollWheel && box.Contains(e.mousePosition))
            {
                _scroll = Mathf.Clamp(_scroll + e.delta.y * 20, 0, Mathf.Max(0, _progContentH - _progViewH));
                e.Use();
            }
            _scroll = Mathf.Clamp(_scroll, 0, Mathf.Max(0, _progContentH - _progViewH));

            var inner = new Rect(box.x + 6, box.y + 6, box.width - 12, box.height - 12);
            var prevO = _origin; var prevC = _clip;
            GUI.BeginGroup(inner);
            _origin = new Vector2(inner.x, inner.y);
            _clip = inner;
            var prog = _g.Prog;
            float w = inner.width - 4, y = -_scroll;
            AddZone(new Rect(0, 0, inner.width, inner.height + _scroll), prog, 0, null);
            if (prog.Count == 0)
            {
                string msg = _g.levelKey == "field" ? "Drop “Walk to beacon” here" : "Drop an If block or an action here";
                Text(new Rect(0, 30, inner.width, 40), msg, 13, Muted, TextAnchor.MiddleCenter);
                y = 0;
            }
            else foreach (var n in prog.ToArray()) y += DrawNode(n, prog, 0, y, w) + 6;
            GUI.EndGroup();
            _origin = prevO; _clip = prevC;
            if (Event.current.type == EventType.Repaint) _progContentH = y + _scroll;
        }

        void DrawZone(string label, List<Node> list, float x, float y, float w)
        {
            float h = ZoneH(list);
            var r = new Rect(x, y, w, h);
            Fill(r, Hx("#0b1325"), 6); Outline(r, Hx("#3a4f7a"), 1, 6);
            Text(new Rect(x + 8, y + 3, w, 14), label.ToUpperInvariant(), 9, Muted, TextAnchor.MiddleLeft, FontStyle.Bold);
            AddZone(r, list, 0, null);
            float cy = y + 20;
            if (list.Count == 0) { Text(new Rect(x + 8, cy, w, 28), "drop actions here", 12, Hx("#4b5a7c")); return; }
            foreach (var n in list.ToArray()) cy += DrawNode(n, list, x + 6, cy, w - 12) + 6;
        }

        float DrawNode(Node n, List<Node> list, float x, float y, float w)
        {
            float h = NodeH(n);
            var r = new Rect(x, y, w, h);
            Color bg, bd, fg; BlockColors(n.kind, out bg, out bd, out fg);
            string label = n.kind == NodeKind.If ? "If … then … else" : ProgramModel.Label(n);
            if (y + h < 0 || y > _clip.height) return h;   // scrolled out of view
            Fill(r, Hover(r) && n.kind != NodeKind.If ? Color.Lerp(bg, Color.white, 0.08f) : bg, 9);
            Outline(r, bd, 1, 9);
            AddZone(r, list, n.id, null);
            AddSrc(r, "move:" + n.id, label, false);
            int id = n.id;
            if (n.kind != NodeKind.If)
            {
                Text(new Rect(x + 8, y, 16, h), "::", 13, new Color(fg.r, fg.g, fg.b, 0.4f), TextAnchor.MiddleLeft, FontStyle.Bold);
                Text(new Rect(x + 26, y, w - 56, h), label, 13, fg);
                if (CloseBtn(new Rect(x + w - 28, y + 5, 22, 22))) _pending.Add(() => _g.RemoveNode(id));
                return h;
            }
            float yy = y + 8;
            Text(new Rect(x + 12, yy, 60, 24), "IF", 13, fg, TextAnchor.MiddleLeft, FontStyle.Bold);
            if (CloseBtn(new Rect(x + w - 30, yy + 1, 22, 22))) _pending.Add(() => _g.RemoveNode(id));
            yy += 28;
            DrawCondSlot(n, x + 8, yy, w - 16);
            yy += 42;
            DrawZone("Then", n.then, x + 8, yy, w - 16); yy += ZoneH(n.then) + 6;
            DrawZone("Else", n.els, x + 8, yy, w - 16);
            return h;
        }

        void DrawCondSlot(Node ifNode, float x, float y, float w)
        {
            var r = new Rect(x, y, w, 36);
            Fill(r, Hx("#161208"), 8); Outline(r, Hx("#8a6d28"), 2, 8);
            AddZone(r, null, 0, ifNode);
            if (ifNode.cond == null) { Text(new Rect(x + 10, y, w, 36), "drop a condition here", 12, Hx("#c9a95a")); return; }
            string label = ProgramModel.Label(ifNode.cond);
            float pw = TextWidth(label, 13) + 22;
            var pr = new Rect(x + 6, y + 5, pw, 26);
            Fill(pr, Hx("#3f3115"), 7); Outline(pr, Hx("#b08a30"), 1, 7);
            Text(pr, label, 13, Hx("#ffe7ae"), TextAnchor.MiddleCenter);
            AddSrc(pr, "move:" + ifNode.cond.id, label, false);
            var node = ifNode;
            if (CloseBtn(new Rect(r.xMax - 28, y + 7, 22, 22))) _pending.Add(() => { node.cond = null; _g.synth.Sfx("remove"); _g.ResetAndRender(false); });
        }

        bool CloseBtn(Rect r)
        {
            bool h = Hover(r);
            Text(r, "×", 17, h ? Hx("#ff8d8d") : Muted, TextAnchor.MiddleCenter);
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        // ---------- code view ----------
        void DrawCode(Rect r)
        {
            if (!_codeOpen)
            {
                if (Btn(r, "C\no\nd\ne\n>", new Color(1, 1, 1, 0.05f), Line, Ink, 12, FontStyle.Bold)) _codeOpen = true;
                return;
            }
            if (Btn(new Rect(r.x, r.y, r.width, 26), "v CODE", new Color(1, 1, 1, 0.05f), Line, Ink, 12)) _codeOpen = false;
            var box = new Rect(r.x, r.y + 32, r.width, r.height - 32);
            Fill(box, Hx("#080d1a"), 10); Outline(box, Hx("#17233f"), 1, 10);
            Text(new Rect(box.x + 10, box.y + 4, box.width - 80, 22), "TYPE HERE, BLOCKS FOLLOW", 10, Muted, TextAnchor.MiddleLeft, FontStyle.Bold);
            bool copied = Time.unscaledTime < _copiedUntil;
            if (Btn(new Rect(box.xMax - 66, box.y + 4, 58, 22), copied ? "Copied" : "Copy", new Color(1, 1, 1, 0.07f), Line, Ink, 11))
            {
                var body = _g.codeText.Split('\n');
                var sb = new System.Text.StringBuilder("while (!beaconRestored()) {\n");
                foreach (var l in body) sb.Append("  ").Append(l).Append('\n');
                sb.Append("}");
                GUIUtility.systemCopyBuffer = sb.ToString();
                _copiedUntil = Time.unscaledTime + 1.6f;
            }
            var mono = new Color(0x6f / 255f, 0x7f / 255f, 0xa3 / 255f);
            Text(new Rect(box.x + 8, box.y + 30, box.width - 16, 20), "while (!beaconRestored()) {", 13, mono);
            float errH = string.IsNullOrEmpty(_g.codeError) ? 0 : 36;
            var ta = new Rect(box.x + 14, box.y + 50, box.width - 24, Mathf.Max(30, box.height - 50 - 24 - errH));
            string before = _g.codeText;
            string after = GUI.TextArea(ta, before, _mono);
            if (after != before) _pending.Add(() => _g.OnCodeTyped(after));
            Text(new Rect(box.x + 8, ta.yMax, box.width - 16, 20), "}", 13, mono);
            if (errH > 0)
            {
                var er = new Rect(box.x + 6, box.yMax - errH - 4, box.width - 12, errH);
                Fill(er, new Color(1, 0.35f, 0.35f, 0.1f), 6);
                Text(new Rect(er.x + 6, er.y, er.width - 12, er.height), _g.codeError, 11, Hx("#ffb3b3"), TextAnchor.MiddleLeft, FontStyle.Normal, true);
            }
        }

        // ========================
        // MOUSE: minimap drag + block drag and drop
        // ========================
        void HandleMouse(Event e)
        {
            var mp = e.mousePosition;
            // minimap
            if (_g.view.camEnabled && _miniRect.width > 0)
            {
                if (e.type == EventType.MouseDown && e.button == 0 && _miniRect.Contains(mp)) { _miniDrag = true; e.Use(); }
                if (_miniDrag && (e.type == EventType.MouseDown || e.type == EventType.MouseDrag))
                {
                    _g.view.CenterOn(Mathf.Clamp01((mp.x - _miniRect.x) / _miniRect.width), Mathf.Clamp01((mp.y - _miniRect.y) / _miniRect.height));
                    e.Use();
                }
                if (e.type == EventType.MouseUp) _miniDrag = false;
            }
            if (e.type == EventType.MouseDown && e.button == 0)
            {
                Src hit = null;
                foreach (var s in _srcs) if (s.rect.Contains(mp)) hit = s;   // innermost (recorded last) wins
                if (hit != null) { _cand = hit; _candStart = mp; _dragging = false; e.Use(); }
            }
            else if (e.type == EventType.MouseDrag && _cand != null)
            {
                if (!_dragging && Vector2.Distance(mp, _candStart) > 6) _dragging = true;
                e.Use();
            }
            else if (e.type == EventType.MouseUp && e.button == 0 && _cand != null)
            {
                var cand = _cand; bool dragged = _dragging;
                _cand = null; _dragging = false;
                if (dragged)
                {
                    Zone target = null;
                    foreach (var z in _zones) if (z.rect.Contains(mp)) target = z;
                    if (target != null)
                    {
                        var t = target;
                        if (t.ifNode != null) _pending.Add(() => _g.DropOnCond(cand.payload, t.ifNode));
                        else _pending.Add(() => _g.DropOnList(cand.payload, t.list, t.beforeId));
                    }
                }
                else if (cand.palette) { string key = cand.payload; _pending.Add(() => _g.AddBlock(key)); }
                e.Use();
            }
        }

        // The floating label that follows the mouse, plus a highlight on the zone it would land in
        void DrawDragFeedback(Vector2 mp)
        {
            if (Event.current.type != EventType.Repaint) return;
            Zone target = null;
            foreach (var z in _zones) if (z.rect.Contains(mp)) target = z;
            if (target != null) Outline(target.rect, Hx("#7fd7ff", 0.9f), 2, 8);
            float w = TextWidth(_cand.label, 13, FontStyle.Bold) + 24;
            var r = new Rect(mp.x - w / 2, mp.y - 40, w, 28);
            Fill(r, Hx("#7fd7ff", 0.94f), 9);
            Text(r, _cand.label, 13, Hx("#05121f"), TextAnchor.MiddleCenter, FontStyle.Bold);
        }
    }
}
