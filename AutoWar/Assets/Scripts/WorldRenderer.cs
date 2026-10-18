using System.Collections.Generic;
using UnityEngine;

namespace AutoWar
{
    // ========================
    // WORLD RENDERER
    // Draws the map, fog, units, guards, beacon and sparks, and owns the camera (pan / zoom / fit).
    // World space: 1 unit = 1 tile, column -> +x, row -> -y (row 0 is the top of the map).
    // ========================
    public class WorldRenderer : MonoBehaviour
    {
        SquadSimulator _sim;
        Camera _cam;
        Material _normalMat, _addMat;
        Sprite _disc, _glow, _ring, _square, _beam;

        GameObject _terrainGo, _fogGo, _root;
        Texture2D _fogTex, _miniTex;
        Color32[] _fogPx;
        public Texture2D MiniTexture { get { return _miniTex; } }

        // Camera state, in map tile units (cx = column, cy = row) and screen pixels per tile
        public float camCx, camCy, ppu = 40, fitPpu = 40, maxPpu = 100;
        public bool camEnabled;
        Rect _safe;             // screen pixels, top-left origin
        float _uiScale = 1;
        bool _hasCamBounds;

        class Dot { public GameObject go; public SpriteRenderer sr; }
        readonly List<Dot> _pool = new List<Dot>();
        int _poolUsed;
        bool _panning;
        Vector2 _panStart; float _panCx, _panCy;
        int _frame;

        static readonly Color Smoke = new Color(4 / 255f, 6 / 255f, 12 / 255f, 1);

        // -------------------------------------------------------
        // Init(sim)
        // Creates the camera, shared sprites and materials. Runs once at startup.
        // -------------------------------------------------------
        public void Init(SquadSimulator sim)
        {
            _sim = sim;
            _cam = Camera.main;
            if (_cam == null)
            {
                var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
                _cam = camGo.AddComponent<Camera>();
                camGo.AddComponent<AudioListener>();
            }
            _cam.orthographic = true;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = Smoke;
            _cam.transform.rotation = Quaternion.identity;
            _cam.nearClipPlane = -50; _cam.farClipPlane = 50;

            var spriteShader = Shader.Find("Sprites/Default");
            _normalMat = new Material(spriteShader);
            var addShader = Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Sprites/Default");
            _addMat = new Material(addShader);

            _disc = MakeSprite(64, (x, y) => Mathf.Clamp01((1 - Mathf.Sqrt(x * x + y * y)) * 32f + 0.5f));
            _glow = MakeSprite(64, (x, y) => { float d = Mathf.Sqrt(x * x + y * y); return d >= 1 ? 0 : Mathf.Pow(1 - d, 1.6f); });
            _ring = MakeSprite(64, (x, y) => { float d = Mathf.Sqrt(x * x + y * y); return Mathf.Clamp01((1 - Mathf.Abs(d - 0.93f) * 32f)); });
            _square = MakeSprite(4, (x, y) => 1f);
            _beam = MakeSprite(32, (x, y) => Mathf.Clamp01((1 - y) * 0.5f)); // fades toward +y

            _root = new GameObject("World");
        }

        // Builds a white sprite whose alpha comes from f(x, y) with x, y in [-1, 1]
        static Sprite MakeSprite(int size, System.Func<float, float, float> alpha)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color[size * size];
            for (int j = 0; j < size; j++) for (int i = 0; i < size; i++)
            {
                float x = (i + 0.5f) / size * 2 - 1, y = (j + 0.5f) / size * 2 - 1;
                px[j * size + i] = new Color(1, 1, 1, alpha(x, y));
            }
            tex.SetPixels(px);
            tex.filterMode = FilterMode.Bilinear; tex.wrapMode = TextureWrapMode.Clamp;
            tex.Apply(false);
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        }

        // -------------------------------------------------------
        // BuildLevel(uiScale)
        // Called after the simulator loads a map: repaints the terrain, rebuilds the fog and minimap textures.
        // -------------------------------------------------------
        public void BuildLevel()
        {
            if (_terrainGo != null) Destroy(_terrainGo);
            if (_fogGo != null) Destroy(_fogGo);
            var terrainTex = new TerrainPainter(_sim).Paint();
            _terrainGo = new GameObject("Terrain");
            _terrainGo.transform.SetParent(_root.transform, false);
            var sr = _terrainGo.AddComponent<SpriteRenderer>();
            sr.sprite = Sprite.Create(terrainTex, new Rect(0, 0, terrainTex.width, terrainTex.height), Vector2.zero, TerrainPainter.Tile);
            sr.sharedMaterial = _normalMat;
            sr.sortingOrder = 0;
            _terrainGo.transform.position = new Vector3(-_sim.padC, -(_sim.rows + _sim.padR), 0);

            _fogTex = new Texture2D(_sim.cols, _sim.rows, TextureFormat.RGBA32, false);
            _fogTex.filterMode = FilterMode.Point; _fogTex.wrapMode = TextureWrapMode.Clamp;
            _fogPx = new Color32[_sim.cols * _sim.rows];
            _fogGo = new GameObject("Fog");
            _fogGo.transform.SetParent(_root.transform, false);
            var fr = _fogGo.AddComponent<SpriteRenderer>();
            fr.sprite = Sprite.Create(_fogTex, new Rect(0, 0, _sim.cols, _sim.rows), Vector2.zero, 1);
            fr.sharedMaterial = _normalMat;
            fr.sortingOrder = 10;
            _fogGo.transform.position = new Vector3(0, -_sim.rows, 0);

            _miniTex = new Texture2D(_sim.cols * 3, _sim.rows * 3, TextureFormat.RGBA32, false);
            _miniTex.filterMode = FilterMode.Point;
            camEnabled = _sim.level.hasCamera;
            _hasCamBounds = true;
        }

        // -------------------------------------------------------
        // SetSafeArea(...)
        // Tells the camera which part of the screen is free of HUD (screen pixels, top-left origin).
        // Keeps the current zoom unless `reset` is set or the view was already "fit".
        // -------------------------------------------------------
        public void SetSafeArea(Rect safe, float uiScale, bool reset)
        {
            _safe = safe; _uiScale = uiScale;
            if (!_hasCamBounds) return;
            bool wasFit = Mathf.Abs(ppu - fitPpu) < 1e-3f;
            fitPpu = Mathf.Min(safe.width / _sim.cols, safe.height / _sim.rows) * 0.97f;
            maxPpu = Mathf.Max(fitPpu, 64f * uiScale);
            if (reset || wasFit || !camEnabled) { ppu = fitPpu; camCx = _sim.cols / 2f; camCy = _sim.rows / 2f; }
            else ppu = Mathf.Clamp(ppu, fitPpu, maxPpu);
            ClampCam();
        }

        public void FitView() { ppu = fitPpu; camCx = _sim.cols / 2f; camCy = _sim.rows / 2f; ClampCam(); }

        void ClampCam()
        {
            float vw = _safe.width / ppu, vh = _safe.height / ppu;
            camCx = vw >= _sim.cols ? _sim.cols / 2f : Mathf.Clamp(camCx, vw / 2, _sim.cols - vw / 2);
            camCy = vh >= _sim.rows ? _sim.rows / 2f : Mathf.Clamp(camCy, vh / 2, _sim.rows - vh / 2);
        }

        public void ZoomAt(float px, float pyTop, float factor)
        {
            float sx = _safe.x + _safe.width / 2, sy = _safe.y + _safe.height / 2;
            float wx = camCx + (px - sx) / ppu, wy = camCy + (pyTop - sy) / ppu;
            ppu = Mathf.Clamp(ppu * factor, fitPpu, maxPpu);
            camCx = wx - (px - sx) / ppu;
            camCy = wy - (pyTop - sy) / ppu;
            ClampCam();
        }
        public void ZoomCenter(float factor) { ZoomAt(_safe.x + _safe.width / 2, _safe.y + _safe.height / 2, factor); }

        // Minimap click: fractions of the map (0..1)
        public void CenterOn(float fx, float fy)
        {
            camCx = fx * _sim.cols; camCy = fy * _sim.rows; ClampCam();
        }

        // The visible region as fractions of the map, for the minimap outline
        public Rect ViewFractions()
        {
            float vw = _safe.width / ppu, vh = _safe.height / ppu;
            return new Rect((camCx - vw / 2) / _sim.cols, (camCy - vh / 2) / _sim.rows, vw / _sim.cols, vh / _sim.rows);
        }

        // ========================
        // PER-FRAME
        // ========================
        public void HandleCameraInput(bool pointerOverUi)
        {
            if (!camEnabled) return;
            var mp = Input.mousePosition; mp.y = Screen.height - mp.y; // top-left origin
            if (Input.GetMouseButtonDown(0) && !pointerOverUi)
            {
                _panning = true; _panStart = mp; _panCx = camCx; _panCy = camCy;
            }
            if (_panning)
            {
                if (Input.GetMouseButton(0))
                {
                    camCx = _panCx - (mp.x - _panStart.x) / ppu;
                    camCy = _panCy - (mp.y - _panStart.y) / ppu;
                    ClampCam();
                }
                else _panning = false;
            }
            float wheel = Input.mouseScrollDelta.y;
            if (!pointerOverUi && Mathf.Abs(wheel) > 0.01f) ZoomAt(mp.x, mp.y, wheel > 0 ? 1.12f : 1 / 1.12f);
        }

        void LateUpdate()
        {
            if (_sim == null || _sim.map == null || _fogTex == null) return;
            _frame++;
            PlaceCamera();
            UpdateFog();
            DrawEntities();
            if (camEnabled && _frame % 2 == 0) UpdateMini();
        }

        void PlaceCamera()
        {
            float W = Screen.width, H = Screen.height;
            float sx = _safe.x + _safe.width / 2, sy = _safe.y + _safe.height / 2;
            float wx = camCx, wy = -camCy;
            _cam.orthographicSize = H / (2f * ppu);
            _cam.transform.position = new Vector3(wx - (sx - W / 2) / ppu, wy - (H - sy - H / 2) / ppu, -10);
        }

        void UpdateFog()
        {
            int R = _sim.rows, C = _sim.cols;
            float t = Time.time;
            for (int r = 0; r < R; r++) for (int c = 0; c < C; c++)
            {
                Color32 col;
                if (!_sim.known[r, c])
                {
                    float a = 0.5f + 0.5f * Mathf.Sin(t * 0.7f + r * 0.8f + c * 0.5f);
                    float al = 0.22f + 0.16f * a;
                    col = new Color(Mathf.Lerp(4, 52, al) / 255f, Mathf.Lerp(6, 62, al) / 255f, Mathf.Lerp(12, 88, al) / 255f, 1f);
                }
                else if (!_sim.visibleNow[r, c]) col = new Color32(5, 8, 16, 128);
                else col = new Color32(0, 0, 0, 0);
                _fogPx[(R - 1 - r) * C + c] = col;
            }
            _fogTex.SetPixels32(_fogPx);
            _fogTex.Apply(false);
        }

        void UpdateMini()
        {
            int R = _sim.rows, C = _sim.cols, S = 3;
            var px = new Color32[C * S * R * S];
            var bg = new Color32(4, 6, 12, 255);
            for (int i = 0; i < px.Length; i++) px[i] = bg;
            System.Action<int, int, int, Color32> dot = (c, r, size, col) =>
            {
                for (int dy = 0; dy < size; dy++) for (int dx = 0; dx < size; dx++)
                {
                    int x = c + dx, y = r + dy;
                    if (x < 0 || y < 0 || x >= C * S || y >= R * S) continue;
                    px[(R * S - 1 - y) * (C * S) + x] = col;
                }
            };
            for (int r = 0; r < R; r++) for (int c = 0; c < C; c++)
            {
                if (!_sim.known[r, c]) continue;
                bool lit = _sim.visibleNow[r, c];
                Color32 col = SquadSimulator.IsSolidCh(_sim.map[r][c])
                    ? (lit ? new Color32(0x7a, 0x9a, 0xd0, 255) : new Color32(0x46, 0x60, 0x8f, 255))
                    : (lit ? new Color32(0x2d, 0x4a, 0x80, 255) : new Color32(0x1a, 0x2c, 0x50, 255));
                dot(c * S, r * S, S, col);
            }
            if (_sim.known[_sim.beacon.r, _sim.beacon.c])
                dot(_sim.beacon.c * S - 1, _sim.beacon.r * S - 1, 5, _sim.restored ? new Color32(0xff, 0xc8, 0x57, 255) : new Color32(0x9f, 0xe3, 0xff, 255));
            foreach (var u in _sim.units)
                dot(Mathf.RoundToInt(u.ux * S), Mathf.RoundToInt(u.uy * S), 3, (Color32)Color.HSVToRGB(u.hue / 360f, 0.55f, 1f));
            _miniTex.SetPixels32(px);
            _miniTex.Apply(false);
        }

        // ========================
        // ENTITIES (pooled sprites)
        // ========================
        void BeginDots() { _poolUsed = 0; }
        void EndDots() { for (int i = _poolUsed; i < _pool.Count; i++) _pool[i].go.SetActive(false); }

        // Draws one sprite at world position `p` (z ignored) with a size in tiles
        void Put(Sprite sp, bool additive, Vector2 p, Vector2 size, Color col, int order, float rot = 0)
        {
            if (_poolUsed >= _pool.Count)
            {
                var go = new GameObject("dot");
                go.transform.SetParent(_root.transform, false);
                _pool.Add(new Dot { go = go, sr = go.AddComponent<SpriteRenderer>() });
            }
            var d = _pool[_poolUsed++];
            d.go.SetActive(true);
            d.sr.sprite = sp;
            d.sr.sharedMaterial = additive ? _addMat : _normalMat;
            d.sr.color = col;
            d.sr.sortingOrder = order;
            d.go.transform.position = new Vector3(p.x, p.y, 0);
            d.go.transform.rotation = Quaternion.Euler(0, 0, rot);
            // sprites are 1 world unit wide; scale by size
            d.go.transform.localScale = new Vector3(size.x, size.y, 1);
        }

        static Vector2 TilePos(float c, float r) { return new Vector2(c + 0.5f, -(r + 0.5f)); }

        void DrawEntities()
        {
            BeginDots();
            float t = Time.time;
            // Keep things readable when zoomed out
            float us = Mathf.Max(1f, 0.85f * 40f / (ppu / _uiScale));
            float px = 1f / 40f * us; // one "web pixel" in tiles

            DrawBeacon(t, us, px);

            foreach (var u in _sim.units)
            {
                var pos = TilePos(u.ux, u.uy);
                float bob = u.hasTarget ? Mathf.Abs(Mathf.Sin(t * 14 + u.id)) * 2.5f * px : 0;
                var halo = Color.HSVToRGB(u.hue / 360f, 0.65f, 1f); halo.a = 0.16f;
                Put(_glow, true, pos, Vector2.one * 6f, halo, 30);
                Put(_disc, false, pos + new Vector2(0, -12 * px), new Vector2(20 * px, 8 * px), new Color(0, 0, 0, 0.45f), 31);
                var body = Color.HSVToRGB(u.hue / 360f, 0.68f, 0.78f);
                var bodyGlow = Color.HSVToRGB(u.hue / 360f, 0.6f, 1f); bodyGlow.a = 0.45f;
                Put(_glow, true, pos + new Vector2(0, bob), Vector2.one * 36 * px, bodyGlow, 32);
                Put(_disc, false, pos + new Vector2(0, bob), Vector2.one * 20 * px, body, 33);
                var hi = Color.HSVToRGB(u.hue / 360f, 0.35f, 1f); hi.a = 0.75f;
                Put(_disc, false, pos + new Vector2(-3 * px, 3 * px + bob), Vector2.one * 11 * px, hi, 34);
                // the eye looks where the unit is heading (row -> -y)
                Put(_disc, false, pos + new Vector2(u.faceC * 5 * px, -u.faceR * 5 * px + bob), Vector2.one * 6 * px, Hex("#0a2238"), 35);
                if (_sim.running && u.idle)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        float a = 0.35f + 0.55f * Mathf.Max(0, Mathf.Sin(t * 4 - i * 0.9f));
                        Put(_disc, false, pos + new Vector2((-8 + i * 8) * px, 24 * px), Vector2.one * 4.4f * px, new Color(1, 0.84f, 0.47f, a), 36);
                    }
                }
                if (u.hp < SquadSimulator.SoldierHp) Bar(pos + new Vector2(0, 22 * px), 26 * px, 4 * px, u.hp / SquadSimulator.SoldierHp, Hex("#7fd7ff"));
            }

            foreach (var e in _sim.guards)
            {
                if (e.hp <= 0 || !_sim.known[e.r, e.c]) continue;
                var pos = TilePos(e.ux, e.uy);
                float pulse = 0.5f + 0.5f * Mathf.Sin(t * 3 + e.r);
                Put(_glow, true, pos, Vector2.one * 4f, new Color(1, 0.43f, 0.35f, 0.12f + 0.06f * pulse), 30);
                Put(_disc, false, pos + new Vector2(0, -12 * px), new Vector2(20 * px, 8 * px), new Color(0, 0, 0, 0.45f), 31);
                Put(_disc, false, pos, Vector2.one * 24 * px, Hex("#ff7a66"), 32);
                Put(_disc, false, pos, Vector2.one * 20 * px, Hex("#3a2a33"), 33);
                if (e.hp < SquadSimulator.GuardHp) Bar(pos + new Vector2(0, 22 * px), 26 * px, 4 * px, e.hp / SquadSimulator.GuardHp, Hex("#ff7a66"));
            }

            foreach (var s in _sim.sparks)
                Put(_disc, true, new Vector2(s.x, -s.y), Vector2.one * 4.4f * px, new Color(1, 0.84f, 0.47f, Mathf.Min(1, s.life / 1.1f)), 50);
            EndDots();
        }

        void Bar(Vector2 center, float w, float h, float frac, Color col)
        {
            Put(_square, false, center, new Vector2(w / 4f * 4f, h), new Color(0, 0, 0, 0.6f), 40);
            float f = Mathf.Clamp01(frac);
            Put(_square, false, center + new Vector2(-(w - w * f) / 2, 0), new Vector2(w * f, h), col, 41);
        }

        void DrawBeacon(float t, float us, float px)
        {
            var b = _sim.beacon;
            if (!_sim.known[b.r, b.c]) return;
            bool lit = _sim.visibleNow[b.r, b.c];
            bool done = _sim.restored;
            var rgb = done ? new Color(1f, 0.81f, 0.43f) : new Color(0.5f, 0.84f, 1f);
            float al = lit ? 1f : 0.55f;
            float pulse = 0.5f + 0.5f * Mathf.Sin(t * 2.4f);
            var pos = TilePos(b.c, b.r);
            float R = done ? 4.4f : 3f;
            Put(_glow, true, pos, Vector2.one * R * 2 * us, WithA(rgb, ((done ? 0.36f : 0.22f) + 0.08f * pulse) * al), 20);
            for (int k = 0; k < 2; k++)
            {
                float p = (t * 0.6f + k * 0.5f) % 1;
                float rad = (12 + p * (done ? 34 : 24)) * px;
                Put(_ring, false, pos, Vector2.one * rad * 2, WithA(rgb, (1 - p) * 0.6f * al), 21);
            }
            float bh = (done ? 150 : 90) * px, bw = (done ? 14 : 10) * px;
            Put(_beam, true, pos + new Vector2(0, bh / 2), new Vector2(bw, bh),WithA(rgb, 0.4f * al), 22);
            Put(_disc, false, pos + new Vector2(0, -12 * px), new Vector2(22 * px, 8 * px), new Color(0, 0, 0, 0.4f), 23);
            Put(_glow, true, pos, Vector2.one * 40 * px, WithA(rgb, 0.5f * al), 24);
            var body = done ? Hex("#fff1c9") : Hex("#dff3ff");
            Put(_square, false, pos, new Vector2(12 * px, 20 * px), WithA(body, al), 25);
            Put(_disc, false, pos + new Vector2(0, 14 * px), Vector2.one * (10 + Mathf.Sin(t * 4) * 1.2f) * px, WithA(body, al), 26);
        }

        static Color WithA(Color c, float a) { return new Color(c.r, c.g, c.b, a); }
        static Color Hex(string h) { return TerrainPainter.Hex(h); }
    }
}
