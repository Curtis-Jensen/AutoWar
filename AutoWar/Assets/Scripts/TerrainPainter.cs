using UnityEngine;

namespace AutoWar
{
    // ========================
    // PIXEL CANVAS
    // A tiny software canvas (top-left origin, alpha blended) so the terrain art ports over from the web version unchanged.
    // ========================
    public class PixelCanvas
    {
        public readonly int W, H;
        readonly Color[] _px;
        public float offX, offY;
        bool _clipOn;
        float _cx0, _cy0, _cx1, _cy1;
        readonly float[] _rad = new float[4]; // TL, TR, BR, BL

        public PixelCanvas(int w, int h, Color fill)
        {
            W = w; H = h;
            _px = new Color[w * h];
            for (int i = 0; i < _px.Length; i++) _px[i] = fill;
        }

        public void SetClip(float x, float y, float w, float h, float tl, float tr, float br, float bl)
        {
            _clipOn = true;
            _cx0 = x + offX; _cy0 = y + offY; _cx1 = _cx0 + w; _cy1 = _cy0 + h;
            _rad[0] = tl; _rad[1] = tr; _rad[2] = br; _rad[3] = bl;
        }
        public void ClearClip() { _clipOn = false; }

        bool InClip(float px, float py)
        {
            if (px < _cx0 || px > _cx1 || py < _cy0 || py > _cy1) return false;
            // each corner: outside the rounded arc?
            float r;
            r = _rad[0]; if (r > 0 && px < _cx0 + r && py < _cy0 + r && Sq(px - _cx0 - r) + Sq(py - _cy0 - r) > r * r) return false;
            r = _rad[1]; if (r > 0 && px > _cx1 - r && py < _cy0 + r && Sq(px - _cx1 + r) + Sq(py - _cy0 - r) > r * r) return false;
            r = _rad[2]; if (r > 0 && px > _cx1 - r && py > _cy1 - r && Sq(px - _cx1 + r) + Sq(py - _cy1 + r) > r * r) return false;
            r = _rad[3]; if (r > 0 && px < _cx0 + r && py > _cy1 - r && Sq(px - _cx0 - r) + Sq(py - _cy1 + r) > r * r) return false;
            return true;
        }
        static float Sq(float v) { return v * v; }

        void Blend(int x, int y, Color s, float cov)
        {
            if (x < 0 || y < 0 || x >= W || y >= H) return;
            if (_clipOn && !InClip(x + 0.5f, y + 0.5f)) return;
            float a = s.a * cov;
            if (a <= 0f) return;
            int i = y * W + x;
            var d = _px[i];
            d.r = s.r * a + d.r * (1 - a);
            d.g = s.g * a + d.g * (1 - a);
            d.b = s.b * a + d.b * (1 - a);
            d.a = 1;
            _px[i] = d;
        }

        public void FillRect(float x, float y, float w, float h, Color c)
        {
            x += offX; y += offY;
            int x0 = Mathf.RoundToInt(x), x1 = Mathf.RoundToInt(x + w), y0 = Mathf.RoundToInt(y), y1 = Mathf.RoundToInt(y + h);
            if (x1 == x0 && w > 0) x1 = x0 + 1;
            if (y1 == y0 && h > 0) y1 = y0 + 1;
            for (int py = y0; py < y1; py++) for (int px = x0; px < x1; px++) Blend(px, py, c, 1f);
        }

        // Vertical gradient between two colours (alpha included)
        public void GradV(float x, float y, float w, float h, Color top, Color bot)
        {
            x += offX; y += offY;
            int x0 = Mathf.RoundToInt(x), x1 = Mathf.RoundToInt(x + w), y0 = Mathf.RoundToInt(y), y1 = Mathf.RoundToInt(y + h);
            for (int py = y0; py < y1; py++)
            {
                float t = y1 - y0 <= 1 ? 0 : (py - y0) / (float)(y1 - y0 - 1);
                var c = Color.Lerp(top, bot, t);
                for (int px = x0; px < x1; px++) Blend(px, py, c, 1f);
            }
        }

        public void GradH(float x, float y, float w, float h, Color left, Color right)
        {
            x += offX; y += offY;
            int x0 = Mathf.RoundToInt(x), x1 = Mathf.RoundToInt(x + w), y0 = Mathf.RoundToInt(y), y1 = Mathf.RoundToInt(y + h);
            for (int px = x0; px < x1; px++)
            {
                float t = x1 - x0 <= 1 ? 0 : (px - x0) / (float)(x1 - x0 - 1);
                var c = Color.Lerp(left, right, t);
                for (int py = y0; py < y1; py++) Blend(px, py, c, 1f);
            }
        }

        public void StrokeRect(float x, float y, float w, float h, float lw, Color c)
        {
            FillRect(x, y, w, lw, c); FillRect(x, y + h - lw, w, lw, c);
            FillRect(x, y, lw, h, c); FillRect(x + w - lw, y, lw, h, c);
        }

        public void Ellipse(float cx, float cy, float rx, float ry, Color c)
        {
            cx += offX; cy += offY;
            int x0 = Mathf.FloorToInt(cx - rx - 1), x1 = Mathf.CeilToInt(cx + rx + 1), y0 = Mathf.FloorToInt(cy - ry - 1), y1 = Mathf.CeilToInt(cy + ry + 1);
            float m = Mathf.Min(rx, ry);
            for (int py = y0; py <= y1; py++) for (int px = x0; px <= x1; px++)
            {
                float dx = (px + 0.5f - cx) / rx, dy = (py + 0.5f - cy) / ry;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float cov = Mathf.Clamp01((1 - d) * m + 0.5f);
                if (cov > 0) Blend(px, py, c, cov);
            }
        }

        public void RoundRect(float x, float y, float w, float h, float r, Color c)
        {
            x += offX; y += offY;
            float hx = w / 2, hy = h / 2, mx = x + hx, my = y + hy;
            int x0 = Mathf.FloorToInt(x - 1), x1 = Mathf.CeilToInt(x + w + 1), y0 = Mathf.FloorToInt(y - 1), y1 = Mathf.CeilToInt(y + h + 1);
            for (int py = y0; py <= y1; py++) for (int px = x0; px <= x1; px++)
            {
                float qx = Mathf.Abs(px + 0.5f - mx) - (hx - r), qy = Mathf.Abs(py + 0.5f - my) - (hy - r);
                float d = Mathf.Sqrt(Mathf.Max(qx, 0) * Mathf.Max(qx, 0) + Mathf.Max(qy, 0) * Mathf.Max(qy, 0)) + Mathf.Min(Mathf.Max(qx, qy), 0) - r;
                float cov = Mathf.Clamp01(0.5f - d);
                if (cov > 0) Blend(px, py, c, cov);
            }
        }

        // Anti-aliased line segment
        public void Line(float ax, float ay, float bx, float by, float width, Color c)
        {
            ax += offX; ay += offY; bx += offX; by += offY;
            int x0 = Mathf.FloorToInt(Mathf.Min(ax, bx) - width - 1), x1 = Mathf.CeilToInt(Mathf.Max(ax, bx) + width + 1);
            int y0 = Mathf.FloorToInt(Mathf.Min(ay, by) - width - 1), y1 = Mathf.CeilToInt(Mathf.Max(ay, by) + width + 1);
            float vx = bx - ax, vy = by - ay, len2 = vx * vx + vy * vy;
            for (int py = y0; py <= y1; py++) for (int px = x0; px <= x1; px++)
            {
                float wx = px + 0.5f - ax, wy = py + 0.5f - ay;
                float t = len2 <= 0 ? 0 : Mathf.Clamp01((wx * vx + wy * vy) / len2);
                float dx = wx - vx * t, dy = wy - vy * t;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float cov = Mathf.Clamp01(width / 2 + 0.5f - d);
                if (cov > 0) Blend(px, py, c, cov);
            }
        }

        // Converts to a texture (flips rows because textures start at the bottom)
        public Texture2D ToTexture()
        {
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            var flipped = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    flipped[(H - 1 - y) * W + x] = _px[y * W + x];
            tex.SetPixels32(flipped);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.Apply(false);
            return tex;
        }
    }

    // ========================
    // TERRAIN PAINTER
    // Paints the overgrown, ruined city into one texture (1 tile = 40 px).
    // ========================
    public class TerrainPainter
    {
        public const int Tile = 40;
        readonly SquadSimulator _s;
        PixelCanvas _t;

        static readonly Color[] Roofs = { Hex("#68726c"), Hex("#6e7770"), Hex("#626b66"), Hex("#6a7468") };

        public TerrainPainter(SquadSimulator sim) { _s = sim; }

        // ---------- colour and noise helpers ----------
        public static Color Hex(string h)
        {
            return new Color(System.Convert.ToInt32(h.Substring(1, 2), 16) / 255f, System.Convert.ToInt32(h.Substring(3, 2), 16) / 255f, System.Convert.ToInt32(h.Substring(5, 2), 16) / 255f, 1f);
        }
        static Color Shade(Color c, float f, float a = 1f) { return new Color(Mathf.Clamp01(c.r * f), Mathf.Clamp01(c.g * f), Mathf.Clamp01(c.b * f), a); }
        static Color Rgba(float r, float g, float b, float a) { return new Color(r / 255f, g / 255f, b / 255f, a); }

        // Same hash as the web version so the cracks and weeds land in the same places.
        public static float Hash(int r, int c, int k)
        {
            unchecked
            {
                int h = ((r + 1) * 73856093) ^ ((c + 1) * 19349663) ^ ((k + 1) * 83492791);
                uint u = (uint)h;
                h = (int)((u ^ (u >> 13)) * 1274126177u);
                u = (uint)h;
                u ^= u >> 16;
                return (u % 1000) / 1000f;
            }
        }

        // ---------- tile queries ----------
        bool BldAt(int r, int c) { return _s.InBounds(r, c) ? _s.map[r][c] == '#' : (_s.padR > 0 || _s.padC > 0); }
        struct Nb { public bool up, dn, lf, rt; }
        Nb Neighbors(int r, int c) { return new Nb { up = BldAt(r - 1, c), dn = BldAt(r + 1, c), lf = BldAt(r, c - 1), rt = BldAt(r, c + 1) }; }

        struct City { public Nb n; public bool plaza, dashH, dashV, inV, inH; }
        City CityInfo(int r, int c)
        {
            var lv = _s.level; var p = lv.plaza;
            int[] inV = null, inH = null;
            foreach (var v in lv.cityV) if (c >= v[0] && c <= v[1]) { inV = v; break; }
            foreach (var h in lv.cityH) if (r >= h[0] && r <= h[1]) { inH = h; break; }
            return new City
            {
                n = Neighbors(r, c),
                plaza = r >= p[0] && r <= p[2] && c >= p[1] && c <= p[3],
                dashH = inH != null && inH[1] > inH[0] && r == inH[0] && inV == null && c % 2 == 0,
                dashV = inV != null && inV[1] > inV[0] && c == inV[0] && inH == null && r % 2 == 0,
                inV = inV != null, inH = inH != null
            };
        }

        // ---------- drawing pieces ----------
        void Speckle(float x, float y, int r, int c, int count, float alpha)
        {
            for (int i = 0; i < count; i++)
            {
                var col = Hash(r, c, 50 + i) < 0.5f ? new Color(0, 0, 0, alpha) : new Color(1, 1, 1, alpha * 0.8f);
                _t.FillRect(x + Hash(r, c, 90 + i) * (Tile - 2), y + Hash(r, c, 130 + i) * (Tile - 2), 1.6f, 1.6f, col);
            }
        }

        void CrackLine(float x, float y, int r, int c, int seed, Color color)
        {
            float px = x + 8 + Hash(r, c, seed) * (Tile - 16), py = y + 6 + Hash(r, c, seed + 1) * (Tile - 12);
            for (int i = 0; i < 4; i++)
            {
                float nx = Mathf.Clamp(px + (Hash(r, c, seed + 2 + i) - 0.5f) * 14, x + 3, x + Tile - 3);
                float ny = Mathf.Clamp(py + (Hash(r, c, seed + 6 + i) - 0.3f) * 10, y + 3, y + Tile - 3);
                _t.Line(px, py, nx, ny, 1f, color);
                px = nx; py = ny;
            }
        }

        void SidewalkEdge(float x, float y, Nb n, Color fill, Color curb, float d)
        {
            if (n.up) _t.FillRect(x, y, Tile, d, fill);
            if (n.dn) _t.FillRect(x, y + Tile - d, Tile, d, fill);
            if (n.lf) _t.FillRect(x, y, d, Tile, fill);
            if (n.rt) _t.FillRect(x + Tile - d, y, d, Tile, fill);
            if (n.up) _t.FillRect(x, y + d, Tile, 1.6f, curb);
            if (n.dn) _t.FillRect(x, y + Tile - d - 1.6f, Tile, 1.6f, curb);
            if (n.lf) _t.FillRect(x + d, y, 1.6f, Tile, curb);
            if (n.rt) _t.FillRect(x + Tile - d - 1.6f, y, 1.6f, Tile, curb);
        }

        void FloorTile(int r, int c)
        {
            float x = c * Tile, y = r * Tile;
            var i = CityInfo(r, c);
            if (i.plaza)
            {
                _t.FillRect(x, y, Tile, Tile, Hex("#7b807c"));
                var line = Rgba(20, 30, 24, 0.4f);
                _t.StrokeRect(x + 0.5f, y + 0.5f, Tile - 1, Tile - 1, 1.2f, line);
                _t.FillRect(x + Tile / 2f - 0.6f, y, 1.2f, Tile, line);
                _t.FillRect(x, y + Tile / 2f - 0.6f, Tile, 1.2f, line);
                if (Hash(r, c, 8) < 0.2f) _t.FillRect(x + 22, y + 22, 16, 16, Hex("#1a201c"));
            }
            else
            {
                float b = 68 + Hash(r, c, 1) * 6;
                _t.FillRect(x, y, Tile, Tile, Rgba(b, b + 5, b + 6, 1f));
                Speckle(x, y, r, c, 16, 0.06f);
                var dash = Rgba(200, 190, 150, 0.2f);
                if (i.dashH) _t.FillRect(x + 6, y + Tile - 1.5f, Tile - 12, 3, dash);
                if (i.dashV) _t.FillRect(x + Tile - 1.5f, y + 6, 3, Tile - 12, dash);
                SidewalkEdge(x, y, i.n, Hex("#767b78"), Hex("#575c59"), 9);
            }
            if (Hash(r, c, 2) < 0.4f) CrackLine(x, y, r, c, 14, Rgba(10, 16, 12, 0.5f));
            if (Hash(r, c, 3) < 0.3f)
            {
                float gx = x + 6 + Hash(r, c, 4) * (Tile - 12), gy = y + 6 + Hash(r, c, 5) * (Tile - 12);
                for (int k = 0; k < 4; k++)
                {
                    var col = Shade(Hex("#4f8f4c"), 0.75f + Hash(r, c, 6 + k) * 0.5f, 0.85f);
                    _t.Ellipse(gx + (Hash(r, c, 10 + k) - 0.5f) * 8, gy + (Hash(r, c, 14 + k) - 0.5f) * 6, 2.4f + Hash(r, c, 18 + k) * 2.4f, 2f, col);
                }
            }
        }

        void DrawTree(float cx, float cy, float size, int seed)
        {
            _t.RoundRect(cx - 12, cy - 12, 24, 24, 4, new Color(0, 0, 0, 0.22f));
            _t.Ellipse(cx + 5, cy + 7, size, size * 0.8f, new Color(0, 0, 0, 0.3f));
            for (int i = 0; i < 7; i++)
            {
                float a = (i / 7f) * 6.28f + Hash(seed, i, 3), d = size * 0.45f;
                _t.Ellipse(cx + Mathf.Cos(a) * d, cy + Mathf.Sin(a) * d, size * 0.62f, size * 0.62f, Shade(Hex("#46803f"), 0.78f + Hash(seed, i, 4) * 0.3f));
            }
            _t.Ellipse(cx - size * 0.25f, cy - size * 0.3f, size * 0.42f, size * 0.42f, Shade(Hex("#46803f"), 1.18f, 0.55f));
        }

        // Wrecked cars are plain, low obstacles in the same muted tone as the buildings
        void DrawWreck(float cx, float cy, bool vert, int seed)
        {
            var tone = Roofs[seed % Roofs.Length];
            System.Func<float, float, float, float, float, float[]> box = (lx, ly, w, h, r) =>
                vert ? new[] { cx - ly - h, cy + lx, h, w, r } : new[] { cx + lx, cy + ly, w, h, r };
            var s = box(-13, -5, 30, 17, 5); _t.RoundRect(s[0], s[1], s[2], s[3], s[4], new Color(0, 0, 0, 0.3f));
            var b = box(-15, -8, 30, 16, 5); _t.RoundRect(b[0], b[1], b[2], b[3], b[4], Shade(tone, 0.95f));
            var hi = box(-14, -7, 28, 4, 2); _t.RoundRect(hi[0], hi[1], hi[2], hi[3], hi[4], Shade(tone, 1.12f, 0.6f));
            var roof = box(-9, -6, 17, 12, 3); _t.RoundRect(roof[0], roof[1], roof[2], roof[3], roof[4], Shade(tone, 0.72f, 0.8f));
            float mx = vert ? cx + 2 : cx + 3, my = vert ? cy + 3 : cy - 2;
            _t.Ellipse(mx, my, 6, 4, Rgba(80, 150, 70, 0.4f));
        }

        // A building tile: rooftop plus a front facade where the building ends. Edges and corners are drawn as one continuous frame.
        void BuildingTile(int r, int c)
        {
            float x = c * Tile, y = r * Tile;
            var n = Neighbors(r, c);
            int id = _s.InBounds(r, c) ? _s.buildingId[r, c] : Mathf.Abs((r >> 2) * 5 + (c >> 2) * 3);
            var tone = Roofs[id % Roofs.Length];
            const float FH = 12, R = 5, L = 3;
            float topH = n.dn ? Tile : Tile - FH;
            _t.SetClip(x, y, Tile, Tile, (!n.up && !n.lf) ? R : 0, (!n.up && !n.rt) ? R : 0, (!n.dn && !n.rt) ? R : 0, (!n.dn && !n.lf) ? R : 0);
            _t.FillRect(x, y, Tile, Tile, Shade(tone, 0.4f));

            // rooftop
            _t.GradV(x, y, Tile, topH, Shade(tone, 1.12f), Shade(tone, 0.92f));
            Speckle(x, y, r, c, 10, 0.07f);
            var edge = Shade(tone, 0.6f, 0.3f);
            if (n.up) _t.FillRect(x, y, Tile, 1, edge);
            if (n.lf) _t.FillRect(x, y, 1, topH, edge);
            if (Hash(r, c, 60) < 0.4f)
            {
                for (int i = 0; i < 3; i++)
                    _t.Ellipse(x + 6 + Hash(r, c, 61 + i) * (Tile - 12), y + 6 + Hash(r, c, 65 + i) * (topH - 12), 4 + Hash(r, c, 69 + i) * 5, 3 + Hash(r, c, 73 + i) * 3, Rgba(90, 150, 80, 0.42f));
            }
            if (Hash(r, c, 80) < 0.2f) CrackLine(x, y, r, c, 82, Rgba(8, 14, 10, 0.5f));
            if (Hash(r, c, 90) < 0.06f && topH > 24)
            {
                _t.FillRect(x + 10, y + 10, 18, 11, Hex("#101512"));
                _t.FillRect(x + 8, y + 22, 8, 3, Rgba(140, 150, 140, 0.35f));
            }

            // parapet lip along exposed roof edges, mitred so the corners meet cleanly
            float y0 = n.up ? y : y + L;
            if (!n.up) { _t.FillRect(x, y, Tile, L, Rgba(205, 228, 208, 0.3f)); _t.FillRect(x, y + L, Tile, 1, new Color(0, 0, 0, 0.2f)); }
            if (!n.lf) _t.FillRect(x, y0, L, y + topH - y0, Rgba(205, 228, 208, 0.22f));
            if (!n.rt) _t.FillRect(x + Tile - L, y0, L, y + topH - y0, new Color(0, 0, 0, 0.3f));

            // front facade
            if (!n.dn)
            {
                float fy = y + topH;
                _t.FillRect(x, fy - 3, Tile, 3, Shade(tone, 1.05f));
                _t.FillRect(x, fy - 3, Tile, 1, new Color(1, 1, 1, 0.18f));
                _t.GradV(x, fy, Tile, FH, Shade(tone, 0.72f), Shade(tone, 0.42f));
                _t.FillRect(x, fy, Tile, 2, new Color(0, 0, 0, 0.35f));
                for (int i = 0; i < 3; i++)
                {
                    _t.FillRect(x + 6 + i * 11, fy + 3.5f, 7, 6, Hex("#121816"));
                    if (Hash(r, c * 3 + i, 71) < 0.25f) _t.FillRect(x + 6 + i * 11, fy + 3.5f, 3, 3, Rgba(150, 180, 170, 0.4f));
                }
                if (Hash(r, c, 95) < 0.45f)
                {
                    float vx = x + 6 + Hash(r, c, 96) * (Tile - 12);
                    _t.Line(vx, fy, vx + 2, fy + FH, 2f, Rgba(70, 140, 70, 0.75f));
                }
                // corner posts carry the roof edge lines down the wall, so roof and facade read as one block
                if (!n.lf) { _t.FillRect(x, fy - 3, L, FH + 3, Shade(tone, 1.0f, 0.9f)); _t.FillRect(x, fy - 3, 1, FH + 3, new Color(1, 1, 1, 0.18f)); }
                if (!n.rt) _t.FillRect(x + Tile - L, fy - 3, L, FH + 3, Shade(tone, 0.55f, 0.95f));
                _t.FillRect(x, y + Tile - 1.5f, Tile, 1.5f, new Color(0, 0, 0, 0.45f));
            }
            _t.ClearClip();
        }

        // -------------------------------------------------------
        // Paint()
        // Renders floors, building shadows, buildings, trees and wrecks (plus the dimmed decorative surround) into one texture.
        // Called whenever a level loads.
        // -------------------------------------------------------
        public Texture2D Paint()
        {
            int PR = _s.padR, PC = _s.padC, ROWS = _s.rows, COLS = _s.cols;
            int cols = COLS + 2 * PC, rows = ROWS + 2 * PR;
            _t = new PixelCanvas(cols * Tile, rows * Tile, Hex("#070b14"));
            _t.offX = PC * Tile; _t.offY = PR * Tile;
            for (int r = -PR; r < ROWS + PR; r++) for (int c = -PC; c < COLS + PC; c++) if (!BldAt(r, c)) FloorTile(r, c);
            for (int r = -PR; r < ROWS + PR; r++) for (int c = -PC; c < COLS + PC; c++)
            {
                if (!BldAt(r, c)) continue;
                float x = c * Tile, y = r * Tile;
                if (!BldAt(r + 1, c)) _t.GradV(x, y + Tile, Tile, 14, new Color(0, 0, 0, 0.45f), new Color(0, 0, 0, 0));
                if (!BldAt(r, c + 1)) _t.GradH(x + Tile, y, 9, Tile, new Color(0, 0, 0, 0.28f), new Color(0, 0, 0, 0));
            }
            for (int r = -PR; r < ROWS + PR; r++) for (int c = -PC; c < COLS + PC; c++) if (BldAt(r, c)) BuildingTile(r, c);
            for (int r = 0; r < ROWS; r++) for (int c = 0; c < COLS; c++)
            {
                char k = _s.map[r][c];
                float cx = c * Tile + Tile / 2f, cy = r * Tile + Tile / 2f;
                if (k == 'T') DrawTree(cx, cy, 15 + Hash(r, c, 7) * 3, r * 31 + c);
                if (k == 'C') { var i = CityInfo(r, c); DrawWreck(cx, cy, i.inV && !i.inH, r * 3 + c); }
            }
            if (PR > 0 || PC > 0)
            {
                var dim = Rgba(5, 8, 16, 0.5f);
                _t.FillRect(-PC * Tile, -PR * Tile, cols * Tile, PR * Tile, dim);
                _t.FillRect(-PC * Tile, ROWS * Tile, cols * Tile, PR * Tile, dim);
                _t.FillRect(-PC * Tile, 0, PC * Tile, ROWS * Tile, dim);
                _t.FillRect(COLS * Tile, 0, PC * Tile, ROWS * Tile, dim);
            }
            return _t.ToTexture();
        }
    }
}
