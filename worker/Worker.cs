using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using SabberStoneCore.Actions;
using SabberStoneCore.Config;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;
using SabberStoneCore.Tasks;
using SabberStoneCore.Tasks.PlayerTasks;
using SabberStoneCore.Tasks.SimpleTasks;

namespace SoloStoneMcp
{
    public sealed class Session
    {
        public Game G;
        public JsonObject Setup;
        public long Seed;
        public readonly Dictionary<string, int> Aliases = new Dictionary<string, int>();
        public readonly Dictionary<string, (byte[] snap, Dictionary<string, int> aliases)> Checkpoints = new Dictionary<string, (byte[], Dictionary<string, int>)>();
        public int StepStart; // PowerHistory.Full index where the last step began
    }

    public sealed partial class Worker
    {
        readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        int nextSession = 1;

        public JsonNode Handle(string cmd, JsonObject req)
        {
            switch (cmd)
            {
                case "ping": return new JsonObject { ["pong"] = true, ["cards"] = Cards.Count };
                case "cards": return CardSearch(req);
                case "new": return New(req);
                case "close": sessions.Remove(Str(req, "session")); return true;
                case "sessions": return new JsonArray(sessions.Keys.Select(k => (JsonNode)k).ToArray());
                case "state": return State(Get(req), Bool(req, "full"));
                case "entity": { var s = Get(req); return EntityFull(s, Resolve(s, s.G.CurrentPlayer, Str(req, "ref"))); }
                case "options": return OptionsOf(Get(req), req["by"] == null ? null : Player(Get(req), (string)req["by"]));
                case "act": return Act(Get(req), req["steps"] as JsonArray ?? new JsonArray(req["step"]?.DeepClone()));
                case "history": return History(Get(req), req);
                case "assert": { var s = Get(req); return Expect(s, req["expect"] as JsonArray, s.StepStart); }
                case "checkpoint": return Checkpoint(Get(req), Str(req, "name") ?? "default");
                case "restore": return Restore(Get(req), Str(req, "name") ?? "default");
                case "scenario": return Scenario(req["scenario"].AsObject(), Bool(req, "keep"));
                case "seed_search": return SeedSearch(req);
                case "fuzz": return Fuzz(req);
                case "replay": return Replay(req);
                case "diag": return Diag(Get(req));
                default: throw new UserError("unknown command " + cmd);
            }
        }

        // ---------- small JSON helpers ----------
        internal static string Str(JsonObject o, string k) => o?[k] is JsonValue v && v.TryGetValue(out string s) ? s : o?[k]?.ToString();
        internal static bool Bool(JsonObject o, string k) => o?[k] is JsonValue v && v.TryGetValue(out bool b) && b;
        internal static int? Int(JsonObject o, string k) => o?[k] is JsonValue v && v.TryGetValue(out int i) ? i : (int?)null;
        internal static long? Long(JsonObject o, string k) => o?[k] is JsonValue v && v.TryGetValue(out long i) ? i : (long?)null;
        internal static IEnumerable<JsonNode> Arr(JsonObject o, string k) => o?[k] is JsonArray a ? a : o?[k] != null ? new[] { o[k] } : Enumerable.Empty<JsonNode>();

        Session Get(JsonObject req)
        {
            string id = Str(req, "session");
            if (id == null && sessions.Count == 1) return sessions.Values.First();
            if (id == null || !sessions.TryGetValue(id, out var s)) throw new UserError("no session " + (id ?? "(none given)") + "; open sessions: " + string.Join(",", sessions.Keys));
            return s;
        }

        // ---------- cards ----------
        JsonNode CardSearch(JsonObject req)
        {
            string q = (Str(req, "query") ?? "").Trim();
            int limit = Int(req, "limit") ?? 25;
            bool collectibleOnly = Bool(req, "collectible");
            var exact = Cards.All.Where(c => string.Equals(c.Id, q, StringComparison.OrdinalIgnoreCase)).ToList();
            IEnumerable<Card> hits = exact.Count > 0 ? exact : Cards.All.Where(c =>
                (c.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || (Bool(req, "text") && (c.Text ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0));
            if (collectibleOnly) hits = hits.Where(c => c.Collectible);
            hits = hits.OrderByDescending(c => string.Equals(c.Name, q, StringComparison.OrdinalIgnoreCase)).ThenByDescending(c => c.Collectible).ThenBy(c => c.Name.Length);
            var arr = new JsonArray();
            foreach (var c in hits.Take(limit))
                arr.Add(new JsonObject
                {
                    ["id"] = c.Id, ["name"] = c.Name, ["type"] = c.Type.ToString(), ["class"] = c.Class.ToString(), ["cost"] = c.Cost,
                    ["atk"] = c[GameTag.ATK], ["health"] = c[GameTag.HEALTH], ["collectible"] = c.Collectible, ["set"] = c.Set.ToString(),
                    ["secret"] = c[GameTag.SECRET] == 1, ["text"] = Clean(c.Text)
                });
            return arr;
        }

        internal static string Clean(string t) => t == null ? null : System.Text.RegularExpressions.Regex.Replace(t, "<[^>]+>|\\[x\\]", "").Replace("\n", " ").Replace("$", "").Replace("#", "");

        // ---------- game setup ----------
        internal static CardClass ParseClass(string s, CardClass dflt)
        {
            if (string.IsNullOrEmpty(s)) return dflt;
            if (Enum.TryParse<CardClass>(s.Trim().ToUpperInvariant().Replace(" ", "").Replace("DEMONHUNTER", "DEMONHUNTER"), out var c)) return c;
            throw new UserError("unknown class " + s + "; use e.g. MAGE, HUNTER, PALADIN, DEMONHUNTER, DEATHKNIGHT");
        }

        internal static Card CardOf(string idOrName)
        {
            if (string.IsNullOrEmpty(idOrName)) throw new UserError("empty card id");
            var c = Cards.FromId(idOrName);
            if (c != null) return c;
            c = Cards.All.Where(x => string.Equals(x.Name, idOrName, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.Collectible).FirstOrDefault();
            if (c != null) return c;
            throw new UserError("unknown card " + idOrName + " (use card_search)");
        }

        JsonNode New(JsonObject req)
        {
            var setup = req["setup"]?.AsObject() ?? new JsonObject();
            var s = Build(setup, Long(req, "seed"));
            string id = Str(req, "session") ?? ("g" + nextSession++);
            sessions[id] = s;
            var st = State(s, false).AsObject();
            st["session"] = id;
            st["seed"] = s.Seed;
            return st;
        }

        internal Session Build(JsonObject setup, long? seedOverride = null)
        {
            long seed = seedOverride ?? Long(setup, "seed") ?? 1;
            var me = setup["me"]?.AsObject() ?? new JsonObject();
            var opp = setup["opp"]?.AsObject() ?? new JsonObject();
            string first = Str(setup, "first") ?? "me";
            List<Card> Deck(JsonObject p) => p["deck"] is JsonArray d ? d.Select(x => CardOf((string)x)).ToList() : null;
            var config = new GameConfig
            {
                StartPlayer = first == "opp" ? 2 : 1,
                Player1HeroClass = ParseClass(Str(me, "class"), CardClass.MAGE),
                Player2HeroClass = ParseClass(Str(opp, "class"), CardClass.HUNTER),
                Player1Deck = Deck(me), Player2Deck = Deck(opp),
                FillDecks = true, FillDecksPredictably = false,
                Shuffle = !Bool(setup, "noShuffle"), Logging = false, History = true,
                SkipMulligan = !Bool(setup, "mulligan"), RandomSeed = seed,
                FormatType = Str(setup, "format") == "standard" ? FormatType.FT_STANDARD : FormatType.FT_WILD,
            };
            if (Str(me, "hero") != null) config.Player1HeroCard = CardOf(Str(me, "hero"));
            if (Str(opp, "hero") != null) config.Player2HeroCard = CardOf(Str(opp, "hero"));
            var g = new Game(config);
            g.StartGame();
            var s = new Session { G = g, Setup = setup, Seed = seed };
            Apply(s, g.Player1, me);
            Apply(s, g.Player2, opp);
            if (setup["start"] != null) { } // reserved
            g.DeathProcessingAndAuraUpdate();
            s.StepStart = g.PowerHistory.Full.Count;
            return s;
        }

        void Apply(Session s, Controller p, JsonObject o)
        {
            var g = s.G;
            bool current = p == g.CurrentPlayer;
            if (Bool(o, "handOnly") || o["hand"] is JsonArray && Bool(o, "replaceHand"))
                foreach (var card in p.HandZone.ToList()) { p.HandZone.Remove(card); p.DeckZone.Add(card); }
            if (Bool(o, "emptyDeck")) foreach (var card in p.DeckZone.ToList()) p.DeckZone.Remove(card);
            // Last in the deck zone is drawn next, so deckTop is added in reverse.
            var top = Arr(o, "deckTop").ToList();
            for (int i = top.Count - 1; i >= 0; i--) Alias(s, top[i], Entity.FromCard(p, CardOf(SpecCard(top[i])), zone: p.DeckZone));
            foreach (var h in Arr(o, "hand")) Alias(s, h, Entity.FromCard(p, CardOf(SpecCard(h)), zone: p.HandZone));
            foreach (var b in Arr(o, "board"))
            {
                var body = Entity.FromCard(p, CardOf(SpecCard(b)));
                if (body is Minion m)
                {
                    Generic.SummonBlock(g, m, -1, null);
                    bool ready = !(b is JsonObject bo && bo["ready"] is JsonValue rv && rv.TryGetValue(out bool r) && !r);
                    if (ready) { m.IsExhausted = false; m[GameTag.NUM_TURNS_IN_PLAY] = Math.Max(1, m[GameTag.NUM_TURNS_IN_PLAY]); }
                    if (b is JsonObject bj && Int(bj, "damage") is int dmg) m.Damage = dmg;
                }
                else p.BoardZone.Add((BoardObject)body);
                Alias(s, b, body);
                ApplyTags(body, b as JsonObject);
            }
            foreach (var x in Arr(o, "secrets"))
            {
                var secret = Entity.FromCard(p, CardOf(SpecCard(x)), zone: p.SecretZone);
                secret.Card.Power?.Trigger?.Activate(secret);
                secret.IsExhausted = current; // live on the opponent's turn, like a secret played on an earlier turn
                Alias(s, x, secret);
            }
            if (Str(o, "weapon") is string w)
            {
                var weapon = (Weapon)Entity.FromCard(p, CardOf(w));
                p.Hero.AddWeapon(weapon);
                weapon.Card.Power?.Trigger?.Activate(weapon);
                weapon.Card.Power?.Aura?.Activate(weapon);
            }
            if (Int(o, "mana") is int mana)
            {
                // The current player gets it now; the other gets it at its next turn start (+1 there).
                p.BaseMana = current ? mana : Math.Max(0, mana - 1);
                p.UsedMana = 0;
            }
            if (Int(o, "health") is int hp) p.Hero.Damage = Math.Max(0, p.Hero.BaseHealth - hp);
            if (Int(o, "armor") is int armor) p.Hero.Armor = armor;
            if (o["heroTags"] is JsonObject ht) ApplyTags(p.Hero, ht);
        }

        static string SpecCard(JsonNode spec) => spec is JsonObject o ? Str(o, "card") : (string)spec;

        static void ApplyTags(IPlayable e, JsonObject spec)
        {
            if (spec?["tags"] is JsonObject tags)
                foreach (var kv in tags) e[ParseTag(kv.Key)] = (int)kv.Value;
        }

        internal static GameTag ParseTag(string name)
        {
            if (int.TryParse(name, out int n)) return (GameTag)n;
            if (Enum.TryParse<GameTag>(name, true, out var t)) return t;
            throw new UserError("unknown tag " + name);
        }

        static void Alias(Session s, JsonNode spec, IPlayable e)
        {
            if (spec is JsonObject o && Str(o, "as") is string a) s.Aliases[a.TrimStart('@')] = e.Id;
        }

        // ---------- references ----------
        internal Controller Player(Session s, string who)
        {
            switch ((who ?? "current").ToLowerInvariant())
            {
                case "me": case "p1": case "player1": case "human": return s.G.Player1;
                case "opp": case "p2": case "player2": case "npc": case "ai": return s.G.Player2;
                case "current": return s.G.CurrentPlayer;
                case "other": case "waiting": return s.G.CurrentPlayer.Opponent;
                default: throw new UserError("unknown player " + who + " (me, opp, current, other)");
            }
        }

        internal string Side(Session s, IEntity e)
        {
            var c = e as IPlayable;
            var ctl = c?.Controller ?? e as Controller;
            if (ctl == null) return "game";
            return ctl == s.G.Player1 ? "me" : "opp";
        }

        static IEnumerable<IPlayable> ZoneOf(Controller p, string zone)
        {
            switch (zone)
            {
                case "hand": return p.HandZone;
                case "board": case "play": return p.BoardZone;
                case "secret": case "secrets": return p.SecretZone;
                case "deck": return p.DeckZone;
                case "grave": case "graveyard": return p.GraveyardZone;
                case "setaside": return p.SetasideZone;
                default: throw new UserError("unknown zone " + zone + " (hand, board, secret, deck, grave, setaside)");
            }
        }

        static bool Matches(IPlayable e, string key) =>
            string.Equals(e.Card.Id, key, StringComparison.OrdinalIgnoreCase) || string.Equals(e.Card.Name, key, StringComparison.OrdinalIgnoreCase);

        internal IPlayable Resolve(Session s, Controller by, JsonNode spec)
        {
            if (spec == null) return null;
            if (spec is JsonValue v && v.TryGetValue(out int id)) return ById(s, id);
            return Resolve(s, by, (string)spec);
        }

        internal IPlayable Resolve(Session s, Controller by, string r)
        {
            if (r == null) return null;
            r = r.Trim();
            if (r.StartsWith("@"))
            {
                if (!s.Aliases.TryGetValue(r.Substring(1), out int aid)) throw new UserError("no alias " + r + "; known: " + string.Join(",", s.Aliases.Keys.Select(k => "@" + k)));
                return ById(s, aid);
            }
            if (int.TryParse(r, out int n)) return ById(s, n);
            int dot = r.IndexOf('.');
            if (dot > 0 && (r.StartsWith("me.") || r.StartsWith("opp.") || r.StartsWith("current.") || r.StartsWith("other.")))
            {
                var p = Player(s, r.Substring(0, dot));
                string rest = r.Substring(dot + 1);
                switch (rest)
                {
                    case "hero": return p.Hero;
                    case "power": case "heropower": return p.Hero.HeroPower;
                    case "weapon": return p.Hero.Weapon ?? throw new UserError(r + ": no weapon");
                }
                string zone = rest, key = null; int? index = null;
                int colon = rest.IndexOf(':'), br = rest.IndexOf('[');
                if (colon > 0) { zone = rest.Substring(0, colon); key = rest.Substring(colon + 1); }
                else if (br > 0) { zone = rest.Substring(0, br); index = int.Parse(rest.Substring(br + 1).TrimEnd(']')); }
                var list = ZoneOf(p, zone).ToList();
                if (key != null) return list.FirstOrDefault(e => Matches(e, key)) ?? throw new UserError(r + ": not found; " + zone + " holds " + string.Join(", ", list.Select(Label)));
                if (index != null)
                {
                    int i = index.Value < 0 ? list.Count + index.Value : index.Value;
                    if (i < 0 || i >= list.Count) throw new UserError(r + ": index out of range (" + list.Count + " in " + zone + ")");
                    return list[i];
                }
                throw new UserError(r + ": use zone:CARD or zone[index]");
            }
            // Bare card id or name: the acting player's hand, board, secrets, then the opponent's board.
            foreach (var zone in new[] { by.HandZone.Cast<IPlayable>(), by.BoardZone, by.SecretZone, by.Opponent.BoardZone, by.Opponent.SecretZone })
            {
                var hit = zone.FirstOrDefault(e => Matches(e, r));
                if (hit != null) return hit;
            }
            if (string.Equals(r, "hero", StringComparison.OrdinalIgnoreCase)) return by.Hero;
            throw new UserError("cannot resolve " + r + " for " + Side(s, by) + " (try @alias, an entity id, me.hand:ID, opp.board[0], opp.hero)");
        }

        IPlayable ById(Session s, int id)
        {
            if (s.G.IdEntityDic.TryGetValue(id, out var e) && e is IPlayable p) return p;
            throw new UserError("no playable entity #" + id);
        }

        internal static string Label(IEntity e)
        {
            if (e == null) return "-";
            if (e is IPlayable p) return p.Card.Name + "#" + p.Id;
            if (e is Controller c) return "Player" + c.PlayerId + "#" + c.Id;
            return "Game#" + e.Id;
        }
    }
}
