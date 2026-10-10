using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace KSPChatBridge
{
    /// <summary>Samples Kerbin terrain around KSC + Island once (or on request) and writes PluginData/terrain_kerbin.json for the chart editor.</summary>
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, false)]
    public class TerrainExporter : MonoBehaviour
    {
        internal static bool Requested, Running; internal static string Status = "";
        internal static string FilePath { get { return Path.Combine(BridgeLauncher.PluginDataDirectory, "terrain_kerbin.json"); } }
        void Start() { if (!File.Exists(FilePath)) Requested = true; }
        void Update()
        {
            if (!Requested || Running) return;
            var body = FlightGlobals.GetHomeBody();
            if (body == null || body.pqsController == null) return;
            Requested = false; Running = true; StartCoroutine(Run(body));
        }
        IEnumerator Run(CelestialBody body)
        {
            var sites = new List<TerrainGrid.Site>(); int done = 0, total = 0;
            for (int k = 0; k < TerrainGrid.Names.Length; k++) { var s = TerrainGrid.Make(TerrainGrid.Names[k], TerrainGrid.Centers[k, 0], TerrainGrid.Centers[k, 1], TerrainGrid.HalfM, TerrainGrid.StepM, body.Radius); sites.Add(s); total += s.H.Length; }
            foreach (var s in sites)
                for (int i = 0; i < s.H.Length; i++)
                {
                    double la, lo; TerrainGrid.Point(s, i, out la, out lo);
                    double h = 0;
                    try { h = Math.Max(0, body.TerrainAltitude(la, lo, false)); } catch { }
                    s.H[i] = (int)Math.Round(h); done++;
                    if (done % 1500 == 0) { Status = "Sampling terrain " + (100 * done / total) + "%"; yield return null; }
                }
            try { AtomicFile.Write(FilePath, TerrainGrid.Json(sites, body.bodyName)); Status = "Terrain saved: " + FilePath; ChatLog.Write("terrain", "exported " + total + " samples"); }
            catch (Exception ex) { Status = "Terrain export failed: " + ex.Message; }
            Running = false;
        }
    }
}
