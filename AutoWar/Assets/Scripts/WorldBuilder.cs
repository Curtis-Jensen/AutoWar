using System;
using System.Collections.Generic;

namespace AutoWar
{
    // A grid cell, row first to match the original web game.
    public struct Tile
    {
        public int r, c;
        public Tile(int r, int c) { this.r = r; this.c = c; }
    }

    public class WorldMap
    {
        public string[] rows;
        public List<Tile> starts = new List<Tile>();
        public List<Tile> enemies = new List<Tile>();
    }

    // Describes one mission: map, rules, look, and the blocks it offers.
    public class LevelDef
    {
        public string key;
        public bool fullVision;
        public bool hasCamera;
        public Func<WorldMap> build;
        public int[][] cityV, cityH;   // street column / row ranges (for lane lines)
        public int[] plaza;            // r0, c0, r1, c1
        public string kicker, mission;
        public string[] palette;
    }

    // ========================
    // MAP AND LEVEL BUILDING
    // Map characters: # building   T tree   C wrecked car   S squad start   B beacon
    // ========================
    public static class WorldBuilder
    {
        public const int PlazaR0 = 0;

        // -------------------------------------------------------
        // BuildField()
        // Level 1: a wide strip of city. Tall buildings with a gap at alternating ends force the squad to wind through.
        // -------------------------------------------------------
        public static WorldMap BuildField()
        {
            int R = 7, C = 25;
            char[][] g = NewGrid(R, C);
            Box(g, 0, 3, 5, 5, '#'); Box(g, 1, 8, 6, 10, '#'); Box(g, 0, 13, 5, 15, '#'); Box(g, 1, 18, 6, 20, '#');
            int[][] trees = { new[] { 2, 22 }, new[] { 4, 23 }, new[] { 1, 24 } };
            foreach (var t in trees) g[t[0]][t[1]] = 'T';
            int[][] cars = { new[] { 3, 1 }, new[] { 3, 6 }, new[] { 2, 12 } };
            foreach (var t in cars) g[t[0]][t[1]] = 'C';
            g[0][0] = 'S'; g[6][24] = 'B';
            var map = new WorldMap { rows = ToRows(g) };
            map.starts.Add(new Tile(0, 0));
            return map;
        }

        // -------------------------------------------------------
        // BuildFog()
        // Level 2 terrain: a small ruined district. Two-tile streets, big buildings, beacon in a central plaza.
        // Anything the beacon can't reach is filled in so both squads can always get there.
        // -------------------------------------------------------
        public static WorldMap BuildFog()
        {
            int R = 23, C = 35, BR = 11, BC = 17;
            char[][] g = NewGrid(R, C);
            int[][] rowsB = { new[] { 2, 6 }, new[] { 9, 13 }, new[] { 16, 20 } };
            int[][] colsB = { new[] { 2, 7 }, new[] { 10, 15 }, new[] { 18, 23 }, new[] { 26, 31 } };
            foreach (var rb in rowsB) foreach (var cb in colsB) Box(g, rb[0], cb[0], rb[1], cb[1], '#');
            Box(g, 9, 14, 13, 15, '.'); Box(g, 9, 18, 13, 19, '.');   // open the plaza around the beacon
            Box(g, 6, 18, 6, 23, '.'); Box(g, 16, 10, 16, 15, '.');   // vary the skyline a little
            Box(g, 9, 7, 13, 7, '.'); Box(g, 16, 26, 20, 26, '.');
            int[][] trees = { new[] { 10, 15 }, new[] { 12, 19 }, new[] { 13, 15 } };
            foreach (var t in trees) Set(g, t[0], t[1], 'T');
            int[][] cars = { new[] { 1, 5 }, new[] { 8, 12 }, new[] { 15, 28 }, new[] { 12, 25 }, new[] { 5, 17 }, new[] { 19, 9 } };
            foreach (var t in cars) Set(g, t[0], t[1], 'C');

            Set(g, BR, BC, '.');
            var seen = new bool[R, C];
            var q = new List<int[]> { new[] { BR, BC } };
            seen[BR, BC] = true;
            int[][] d4 = { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 } };
            for (int i = 0; i < q.Count; i++)
            {
                int r = q[i][0], c = q[i][1];
                foreach (var d in d4)
                {
                    int nr = r + d[0], nc = c + d[1];
                    if (nr >= 0 && nc >= 0 && nr < R && nc < C && !seen[nr, nc] && g[nr][nc] == '.')
                    {
                        seen[nr, nc] = true; q.Add(new[] { nr, nc });
                    }
                }
            }
            for (int r = 0; r < R; r++) for (int c = 0; c < C; c++) if (g[r][c] == '.' && !seen[r, c]) g[r][c] = '#';
            g[BR][BC] = 'B';

            var map = new WorldMap { rows = ToRows(g) };
            int[][] want = { new[] { 1, 1 }, new[] { 21, 33 } };
            foreach (var w in want)
            {
                int bestR = 0, bestC = 0, bd = 1000000;
                for (int r = 0; r < R; r++) for (int c = 0; c < C; c++)
                {
                    if (!seen[r, c] || (r == BR && c == BC)) continue;
                    int dd = Math.Abs(r - w[0]) + Math.Abs(c - w[1]);
                    if (dd < bd) { bd = dd; bestR = r; bestC = c; }
                }
                map.starts.Add(new Tile(bestR, bestC));
            }
            return map;
        }

        // Level 3 (shown as mission 02): the same district with two guards posted in the plaza.
        public static WorldMap BuildCombat()
        {
            var w = BuildFog();
            w.enemies.Add(new Tile(9, 17));
            w.enemies.Add(new Tile(13, 17));
            return w;
        }

        public static readonly Dictionary<string, LevelDef> Levels = new Dictionary<string, LevelDef>
        {
            { "field", new LevelDef {
                key = "field", fullVision = true, hasCamera = false, build = BuildField,
                cityV = new[] { new[] { 6, 7 }, new[] { 11, 12 }, new[] { 16, 17 } },
                cityH = new[] { new[] { 0, 0 }, new[] { 6, 6 } },
                plaza = new[] { 0, 21, 6, 24 },
                kicker = "Mission 01 · Secured Field",
                mission = "Your squad holds this secured district, so the sensors see everything. A distress beacon is hidden in the ruins at the far southeast corner. Reach it and restore the signal.",
                palette = new[] { "act:walk" } } },
            { "fog", new LevelDef {
                key = "fog", fullVision = false, hasCamera = true, build = BuildCombat,
                cityV = new[] { new[] { 0, 1 }, new[] { 8, 9 }, new[] { 16, 17 }, new[] { 24, 25 } },
                cityH = new[] { new[] { 0, 1 }, new[] { 7, 8 }, new[] { 14, 15 }, new[] { 21, 22 } },
                plaza = new[] { 9, 14, 13, 19 },
                kicker = "Mission 02 · Fog of War",
                mission = "Smoke covers the ruined city, and your two squads were dropped in opposite corners. Two guards are hidden around the beacon. Clear them before you restore the signal.",
                palette = new[] { "if", "cond:beaconSpotted", "cond:enemySpotted", "act:walk", "act:scout", "act:attack" } } }
        };

        static char[][] NewGrid(int R, int C)
        {
            var g = new char[R][];
            for (int r = 0; r < R; r++) { g[r] = new char[C]; for (int c = 0; c < C; c++) g[r][c] = '.'; }
            return g;
        }
        static void Set(char[][] g, int r, int c, char ch)
        {
            if (r >= 0 && c >= 0 && r < g.Length && c < g[0].Length) g[r][c] = ch;
        }
        static void Box(char[][] g, int r0, int c0, int r1, int c1, char ch)
        {
            for (int r = r0; r <= r1; r++) for (int c = c0; c <= c1; c++) Set(g, r, c, ch);
        }
        static string[] ToRows(char[][] g)
        {
            var o = new string[g.Length];
            for (int i = 0; i < g.Length; i++) o[i] = new string(g[i]);
            return o;
        }
    }
}
