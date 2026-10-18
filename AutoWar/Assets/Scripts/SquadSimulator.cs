using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AutoWar
{
    public class Unit
    {
        public int id;
        public Tile pos;
        public float ux, uy;
        public bool hasTarget;
        public Tile target;
        public float faceR = 0, faceC = 1;
        public List<Tile> scoutPath;
        public bool idle;
        public float idleUntil;
        public float hue;
        public float hp;
    }

    public class Guard
    {
        public int r, c;
        public float ux, uy;
        public bool hasStep;
        public int tr, tc;
        public List<Tile> path;
        public float repath;
        public bool chasing;
        public float hp, cool;
    }

    public struct Spark { public float x, y, vx, vy, life; }

    public enum ActType { None, Move, Fight, Arrived }
    public struct ActResult { public ActType type; public Tile tile; }

    // ========================
    // SQUAD SIMULATION
    // Everything the game "knows": map, fog, squads, guards, and the program being run.
    // No rendering lives here. The renderer and HUD only read these fields.
    // ========================
    public class SquadSimulator
    {
        public const float Speed = 2.6f;
        public const int MinGain = 12, StepLimit = 12000;
        public const int GuardRange = 2, GuardSight = 5;
        public const float GuardSpeed = 1.4f, GuardCool = 1.6f, GuardHp = 3, SoldierHp = 4, SoldierDps = 0.9f;
        public const string Complete = "Beacon restored. Mission complete.";
        const string WaitMsg = "Waiting… nothing in your program covers this situation yet.";
        static readonly int[][] Dirs = { new[] { -1, 0 }, new[] { 0, 1 }, new[] { 1, 0 }, new[] { 0, -1 } };

        // Hooks the runner listens to
        public Action<string> OnSfx;
        public Action<string> OnHint;

        public LevelDef level;
        public string[] map;
        public int rows, cols, padR, padC;
        public List<Tile> starts = new List<Tile>();
        public List<Tile> enemyTemplate = new List<Tile>();
        public Tile beacon;
        public int[,] buildingId;
        public List<List<Tile>> chunks = new List<List<Tile>>();

        public List<Node> prog = new List<Node>();
        public List<Unit> units = new List<Unit>();
        public List<Guard> guards = new List<Guard>();
        public List<Spark> sparks = new List<Spark>();
        public bool[,] known, visibleNow;
        public int knownCount, knownVersion;
        public bool fullVision, beaconSpotted, restored, running, waiting, enemyUnlocked;
        public int exploredPct, totalMoves, steps;
        public float time;
        float _nextWaitSfx;

        // ========================
        // LOADING
        // ========================
        public void LoadMap(WorldMap m, LevelDef def)
        {
            level = def;
            fullVision = def.fullVision;
            map = m.rows;
            rows = map.Length; cols = map[0].Length;
            starts = new List<Tile>(m.starts);
            for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++)
            {
                if (map[r][c] == 'S' && m.starts.Count == 0) starts.Add(new Tile(r, c));
                if (map[r][c] == 'B') beacon = new Tile(r, c);
            }
            enemyTemplate = new List<Tile>(m.enemies);
            AnalyzeBuildings();
            padR = def.hasCamera ? 0 : 10;
            padC = def.hasCamera ? 0 : 8;
        }

        // Groups buildings and notes the chunky ones (a roof tile fully surrounded by roof) so they show up whole once seen.
        void AnalyzeBuildings()
        {
            buildingId = new int[rows, cols];
            for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++) buildingId[r, c] = -1;
            chunks = new List<List<Tile>>();
            int id = 0;
            for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++)
            {
                if (map[r][c] != '#' || buildingId[r, c] != -1) continue;
                var tiles = new List<Tile>();
                var q = new List<Tile> { new Tile(r, c) };
                buildingId[r, c] = id;
                for (int i = 0; i < q.Count; i++)
                {
                    var t = q[i];
                    tiles.Add(t);
                    foreach (var d in Dirs)
                    {
                        int na = t.r + d[0], nb = t.c + d[1];
                        if (InBounds(na, nb) && map[na][nb] == '#' && buildingId[na, nb] == -1) { buildingId[na, nb] = id; q.Add(new Tile(na, nb)); }
                    }
                }
                bool chunky = false;
                foreach (var t in tiles)
                {
                    bool all = true;
                    for (int dr = -1; dr <= 1 && all; dr++) for (int dc = -1; dc <= 1 && all; dc++)
                    {
                        int a = t.r + dr, b = t.c + dc;
                        if (InBounds(a, b) && map[a][b] != '#') all = false;
                    }
                    if (all) { chunky = true; break; }
                }
                if (chunky) chunks.Add(tiles);
                id++;
            }
        }

        // ========================
        // HELPERS
        // ========================
        public bool InBounds(int r, int c) { return r >= 0 && c >= 0 && r < rows && c < cols; }
        public static bool IsSolidCh(char k) { return k == '#' || k == 'T' || k == 'C'; }
        public bool IsWall(int r, int c) { return !InBounds(r, c) || IsSolidCh(map[r][c]); }
        public static bool Adjacent(Tile a, Tile b) { return Math.Abs(a.r - b.r) + Math.Abs(a.c - b.c) == 1; }
        public static int Dist(Tile a, Tile b) { return Math.Abs(a.r - b.r) + Math.Abs(a.c - b.c); }
        void Sfx(string n) { if (OnSfx != null) OnSfx(n); }
        void Hint(string s) { if (OnHint != null) OnHint(s); }

        // Enemy blocks stay locked until the squad has seen its first guard.
        public List<string> LevelBlocks()
        {
            var all = level.palette.ToList();
            if (enemyUnlocked) return all;
            return all.Where(k => k != "cond:enemySpotted" && k != "act:attack").ToList();
        }

        string WaitMessage()
        {
            return (!fullVision && !beaconSpotted)
                ? "Waiting… nobody has spotted the beacon, and nothing tells the squad to scout."
                : WaitMsg;
        }

        // ========================
        // RESET AND VISION
        // ========================
        public void Reset()
        {
            running = false; waiting = false; steps = 0;
            sparks.Clear();
            restored = false; totalMoves = 0;
            units = new List<Unit>();
            for (int i = 0; i < starts.Count; i++) units.Add(MakeUnit(starts[i], i));
            enemyUnlocked = false;
            guards = enemyTemplate.Select(e => new Guard { r = e.r, c = e.c, ux = e.c, uy = e.r, hp = GuardHp, cool = GuardCool / 2 }).ToList();
            known = new bool[rows, cols];
            visibleNow = new bool[rows, cols];
            knownCount = 0; knownVersion++;
            beaconSpotted = false;
            Reveal(false);
        }

        Unit MakeUnit(Tile p, int i)
        {
            return new Unit { id = i, pos = p, ux = p.c, uy = p.r, hue = 195 + (i * 7) % 28, hp = SoldierHp };
        }

        // Sight has no range limit. It only stops at walls.
        public bool Los(int r0, int c0, int r1, int c1)
        {
            int n = Math.Max(Math.Abs(r1 - r0), Math.Abs(c1 - c0));
            for (int i = 1; i < n; i++)
            {
                int r = (int)Math.Round((double)r0 + (r1 - r0) * (double)i / n, MidpointRounding.AwayFromZero);
                int c = (int)Math.Round((double)c0 + (c1 - c0) * (double)i / n, MidpointRounding.AwayFromZero);
                if (IsWall(r, c)) return false;
            }
            return true;
        }

        public void Reveal(bool announce)
        {
            visibleNow = new bool[rows, cols];
            bool wasSpotted = beaconSpotted;
            int fresh = 0;
            for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++)
            {
                bool seen = fullVision;
                if (!seen) foreach (var u in units) { if (Los(u.pos.r, u.pos.c, r, c)) { seen = true; break; } }
                if (!seen) continue;
                visibleNow[r, c] = true;
                if (!known[r, c]) { known[r, c] = true; fresh++; }
                if (r == beacon.r && c == beacon.c) beaconSpotted = true;
            }
            // A big building shows whole only once all four of its walls have been seen; one wall reveals just what is in view
            foreach (var comp in chunks)
            {
                var sideSeen = new bool[4];
                var sideExists = new bool[4];
                bool anyVis = false;
                foreach (var t in comp)
                {
                    if (visibleNow[t.r, t.c]) anyVis = true;
                    for (int d = 0; d < 4; d++)
                    {
                        int na = t.r + Dirs[d][0], nb = t.c + Dirs[d][1];
                        if (!InBounds(na, nb) || map[na][nb] == '#') continue;
                        sideExists[d] = true;
                        if (known[t.r, t.c]) sideSeen[d] = true;
                    }
                }
                bool complete = true;
                for (int d = 0; d < 4; d++) if (sideExists[d] && !sideSeen[d]) complete = false;
                if (!complete) continue;
                foreach (var t in comp)
                {
                    if (!known[t.r, t.c]) { known[t.r, t.c] = true; fresh++; }
                    if (anyVis) visibleNow[t.r, t.c] = true;
                }
            }
            if (fresh > 0) { knownCount += fresh; knownVersion++; }
            exploredPct = Mathf.RoundToInt(100f * knownCount / (rows * cols));
            if (announce && !wasSpotted && beaconSpotted) Sfx("spot");
        }

        // ========================
        // PATHFINDING
        // ========================
        bool Walkable(int r, int c) { return InBounds(r, c) && !(known[r, c] && IsSolidCh(map[r][c])); }
        bool KnownWalkable(int r, int c) { return InBounds(r, c) && known[r, c] && !IsSolidCh(map[r][c]); }

        struct PNode { public int r, c; public float g, f; }
        static bool Better(PNode a, PNode b) { return a.f < b.f || (a.f == b.f && a.g > b.g); }
        static void HeapPush(List<PNode> h, PNode item)
        {
            h.Add(item);
            int i = h.Count - 1;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (!Better(h[i], h[p])) break;
                var tmp = h[p]; h[p] = h[i]; h[i] = tmp;
                i = p;
            }
        }
        static PNode HeapPop(List<PNode> h)
        {
            var top = h[0];
            var last = h[h.Count - 1];
            h.RemoveAt(h.Count - 1);
            if (h.Count > 0)
            {
                h[0] = last;
                int i = 0;
                for (;;)
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < h.Count && Better(h[l], h[m])) m = l;
                    if (r < h.Count && Better(h[r], h[m])) m = r;
                    if (m == i) break;
                    var tmp = h[m]; h[m] = h[i]; h[i] = tmp;
                    i = m;
                }
            }
            return top;
        }

        // A* between two tiles. `pass` decides which tiles may be walked.
        public List<Tile> FindPath(Tile from, Tile to, Func<int, int, bool> pass)
        {
            int N = rows * cols;
            var best = new float[N];
            var came = new int[N];
            for (int i = 0; i < N; i++) { best[i] = float.PositiveInfinity; came[i] = -1; }
            int s = from.r * cols + from.c;
            best[s] = 0;
            var open = new List<PNode>();
            open.Add(new PNode { r = from.r, c = from.c, g = 0, f = Math.Abs(from.r - to.r) + Math.Abs(from.c - to.c) });
            while (open.Count > 0)
            {
                var cur = HeapPop(open);
                int ck = cur.r * cols + cur.c;
                if (cur.g > best[ck]) continue;
                if (cur.r == to.r && cur.c == to.c)
                {
                    var outp = new List<Tile>();
                    for (int k = ck; k != s; k = came[k]) outp.Add(new Tile(k / cols, k % cols));
                    outp.Reverse();
                    return outp;
                }
                foreach (var d in Dirs)
                {
                    int nr = cur.r + d[0], nc = cur.c + d[1];
                    if (!pass(nr, nc)) continue;
                    float ng = cur.g + 1;
                    int ni = nr * cols + nc;
                    if (ng < best[ni])
                    {
                        best[ni] = ng; came[ni] = ck;
                        HeapPush(open, new PNode { r = nr, c = nc, g = ng, f = ng + Math.Abs(nr - to.r) + Math.Abs(nc - to.c) });
                    }
                }
            }
            return null;
        }

        // ========================
        // SCOUTING
        // ========================
        class Samples { public int version = -1; public List<int> list = new List<int>(); public int mult = 1; public List<int> full = new List<int>(); }
        Samples _samp = new Samples();

        Samples GetSamples()
        {
            if (_samp.version == knownVersion) return _samp;
            var full = new List<int>();
            for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++) if (!known[r, c]) full.Add(r * cols + c);
            int mult = Math.Max(1, full.Count / 160);
            var list = new List<int>();
            for (int i = 0; i < full.Count; i += mult) list.Add(full[i]);
            _samp = new Samples { version = knownVersion, list = list, mult = mult, full = full };
            return _samp;
        }

        int GainWith(int r, int c, List<int> list)
        {
            int n = 0;
            foreach (var k in list) if (Los(r, c, k / cols, k % cols)) n++;
            return n;
        }

        // Nearest reachable spot that would reveal a meaningful amount of new ground, away from where squadmates are already heading.
        List<Tile> FindScoutPath(Unit u)
        {
            int N = rows * cols;
            var prev = new int[N];
            for (int i = 0; i < N; i++) prev[i] = -2;
            int s = u.pos.r * cols + u.pos.c;
            prev[s] = -1;
            var queue = new List<int> { s };
            var order = new List<int>();
            var claims = units.Where(o => o != u && o.scoutPath != null && o.scoutPath.Count > 0).Select(o => o.scoutPath[o.scoutPath.Count - 1]).ToList();
            var sm = GetSamples();
            int found = -1, fb = -1;
            for (int qi = 0; qi < queue.Count; qi++)
            {
                int k = queue[qi], r = k / cols, c = k % cols;
                if (k != s)
                {
                    order.Add(k);
                    bool claimed = false;
                    foreach (var q in claims) if (Math.Sqrt((q.r - r) * (q.r - r) + (q.c - c) * (q.c - c)) < 8) { claimed = true; break; }
                    if (!claimed)
                    {
                        int g = GainWith(r, c, sm.list) * sm.mult;
                        if (g >= MinGain) { found = k; break; }
                        if (g > 0 && fb < 0) fb = k;
                    }
                }
                foreach (var d in Dirs)
                {
                    int nr = r + d[0], nc = c + d[1];
                    if (!InBounds(nr, nc) || !known[nr, nc] || IsSolidCh(map[nr][nc])) continue;
                    int ni = nr * cols + nc;
                    if (prev[ni] != -2) continue;
                    prev[ni] = k;
                    queue.Add(ni);
                }
            }
            int goal = found >= 0 ? found : fb;
            if (goal < 0)
            {
                int n = 0;
                foreach (var k in order)
                {
                    if (++n > 500) break;
                    if (GainWith(k / cols, k % cols, sm.full) > 0) { goal = k; break; }
                }
            }
            if (goal < 0) return null;
            var outp = new List<Tile>();
            for (int k = goal; k != s; k = prev[k]) outp.Add(new Tile(k / cols, k % cols));
            outp.Reverse();
            return outp;
        }

        bool ScoutStep(Unit u, out Tile step)
        {
            step = default(Tile);
            bool stale = u.scoutPath == null || u.scoutPath.Count == 0 || !Adjacent(u.scoutPath[0], u.pos);
            if (!stale)
            {
                var last = u.scoutPath[u.scoutPath.Count - 1];
                stale = GainWith(last.r, last.c, GetSamples().full) < 1;
            }
            if (stale) u.scoutPath = FindScoutPath(u);
            if (u.scoutPath == null || u.scoutPath.Count == 0) return false;
            step = u.scoutPath[0];
            u.scoutPath.RemoveAt(0);
            return true;
        }

        // ========================
        // GUARDS AND COMBAT
        // ========================
        public bool EnemySpotted() { return guards.Any(e => e.hp > 0 && known[e.r, e.c]); }

        Guard NearestEnemy(Tile p)
        {
            Guard best = null; int bd = int.MaxValue;
            foreach (var e in guards)
            {
                if (e.hp <= 0 || !known[e.r, e.c]) continue;
                int d = Dist(p, new Tile(e.r, e.c));
                if (d < bd) { bd = d; best = e; }
            }
            return best;
        }

        // The beacon can only be restored once every guard is down.
        public bool MissionDone() { return guards.All(e => e.hp <= 0); }

        // A guard that can see a squad member charges, and keeps charging until it's in reach.
        void GuardChase(Guard e, float dt)
        {
            Unit foe = null;
            var gp = new Tile(e.r, e.c);
            foreach (var u in units) if (foe == null || Dist(u.pos, gp) < Dist(foe.pos, gp)) foe = u;
            if (foe == null || Dist(foe.pos, gp) <= 1) return;
            e.repath -= dt;
            if (e.repath <= 0 || e.path == null || e.path.Count == 0)
            {
                e.repath = 0.5f;
                e.path = FindPath(gp, foe.pos, Walkable);
                if (e.path == null || e.path.Count == 0) return;
                e.tr = e.path[0].r; e.tc = e.path[0].c; e.hasStep = true;
            }
            if (!e.hasStep) { e.tr = e.path[0].r; e.tc = e.path[0].c; e.hasStep = true; }
            float dx = e.tc - e.ux, dy = e.tr - e.uy;
            float d = Mathf.Sqrt(dx * dx + dy * dy), step = GuardSpeed * dt;
            if (d <= step)
            {
                e.ux = e.tc; e.uy = e.tr; e.r = e.tr; e.c = e.tc;
                e.path.RemoveAt(0);
                e.hasStep = false;
            }
            else { e.ux += dx / d * step; e.uy += dy / d * step; }
        }

        void UpdateCombat(float dt)
        {
            if (!enemyUnlocked && guards.Any(e => known[e.r, e.c]))
            {
                enemyUnlocked = true;
                Sfx("spot");
                Hint("Guards spotted. Enemy blocks are now unlocked.");
            }
            foreach (var e in guards)
            {
                if (e.hp <= 0) continue;
                var gp = new Tile(e.r, e.c);
                if (!e.chasing && units.Any(u => Dist(u.pos, gp) <= GuardSight && Los(u.pos.r, u.pos.c, e.r, e.c))) e.chasing = true;
                if (e.chasing) GuardChase(e, dt);
            }
            // Guards strike the nearest soldier in range on a slow beat
            foreach (var e in guards)
            {
                if (e.hp <= 0) continue;
                e.cool -= dt;
                var gp = new Tile(e.r, e.c);
                var target = units.Where(u => Dist(u.pos, gp) <= GuardRange).OrderBy(u => Dist(u.pos, gp)).FirstOrDefault();
                if (target != null && e.cool <= 0)
                {
                    e.cool = GuardCool;
                    target.hp -= 1;
                    Sfx("hit");
                }
            }
            // Soldiers standing next to a guard wear it down
            foreach (var e in guards)
            {
                if (e.hp <= 0) continue;
                var gp = new Tile(e.r, e.c);
                if (units.Any(u => Dist(u.pos, gp) <= 1))
                {
                    e.hp -= SoldierDps * dt;
                    if (e.hp <= 0)
                    {
                        e.hp = 0;
                        Sfx("drop");
                        SpawnSparks(e.c + 0.5f, e.r + 0.5f, 22);
                        int left = guards.Count(g => g.hp > 0);
                        Hint(left > 0 ? "Guard down. " + left + " left." : "Both guards are down. Now restore the beacon.");
                    }
                }
            }
            int before = units.Count;
            units = units.Where(u => u.hp > 0).ToList();
            if (units.Count < before) Sfx("remove");
            if (units.Count == 0) Finish(false, "Your squad was lost to the guards. Try a different order.");
        }

        // ========================
        // RUNNING THE PROGRAM
        // ========================
        bool EvalCond(string name)
        {
            return name == "beaconSpotted" ? beaconSpotted : EnemySpotted();
        }

        ActResult None() { return new ActResult { type = ActType.None }; }

        ActResult RunAct(string name, Unit u)
        {
            if (name == "attack")
            {
                var foe = NearestEnemy(u.pos);
                if (foe == null) return None();
                var fp = new Tile(foe.r, foe.c);
                if (Dist(u.pos, fp) <= 1) return new ActResult { type = ActType.Fight };
                var path = FindPath(u.pos, fp, KnownWalkable) ?? FindPath(u.pos, fp, Walkable);
                return path != null && path.Count > 0 ? new ActResult { type = ActType.Move, tile = path[0] } : None();
            }
            if (name == "walk")
            {
                if (Dist(u.pos, beacon) <= 1) return MissionDone() ? new ActResult { type = ActType.Arrived } : None();
                if (!beaconSpotted) return None();
                var path = FindPath(u.pos, beacon, KnownWalkable) ?? FindPath(u.pos, beacon, Walkable);
                return path != null && path.Count > 0 ? new ActResult { type = ActType.Move, tile = path[0] } : None();
            }
            Tile step;
            return ScoutStep(u, out step) ? new ActResult { type = ActType.Move, tile = step } : None();
        }

        // Runs the first action found, going through conditions and branches top to bottom.
        ActResult? ExecList(List<Node> list, Unit u)
        {
            foreach (var n in list)
            {
                if (n.kind == NodeKind.Act) return RunAct(n.name, u);
                if (n.kind == NodeKind.If)
                {
                    bool ok = n.cond != null && EvalCond(n.cond.name);
                    var r = ExecList(ok ? n.then : n.els, u);
                    if (r.HasValue) return r;
                }
            }
            return null;
        }

        void DecideUnit(Unit u)
        {
            if (Dist(u.pos, beacon) <= 1 && MissionDone()) { Finish(true, Complete); return; }
            if (++steps > StepLimit) { Finish(false, "That took too long. Try a different order."); return; }
            var r = ExecList(prog, u);
            // Nothing to do yet is not an error. The squad waits and checks again.
            if (!r.HasValue || r.Value.type == ActType.None) { u.idle = true; u.idleUntil = time + 0.4f; return; }
            u.idle = false;
            if (r.Value.type == ActType.Arrived) { Finish(true, Complete); return; }
            if (r.Value.type == ActType.Fight) { u.idleUntil = time + 0.15f; return; }
            u.target = r.Value.tile; u.hasTarget = true;
        }

        public void Finish(bool won, string msg)
        {
            running = false; waiting = false;
            if (won)
            {
                restored = true;
                SpawnSparks(beacon.c + 0.5f, beacon.r + 0.5f, 46);
                Sfx("win");
                Hint(level.key == "field" ? msg + " Level 2 is ready, and your program carries over." : msg);
            }
            else { Sfx("fail"); Hint(msg); }
        }

        public void SpawnSparks(float x, float y, int n)
        {
            for (int i = 0; i < n; i++)
            {
                float a = UnityEngine.Random.value * Mathf.PI * 2, s = (30 + UnityEngine.Random.value * 90) / 40f;
                sparks.Add(new Spark { x = x, y = y, vx = Mathf.Cos(a) * s, vy = Mathf.Sin(a) * s - 1f, life = 1.2f + UnityEngine.Random.value * 0.8f });
            }
        }

        // -------------------------------------------------------
        // Tick(dt, time)
        // One frame of the world. Sparks always animate; units only act while running.
        // -------------------------------------------------------
        public void Tick(float dt, float now)
        {
            time = now;
            for (int i = sparks.Count - 1; i >= 0; i--)
            {
                var s = sparks[i];
                s.x += s.vx * dt; s.y += s.vy * dt; s.vy += 0.45f * dt; s.life -= dt;
                if (s.life <= 0) sparks.RemoveAt(i); else sparks[i] = s;
            }
            if (!running) return;
            bool moved = false;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (!u.hasTarget && time >= u.idleUntil) DecideUnit(u);
                if (!running) break;
                if (u.hasTarget)
                {
                    float dx = u.target.c - u.ux, dy = u.target.r - u.uy;
                    float d = Mathf.Sqrt(dx * dx + dy * dy), step = Speed * dt;
                    if (d > 0) { u.faceR = dy / d; u.faceC = dx / d; }
                    if (d <= step)
                    {
                        u.ux = u.target.c; u.uy = u.target.r;
                        u.pos = u.target;
                        u.hasTarget = false;
                        totalMoves++;
                        moved = true;
                        if (u.id == 0) Sfx("step");
                    }
                    else { u.ux += dx / d * step; u.uy += dy / d * step; }
                }
            }
            if (running) UpdateCombat(dt);
            if (moved && running) Reveal(true);
            if (running)
            {
                bool allIdle = units.All(u => u.idle && !u.hasTarget);
                if (allIdle)
                {
                    if (!waiting) { waiting = true; Hint(WaitMessage()); _nextWaitSfx = time + 0.6f; }
                    if (time >= _nextWaitSfx) { Sfx("wait"); _nextWaitSfx = time + 2.4f; }
                }
                else if (waiting) { waiting = false; Hint("Running..."); }
            }
        }
    }
}
