using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletShipInfoScroll;
        static readonly string[] ShipResources = { "LiquidFuel", "Oxidizer", "MonoPropellant", "ElectricCharge", "Ore" };

        void TabletShipInfo(Rect area)
        {
            TabletUi.Ensure(tabletFont);
            GUILayout.BeginArea(area);
            try
            {
                tabletShipInfoScroll = GUILayout.BeginScrollView(tabletShipInfoScroll);
                try
                {
                    float width = area.width - 26, gap = 8, row = Mathf.Max(44, tabletFont * 2.5f);
                    float y = 0;
                    Vessel vessel = FlightGlobals.ActiveVessel;
                    if (vessel == null)
                    {
                        Rect empty = TabletUi.CardPanel(new Rect(0, y, width, row * 2), "SHIP INFO");
                        TabletUi.Label(new Rect(empty.x, empty.y, empty.width, row), "No active vessel.", true);
                        y += row * 2 + gap;
                    }
                    else
                    {
                        LiveRocket live = vessel.parts == null ? null : LiveRocket.Read(vessel);
                        y = ShipIdentity(vessel, width, row, y, gap);
                        y = ShipFlightMetrics(vessel, live, width, row, y, gap);
                        y = ShipOrbit(vessel, width, row, y, gap);
                        y = ShipPropulsion(vessel, live, width, row, y, gap);
                        y = ShipResourcesCard(vessel, width, row, y, gap);
                        y = ShipStages(width, row, y, gap);
                    }
                    GUILayout.Space(y + gap);
                }
                finally
                {
                    GUILayout.EndScrollView();
                }
            }
            finally
            {
                GUILayout.EndArea();
            }
        }

        float ShipIdentity(Vessel vessel, float width, float row, float y, float gap)
        {
            float height = row * 3 + CardHeaderHeight;
            Rect c = TabletUi.CardPanel(new Rect(0, y, width, height), "SHIP");
            TabletUi.Label(new Rect(c.x, c.y, c.width, row), vessel.vesselName ?? "(unnamed vessel)");
            string body = vessel.mainBody == null ? "Unknown body" : vessel.mainBody.bodyName;
            string kind = vessel.vesselType.ToString() + " / " + vessel.situation.ToString();
            TabletUi.Label(new Rect(c.x, c.y + row, c.width, row), kind + "  |  " + body, true);
            TabletUi.Label(new Rect(c.x, c.y + row * 2, c.width, row), "Crew: " + CrewCount(vessel) + "  |  Parts: " +
                (vessel.parts == null ? 0 : vessel.parts.Count), true);
            return y + height + gap;
        }

        float ShipFlightMetrics(Vessel vessel, LiveRocket live, float width, float row, float y, float gap)
        {
            int columns = 3;
            float tileHeight = Mathf.Max(82, tabletFont * 4.2f), tileGap = 8;
            float height = tileHeight * 2 + tileGap + CardHeaderHeight;
            Rect c = TabletUi.CardPanel(new Rect(0, y, width, height), "FLIGHT");
            float tileWidth = (c.width - tileGap * (columns - 1)) / columns;
            double mass = live == null ? double.NaN : live.Mass;
            string[] captions = { "ALTITUDE", "RADAR ALT", "SURFACE SPEED", "VERT SPEED", "MASS", "DYNAMIC Q" };
            string[] values =
            {
                ShipInfo.Distance(vessel.altitude),
                ShipInfo.Distance(vessel.radarAltitude),
                ShipInfo.Speed(vessel.srfSpeed),
                ShipInfo.Speed(vessel.verticalSpeed),
                ShipInfo.Mass(mass),
                double.IsNaN(vessel.dynamicPressurekPa) || double.IsInfinity(vessel.dynamicPressurekPa)
                    ? "--" : vessel.dynamicPressurekPa.ToString("0.00", CultureInfo.InvariantCulture) + " kPa"
            };
            Color[] accents = { TabletUi.Series[0], TabletUi.Series[0], TabletUi.Series[1], TabletUi.Series[2], TabletUi.Accent, TabletUi.Warn };
            for (int i = 0; i < captions.Length; i++)
            {
                int r = i / columns, col = i % columns;
                TabletUi.Tile(new Rect(c.x + col * (tileWidth + tileGap), c.y + r * (tileHeight + tileGap), tileWidth, tileHeight),
                    captions[i], values[i], "", accents[i]);
            }
            return y + height + gap;
        }

        float ShipOrbit(Vessel vessel, float width, float row, float y, float gap)
        {
            float height = row * 4 + CardHeaderHeight;
            Rect c = TabletUi.CardPanel(new Rect(0, y, width, height), "ORBIT");
            Orbit orbit = vessel.orbit;
            if (orbit == null)
                TabletUi.Label(new Rect(c.x, c.y, c.width, row), "No orbit data.", true);
            else
            {
                string body = orbit.referenceBody == null ? "Unknown body" : orbit.referenceBody.bodyName;
                TabletUi.Label(new Rect(c.x, c.y, c.width, row), "Reference body: " + body, true);
                TabletUi.Label(new Rect(c.x, c.y + row, c.width, row), "Apoapsis: " + ShipInfo.Distance(orbit.ApA), true);
                TabletUi.Label(new Rect(c.x, c.y + row * 2, c.width, row), "Periapsis: " + ShipInfo.Distance(orbit.PeA), true);
                TabletUi.Label(new Rect(c.x, c.y + row * 3, c.width, row), "Inclination: " + ShipInfo.Angle(orbit.inclination), true);
            }
            return y + height + gap;
        }

        float ShipPropulsion(Vessel vessel, LiveRocket live, float width, float row, float y, float gap)
        {
            float height = row * 3 + CardHeaderHeight;
            Rect c = TabletUi.CardPanel(new Rect(0, y, width, height), "PROPULSION / THERMAL");
            if (live == null)
            {
                TabletUi.Label(new Rect(c.x, c.y, c.width, row), "Vessel part data unavailable.", true);
                return y + height + gap;
            }

            bool validTwr = live.Mass > 0 && live.LocalG > 0 && !double.IsNaN(live.Mass) &&
                !double.IsInfinity(live.Mass) && !double.IsNaN(live.LocalG) && !double.IsInfinity(live.LocalG);
            double twr = validTwr ? live.Thrust / (live.Mass * live.LocalG) : double.NaN;
            TabletUi.Label(new Rect(c.x, c.y, c.width, row), "Current thrust: " +
                (double.IsNaN(live.Thrust) || double.IsInfinity(live.Thrust) ? "--" : live.Thrust.ToString("#,0.0", CultureInfo.InvariantCulture) + " kN"), true);
            TabletUi.Label(new Rect(c.x, c.y + row, c.width, row), "Current TWR: " + (validTwr ? ShipInfo.Ratio(twr) : "-") +
                "  |  Stage: " + (live.Stage < 0 ? "n/a" : live.Stage.ToString(CultureInfo.InvariantCulture)), true);
            TabletUi.Label(new Rect(c.x, c.y + row * 2, c.width, row), "Peak part temperature: " + ShipInfo.Percent(live.MaxTempRatio), true);
            return y + height + gap;
        }

        float ShipResourcesCard(Vessel vessel, float width, float row, float y, float gap)
        {
            float height = row * ShipResources.Length + CardHeaderHeight;
            Rect c = TabletUi.CardPanel(new Rect(0, y, width, height), "RESOURCES");
            float barHeight = Mathf.Max(34, row * .8f);
            for (int i = 0; i < ShipResources.Length; i++)
            {
                double amount = 0, capacity = 0;
                ReadResource(vessel, ShipResources[i], out amount, out capacity);
                Rect bar = new Rect(c.x, c.y + i * row, c.width, barHeight);
                TabletUi.Bar(bar, ShipResources[i], ShipInfo.ResourceFraction(amount, capacity),
                    ShipInfo.Resource(amount, capacity), TabletUi.Series[i % TabletUi.Series.Length]);
            }
            return y + height + gap;
        }

        float ShipStages(float width, float row, float y, float gap)
        {
            List<StageInfo> stages = StageReader.ForFlight();
            int count = stages == null ? 0 : stages.Count;
            float height = row * Math.Max(2, count + 1) + CardHeaderHeight;
            Rect c = TabletUi.CardPanel(new Rect(0, y, width, height), "STAGE DELTA-V");
            if (count == 0)
                TabletUi.Label(new Rect(c.x, c.y, c.width, row), "Stage delta-v is not available yet.", true);
            else
            {
                TabletUi.Label(new Rect(c.x, c.y, c.width, row), "Stage       Actual dv       TWR (ASL)       Burn", true);
                for (int i = 0; i < count; i++)
                {
                    StageInfo stage = stages[i];
                    string twr = double.IsNaN(stage.TwrAsl) || double.IsInfinity(stage.TwrAsl)
                        ? "--" : stage.TwrAsl.ToString("0.00", CultureInfo.InvariantCulture);
                    string burn = double.IsNaN(stage.BurnTime) || double.IsInfinity(stage.BurnTime)
                        ? "--" : stage.BurnTime.ToString("0", CultureInfo.InvariantCulture) + " s";
                    TabletUi.Label(new Rect(c.x, c.y + row * (i + 1), c.width, row),
                        "S" + stage.Stage + "          " + ShipInfo.Speed(stage.DvActual) + "       " + twr + "x       " + burn, true);
                }
            }
            return y + height + gap;
        }

        static int CrewCount(Vessel vessel)
        {
            if (vessel == null || vessel.parts == null) return 0;
            int crew = 0;
            foreach (Part part in vessel.parts)
                if (part != null && part.protoModuleCrew != null) crew += part.protoModuleCrew.Count;
            return crew;
        }

        float CardHeaderHeight { get { return 16 + tabletFont * 1.6f; } }

        static void ReadResource(Vessel vessel, string name, out double amount, out double capacity)
        {
            amount = 0;
            capacity = 0;
            if (vessel == null || PartResourceLibrary.Instance == null) return;
            PartResourceDefinition definition = PartResourceLibrary.Instance.GetDefinition(name);
            if (definition == null) return;
            vessel.GetConnectedResourceTotals(definition.id, out amount, out capacity);
        }
    }
}
