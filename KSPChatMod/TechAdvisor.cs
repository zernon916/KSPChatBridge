using System;
using System.Collections.Generic;
using KSP.UI.Screens;
using UnityEngine;

namespace KSPChatBridge
{
    /// <summary>tech_advisor (any scene): asks the goal, recommends the cheapest unlock path, highlights it in the open R&D tree.</summary>
    internal static class TechAdvisor
    {
        internal static string Run(Dictionary<string, object> a)
        {
            if (HighLogic.CurrentGame == null || ResearchAndDevelopment.Instance == null) return "Tech advice needs a career/science save with R&D.";
            object g; string goal = a != null && a.TryGetValue("goal", out g) && g != null ? g.ToString().Trim() : "";
            if (goal.Length == 0) return TechAdvisorPolicy.Ask;
            var tree = new Dictionary<string, TechAdvisorPolicy.Node>();
            ProtoRDNode[] nodes;
            try { nodes = AssetBase.RnDTechTree.GetTreeNodes(); } catch (Exception ex) { return "Couldn't read the tech tree: " + ex.Message; }
            foreach (var pn in nodes)
            {
                if (pn == null || pn.tech == null) continue;
                var n = new TechAdvisorPolicy.Node { Id = pn.tech.techID, Cost = pn.tech.scienceCost, AnyParent = pn.AnyParentToUnlock,
                    Unlocked = ResearchAndDevelopment.GetTechnologyState(pn.tech.techID) == RDTech.State.Available };
                if (pn.parents != null) foreach (var p in pn.parents) if (p != null && p.tech != null) n.Parents.Add(p.tech.techID);
                tree[n.Id] = n;
            }
            var titles = new Dictionary<string, string>();
            try { RDTechTree.LoadTechTitles(HighLogic.CurrentGame.Parameters.Career.TechTreeUrl, titles); } catch (Exception) { }
            var targets = TechAdvisorPolicy.Targets(goal);
            if (targets.Count == 0)   // modded tree / odd goal: match words in node titles
                foreach (var kv in titles) foreach (string w in goal.ToLowerInvariant().Split(' ')) if (w.Length > 3 && kv.Value.ToLowerInvariant().Contains(w) && !targets.Contains(kv.Key)) targets.Add(kv.Key);
            if (targets.Count == 0) return "I don't know which nodes fit \"" + goal + "\". " + TechAdvisorPolicy.Ask;
            int total; var path = TechAdvisorPolicy.Path(tree, targets, out total);
            if (path.Count == 0) return "Everything for " + goal + " is already researched (or not in this tree).";
            var names = new List<string>(); foreach (string id in path) { string t; names.Add((titles.TryGetValue(id, out t) ? t : id) + " (" + tree[id].Cost + ")"); }
            string hl = Highlight(path);
            return "For " + goal + ", unlock in order: " + string.Join(" -> ", names.ToArray()) + ". Total " + total + " science (you have "
                + ResearchAndDevelopment.Instance.Science.ToString("0") + ")." + hl;
        }

        /// <summary>Turn on the stock search highlight for the path nodes when the R&D screen is open; selects the first one.</summary>
        static string Highlight(List<string> path)
        {
            var rd = RDController.Instance;
            if (rd == null || rd.nodes == null) return " Open the R&D building and ask again to highlight them.";
            int lit = 0; RDNode first = null;
            foreach (RDNode n in rd.nodes)
            {
                bool on = n != null && n.tech != null && path.Contains(n.tech.techID);
                try { if (n != null && n.graphics != null && n.graphics.searchHighlight != null) { n.graphics.searchHighlight.gameObject.SetActive(on); if (on) lit++; } } catch (Exception) { }
                if (on && first == null && n.tech.techID == path[0]) first = n;
            }
            try { if (first != null) rd.ShowNodePanel(first); } catch (Exception) { }
            return lit > 0 ? " Highlighted " + lit + " node(s) in the tree." : " (Couldn't highlight nodes in this tree UI.)";
        }
    }
}
