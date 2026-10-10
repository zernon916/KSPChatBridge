using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    /// <summary>tech_advisor: goal -> stock tech targets and the cheapest unlock path (pure, offline-testable).</summary>
    internal static class TechAdvisorPolicy
    {
        internal sealed class Node { internal string Id; internal int Cost; internal bool Unlocked, AnyParent; internal List<string> Parents = new List<string>(); }

        internal const string Ask = "What's the plan, Captain? Tell me the goal (e.g. Mun landing, better planes, science, docking, Duna) and I'll find the cheapest unlocks.";

        static readonly string[][] Goals =
        {
            new[] { "mun minmus moon land lander landing", "landing advRocketry fuelSystems electrics" },
            new[] { "orbit orbital", "advRocketry generalConstruction" },
            new[] { "plane planes aircraft jet jets fly aviation supersonic spaceplane", "aviation aerodynamicSystems supersonicFlight" },
            new[] { "science experiment experiments research", "basicScience scienceTech electrics" },
            new[] { "dock docking station rendezvous", "advConstruction advFlightControl" },
            new[] { "duna eve interplanetary jool nuclear", "heavierRocketry advElectrics nuclearPropulsion" },
            new[] { "rover rovers drive wheels", "fieldScience electrics" },
            new[] { "power solar battery electric", "electrics advElectrics" },
            new[] { "heavy bigger rockets lift payload", "heavyRocketry heavierRocketry" },
        };

        /// <summary>Stock tech ids for a goal; empty when the goal is too vague (then the caller asks / matches titles).</summary>
        internal static List<string> Targets(string goal)
        {
            var words = new HashSet<string>((goal ?? "").ToLowerInvariant().Split(new[] { ' ', ',', '.', '!', '?', '-' }, StringSplitOptions.RemoveEmptyEntries));
            var t = new List<string>();
            foreach (var g in Goals)
            {
                bool hit = false; foreach (string w in g[0].Split(' ')) if (words.Contains(w)) { hit = true; break; }
                if (hit) foreach (string id in g[1].Split(' ')) if (!t.Contains(id)) t.Add(id);
            }
            return t;
        }

        /// <summary>Cheapest set of nodes to unlock so every reachable target is researched, in unlock order (parents first).
        /// AnyParent nodes take their cheapest parent chain; others need all parents. total = science cost.</summary>
        internal static List<string> Path(IDictionary<string, Node> tree, IEnumerable<string> targets, out int total)
        {
            var memo = new Dictionary<string, KeyValuePair<int, List<string>>>();
            Func<string, int, KeyValuePair<int, List<string>>> best = null;
            best = (id, depth) =>
            {
                KeyValuePair<int, List<string>> r;
                if (memo.TryGetValue(id, out r)) return r;
                Node n; if (!tree.TryGetValue(id, out n) || depth > 64) return new KeyValuePair<int, List<string>>(int.MaxValue / 4, new List<string>());
                if (n.Unlocked) return memo[id] = new KeyValuePair<int, List<string>>(0, new List<string>());
                var chain = new List<string>(); int cost = n.Cost;
                if (n.Parents.Count > 0)
                {
                    if (n.AnyParent)
                    {
                        KeyValuePair<int, List<string>>? pick = null;
                        foreach (string p in n.Parents) { var c = best(p, depth + 1); if (pick == null || c.Key < pick.Value.Key) pick = c; }
                        cost += pick.Value.Key; chain.AddRange(pick.Value.Value);
                    }
                    else foreach (string p in n.Parents) { var c = best(p, depth + 1); cost += c.Key; foreach (string x in c.Value) if (!chain.Contains(x)) chain.Add(x); }
                }
                chain.Add(id);
                return memo[id] = new KeyValuePair<int, List<string>>(cost, chain);
            };
            var order = new List<string>();
            foreach (string t in targets) { if (!tree.ContainsKey(t)) continue; foreach (string x in best(t, 0).Value) if (!order.Contains(x)) order.Add(x); }
            total = 0; foreach (string x in order) total += tree[x].Cost;
            return order;
        }
    }
}
