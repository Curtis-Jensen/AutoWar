using System.Collections.Generic;
using UnityEngine;

namespace AutoWar
{
    // ========================
    // GAME RUNNER
    // Owns the simulator, renderer, synth and HUD, and applies every player action
    // (run, reset, switch level, add / move / remove blocks, type code). Spawns itself on play.
    // ========================
    public class GameRunner : MonoBehaviour
    {
        public SquadSimulator sim;
        public WorldRenderer view;
        public AudioSynth synth;
        public HudDrawer hud;

        public string levelKey = "field";
        public string hint = "";
        public string codeText = "";
        public string codeError = "";
        public bool needsViewReset;

        public List<Node> Prog { get { return sim.prog; } }

        // -------------------------------------------------------
        // Boot()
        // Creates the game object at startup so the scene needs no setup.
        // -------------------------------------------------------
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (FindObjectOfType<GameRunner>() != null) return;
            var go = new GameObject("AutoWar");
            go.AddComponent<GameRunner>();
        }

        // -------------------------------------------------------
        // Awake()
        // Builds the simulator and view components and loads level 1.
        // -------------------------------------------------------
        void Awake()
        {
            Application.targetFrameRate = 60;
            sim = new SquadSimulator();
            view = gameObject.AddComponent<WorldRenderer>();
            view.Init(sim);

            var audioGo = new GameObject("Audio");
            audioGo.transform.SetParent(transform, false);
            synth = audioGo.AddComponent<AudioSynth>();

            hud = gameObject.AddComponent<HudDrawer>();
            hud.Init(this);

            sim.OnSfx = n => synth.Sfx(n);
            sim.OnHint = SetHint;
            SetLevel("field", true);
        }

        void Update()
        {
            sim.Tick(Mathf.Min(0.05f, Time.deltaTime), Time.time);
            view.HandleCameraInput(hud.IsPointerOverUi());
        }

        public void SetHint(string s) { hint = s; }

        // ========================
        // LEVELS, RUN, RESET
        // ========================
        public void SetLevel(string key, bool quiet)
        {
            sim.running = false;
            levelKey = key;
            var def = WorldBuilder.Levels[key];
            sim.LoadMap(def.build(), def);
            view.BuildLevel();
            synth.SetLevel(key);
            needsViewReset = true;
            ProgramModel.Prune(sim.prog, def.palette);
            ResetAndRender(false);
            if (!quiet) synth.Sfx("tab");
        }

        public void ResetAndRender(bool skipCode)
        {
            sim.Reset();
            UpdateHint();
            if (!skipCode) SetCodeText(ProgramModel.Generate(sim.prog));
        }

        public void Run()
        {
            if (sim.running) { sim.running = false; SetHint("Stopped. Press Run to start over."); return; }
            ResetAndRender(false);
            if (sim.prog.Count == 0) { SetHint("Add a block first."); return; }
            synth.Sfx("run");
            sim.running = true;
            SetHint("Running...");
        }

        public void ClearProgram()
        {
            sim.prog.Clear();
            synth.Sfx("remove");
            ResetAndRender(false);
        }

        public void SetMusic(bool on) { synth.SetMusic(on); }
        public void SetSfx(bool on) { synth.SetSfx(on); }

        // ========================
        // HINTS
        // ========================
        void UpdateHint()
        {
            var prog = sim.prog;
            Node firstIf = prog.Find(n => n.kind == NodeKind.If);
            if (levelKey == "fog" && sim.enemyTemplate.Count > 0)
            {
                if (prog.Count == 0) { SetHint("Guards are posted around the beacon. Add an If block to start."); return; }
                if (sim.enemyUnlocked && !ProgramModel.HasAct(prog, "attack")) { SetHint("Guards are posted around the beacon. Add “Attack nearest enemy” to deal with them."); return; }
                if (!ProgramModel.HasAct(prog, "scout")) { SetHint("Smoke still hides the beacon. Add “Scout” so the squad can find it."); return; }
                if (!ProgramModel.HasAct(prog, "walk")) { SetHint("Add “Walk to beacon” for once the squad has found it."); return; }
                SetHint("Press Run.");
                return;
            }
            if (levelKey == "field")
            {
                SetHint(prog.Count == 0 ? "Drag “Walk to beacon” into the program, then press Run." : "Press Run.");
                return;
            }
            if (prog.Count == 0) SetHint("Drag an If block into the program, or type code on the right.");
            else if (firstIf == null) SetHint("Smoke hides the beacon, so walking alone won’t work. Add an If block: if the beacon is spotted, walk to it, else scout.");
            else if (firstIf.cond == null) SetHint("Drag “Beacon is spotted” into the If’s condition slot.");
            else if (firstIf.then.Count == 0) SetHint("Drag “Walk to beacon” into the Then area.");
            else if (!ProgramModel.HasAct(prog, "scout")) SetHint("Smoke hides the beacon. Drag “Scout” into the Else area.");
            else SetHint("Press Run.");
        }

        // ========================
        // PROGRAM EDITING
        // ========================
        public void AddBlock(string key)
        {
            var d = ProgramModel.Defs[key];
            if (d.kind == NodeKind.Cond) { SetHint("Drag “" + d.label + "” into an If block’s condition slot."); return; }
            sim.prog.Add(ProgramModel.Make(key));
            synth.Sfx("drop");
            ResetAndRender(false);
        }

        // Drop an action / If (new from the palette, or "move:<id>") into a list, before node `beforeId` (0 = at the end).
        public void DropOnList(string data, List<Node> list, int beforeId)
        {
            Node node;
            if (data.StartsWith("move:"))
            {
                int id = int.Parse(data.Substring(5));
                node = ProgramModel.FindNode(sim.prog, id);
                if (node == null || node.id == beforeId || node.kind == NodeKind.Cond) return;
                if (node.kind == NodeKind.If && ProgramModel.ListWithin(node, list)) return;
                ProgramModel.RemoveNode(sim.prog, id);
            }
            else
            {
                if (!ProgramModel.Defs.ContainsKey(data) || !sim.LevelBlocks().Contains(data)) return;
                node = ProgramModel.Make(data);
                if (node.kind == NodeKind.Cond) return;
            }
            int idx = beforeId != 0 ? list.FindIndex(n => n.id == beforeId) : -1;
            if (idx < 0) list.Add(node); else list.Insert(idx, node);
            synth.Sfx("drop");
            ResetAndRender(false);
        }

        public void DropOnCond(string data, Node ifNode)
        {
            Node node;
            if (data.StartsWith("move:"))
            {
                int id = int.Parse(data.Substring(5));
                node = ProgramModel.FindNode(sim.prog, id);
                if (node == null || node.kind != NodeKind.Cond) return;
                ProgramModel.RemoveNode(sim.prog, id);
            }
            else
            {
                if (!ProgramModel.Defs.ContainsKey(data) || ProgramModel.Defs[data].kind != NodeKind.Cond || !sim.LevelBlocks().Contains(data)) return;
                node = ProgramModel.Make(data);
            }
            ifNode.cond = node;
            synth.Sfx("drop");
            ResetAndRender(false);
        }

        public void RemoveNode(int id)
        {
            ProgramModel.RemoveNode(sim.prog, id);
            synth.Sfx("remove");
            ResetAndRender(false);
        }

        // ========================
        // CODE VIEW (typed code <-> blocks)
        // ========================
        void SetCodeText(string text) { codeText = text; codeError = ""; }

        // Typing updates the blocks live; a bad line shows a message and keeps the last good program.
        public void OnCodeTyped(string text)
        {
            codeText = text;
            var res = ProgramModel.Parse(text, sim.LevelBlocks());
            if (res.ok)
            {
                codeError = "";
                sim.prog = res.prog;
                ResetAndRender(true);
            }
            else codeError = "Line " + res.line + ": " + res.msg;
        }
    }
}
