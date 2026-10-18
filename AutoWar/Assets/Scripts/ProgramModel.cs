using System;
using System.Collections.Generic;
using System.Text;

namespace AutoWar
{
    public enum NodeKind { If, Act, Cond }

    // One block in the player's program. An If holds a condition plus Then/Else lists.
    public class Node
    {
        public int id;
        public NodeKind kind;
        public string name;
        public Node cond;
        public List<Node> then = new List<Node>();
        public List<Node> els = new List<Node>();
    }

    public class BlockDef
    {
        public NodeKind kind;
        public string name, label;
        public BlockDef(NodeKind kind, string name, string label) { this.kind = kind; this.name = name; this.label = label; }
    }

    public class ParseResult
    {
        public bool ok;
        public List<Node> prog;
        public string msg;
        public int line;
    }

    // ========================
    // PROGRAM TREE: building, editing, printing, parsing
    // ========================
    public static class ProgramModel
    {
        static int _counter;
        public static int NextId() { return ++_counter; }

        public static readonly Dictionary<string, BlockDef> Defs = new Dictionary<string, BlockDef>
        {
            { "if", new BlockDef(NodeKind.If, null, "If … then … else") },
            { "cond:beaconSpotted", new BlockDef(NodeKind.Cond, "beaconSpotted", "Beacon is spotted") },
            { "act:walk", new BlockDef(NodeKind.Act, "walk", "Walk to beacon") },
            { "cond:enemySpotted", new BlockDef(NodeKind.Cond, "enemySpotted", "Enemy is spotted") },
            { "act:scout", new BlockDef(NodeKind.Act, "scout", "Scout") },
            { "act:attack", new BlockDef(NodeKind.Act, "attack", "Attack nearest enemy") }
        };

        static readonly Dictionary<string, string> Call = new Dictionary<string, string>
        {
            { "beaconSpotted", "beaconSpotted()" }, { "enemySpotted", "enemySpotted()" },
            { "walk", "walkTo(beacon)" }, { "scout", "scout()" }, { "attack", "attack()" }
        };

        public static Node Make(string key)
        {
            var d = Defs[key];
            var n = new Node { id = NextId(), kind = d.kind, name = d.name };
            return n;
        }

        public static string Label(Node n)
        {
            if (n.kind == NodeKind.Act) return Defs["act:" + n.name].label;
            return Defs["cond:" + n.name].label;
        }

        public static Node FindNode(List<Node> list, int id)
        {
            foreach (var n in list)
            {
                if (n.id == id) return n;
                if (n.kind == NodeKind.If)
                {
                    if (n.cond != null && n.cond.id == id) return n.cond;
                    var inner = FindNode(n.then, id) ?? FindNode(n.els, id);
                    if (inner != null) return inner;
                }
            }
            return null;
        }

        public static bool RemoveNode(List<Node> list, int id)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var n = list[i];
                if (n.id == id) { list.RemoveAt(i); return true; }
                if (n.kind == NodeKind.If)
                {
                    if (n.cond != null && n.cond.id == id) { n.cond = null; return true; }
                    if (RemoveNode(n.then, id) || RemoveNode(n.els, id)) return true;
                }
            }
            return false;
        }

        // True when `list` lives somewhere inside `node` (so dropping node into list would create a cycle).
        public static bool ListWithin(Node node, List<Node> list)
        {
            if (node.kind != NodeKind.If) return false;
            if (node.then == list || node.els == list) return true;
            foreach (var c in node.then) if (ListWithin(c, list)) return true;
            foreach (var c in node.els) if (ListWithin(c, list)) return true;
            return false;
        }

        public static bool HasAct(List<Node> list, string name)
        {
            foreach (var n in list)
            {
                if (n.kind == NodeKind.Act && n.name == name) return true;
                if (n.kind == NodeKind.If && (HasAct(n.then, name) || HasAct(n.els, name))) return true;
            }
            return false;
        }

        // When a level changes, drop blocks that level doesn't offer.
        public static void Prune(List<Node> list, IList<string> allowed)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var n = list[i];
                string k = n.kind == NodeKind.If ? "if" : (n.kind == NodeKind.Act ? "act:" : "cond:") + n.name;
                if (n.kind == NodeKind.If && !allowed.Contains("if"))
                {
                    Prune(n.then, allowed);
                    list.RemoveAt(i);
                    list.InsertRange(i, n.then);
                    continue;
                }
                if (!allowed.Contains(k)) { list.RemoveAt(i); continue; }
                if (n.kind == NodeKind.If)
                {
                    if (n.cond != null && !allowed.Contains("cond:" + n.cond.name)) n.cond = null;
                    Prune(n.then, allowed);
                    Prune(n.els, allowed);
                }
            }
        }

        // ---------- code generation ----------
        public static string Generate(List<Node> list)
        {
            var sb = new StringBuilder();
            Gen(list, "", sb);
            return sb.ToString().TrimEnd('\n');
        }

        static void Gen(List<Node> list, string ind, StringBuilder sb)
        {
            foreach (var n in list)
            {
                if (n.kind == NodeKind.Act) sb.Append(ind).Append(Call[n.name]).Append(";\n");
                else if (n.kind == NodeKind.If)
                {
                    string c = n.cond != null ? Call[n.cond.name] : "false";
                    sb.Append(ind).Append("if (").Append(c).Append(") {\n");
                    Gen(n.then, ind + "  ", sb);
                    if (n.els.Count > 0)
                    {
                        sb.Append(ind).Append("} else {\n");
                        Gen(n.els, ind + "  ", sb);
                    }
                    sb.Append(ind).Append("}\n");
                }
            }
        }

        // ---------- parsing ----------
        class ParseError : Exception
        {
            public int line;
            public ParseError(string msg, int line) : base(msg) { this.line = line; }
        }

        struct Tok { public bool isId; public string v; public int line; }

        static List<Tok> Tokenize(string src)
        {
            var toks = new List<Tok>();
            int line = 1, i = 0;
            while (i < src.Length)
            {
                char ch = src[i];
                if (ch == '\n') { line++; i++; continue; }
                if (char.IsWhiteSpace(ch)) { i++; continue; }
                if (ch == '/' && i + 1 < src.Length && src[i + 1] == '/') { while (i < src.Length && src[i] != '\n') i++; continue; }
                if (char.IsLetter(ch) || ch == '_')
                {
                    int j = i;
                    while (j < src.Length && (char.IsLetterOrDigit(src[j]) || src[j] == '_')) j++;
                    toks.Add(new Tok { isId = true, v = src.Substring(i, j - i), line = line });
                    i = j;
                    continue;
                }
                if ("(){};!".IndexOf(ch) >= 0) { toks.Add(new Tok { isId = false, v = ch.ToString(), line = line }); i++; continue; }
                throw new ParseError("Unexpected character “" + ch + "”", line);
            }
            return toks;
        }

        // Turns typed code into program nodes. `allowed` is the list of block keys this level offers.
        public static ParseResult Parse(string src, IList<string> allowed)
        {
            try
            {
                var toks = Tokenize(src);
                int i = 0;
                Func<int> lastLine = () => toks.Count > 0 ? toks[toks.Count - 1].line : 1;
                Func<Tok?> peek = () => i < toks.Count ? toks[i] : (Tok?)null;
                Func<Tok> next = () => toks[i++];
                Action<string, Tok?> fail = (msg, tk) => { throw new ParseError(msg, tk.HasValue ? tk.Value.line : lastLine()); };
                Action<string> expect = v =>
                {
                    var t = peek();
                    if (!t.HasValue || t.Value.v != v)
                        fail("Expected “" + v + "”" + (t.HasValue ? " but found “" + t.Value.v + "”" : " but the code ended"), t);
                    next();
                };
                Func<List<Node>> body = null;
                Func<Node> stmt = null;

                stmt = () =>
                {
                    var t = peek();
                    if (!t.HasValue) fail("The code ended too early", null);
                    var tk = t.Value;
                    if (!tk.isId) fail("Unexpected “" + tk.v + "”", tk);
                    if (tk.v == "if")
                    {
                        if (!allowed.Contains("if")) fail("“if” isn’t available in this level", tk);
                        next();
                        expect("(");
                        var c = peek();
                        if (c.HasValue && c.Value.v == "!") fail("“!” isn’t supported yet. Use an else instead.", c);
                        if (!c.HasValue || !c.Value.isId) fail("Expected a condition inside the parentheses", c ?? tk);
                        string cname = c.Value.v;
                        if (cname != "beaconSpotted" && cname != "enemySpotted") fail("“" + cname + "” isn’t a known condition", c);
                        if (!allowed.Contains("cond:" + cname)) fail("“" + cname + "()” isn’t available in this level", c);
                        next(); expect("("); expect(")"); expect(")");
                        var cond = new Node { id = NextId(), kind = NodeKind.Cond, name = cname };
                        var th = body();
                        var el = new List<Node>();
                        var p = peek();
                        if (p.HasValue && p.Value.v == "else")
                        {
                            next();
                            var p2 = peek();
                            if (p2.HasValue && p2.Value.v == "if") el.Add(stmt()); else el = body();
                        }
                        return new Node { id = NextId(), kind = NodeKind.If, cond = cond, then = th, els = el };
                    }
                    if (tk.v == "else") fail("“else” needs an “if” right before it", tk);
                    if (tk.v == "while") fail("The loop is already built in. Write what happens inside it.", tk);
                    next();
                    string name = null;
                    if (tk.v == "walkTo")
                    {
                        expect("(");
                        var a = peek();
                        if (!a.HasValue || a.Value.v != "beacon") fail("Use walkTo(beacon)", a ?? tk);
                        next(); expect(")");
                        name = "walk";
                    }
                    else if (tk.v == "scout") { expect("("); expect(")"); name = "scout"; }
                    else if (tk.v == "attack") { expect("("); expect(")"); name = "attack"; }
                    else fail("“" + tk.v + "” isn’t a known action", tk);
                    if (!allowed.Contains("act:" + name)) fail("“" + tk.v + "()” isn’t available in this level", tk);
                    var semi = peek();
                    if (semi.HasValue && semi.Value.v == ";") next();
                    return new Node { id = NextId(), kind = NodeKind.Act, name = name };
                };

                body = () =>
                {
                    var p = peek();
                    if (p.HasValue && p.Value.v == "{")
                    {
                        next();
                        var outList = new List<Node>();
                        while (peek().HasValue && peek().Value.v != "}") outList.Add(stmt());
                        expect("}");
                        return outList;
                    }
                    return new List<Node> { stmt() };
                };

                var result = new List<Node>();
                while (i < toks.Count)
                {
                    if (toks[i].v == "}") fail("Unexpected “}”", toks[i]);
                    result.Add(stmt());
                }
                return new ParseResult { ok = true, prog = result };
            }
            catch (ParseError e)
            {
                return new ParseResult { ok = false, msg = e.Message, line = e.line };
            }
        }
    }
}
