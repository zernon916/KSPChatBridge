using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletContractsScroll;

        void TabletContracts(Rect area)
        {
            TabletUi.Ensure(tabletFont);
            GUILayout.BeginArea(area);
            try
            {
                tabletContractsScroll = GUILayout.BeginScrollView(tabletContractsScroll);
                try
                {
                    float width = area.width - 26, gap = 8, row = Mathf.Max(44, tabletFont * 2.5f);
                    float y = 0;
                    Contracts.ContractSystem system = Contracts.ContractSystem.Instance;
                    List<Contracts.Contract> contracts = system == null ? null : system.Contracts;
                    int active = 0, offered = 0, count = contracts == null ? 0 : contracts.Count;
                    if (contracts != null)
                    {
                        for (int i = 0; i < contracts.Count; i++)
                        {
                            Contracts.Contract contract = contracts[i];
                            if (contract == null) continue;
                            string state = contract.ContractState.ToString();
                            if (string.Equals(state, "Active", StringComparison.OrdinalIgnoreCase)) active++;
                            else if (string.Equals(state, "Offered", StringComparison.OrdinalIgnoreCase)) offered++;
                        }
                    }

                    float overviewHeight = row * 2 + CardHeaderHeight;
                    Rect overview = TabletUi.CardPanel(new Rect(0, y, width, overviewHeight), "CONTRACT OVERVIEW");
                    TabletUi.Label(new Rect(overview.x, overview.y, overview.width, row),
                        "Active: " + active.ToString(CultureInfo.InvariantCulture) +
                        "  |  Offers: " + offered.ToString(CultureInfo.InvariantCulture), true);
                    TabletUi.Label(new Rect(overview.x, overview.y + row, overview.width, row),
                        contracts == null ? "Contract data is unavailable." :
                        count == 0 ? "No active contracts or offers." : "Current contracts and available offers.", true);
                    y += overviewHeight + gap;

                    if (contracts != null)
                    {
                        double now = Planetarium.GetUniversalTime();
                        for (int i = 0; i < contracts.Count; i++)
                        {
                            Contracts.Contract contract = contracts[i];
                            if (contract == null) continue;
                            y = ContractCard(contract, now, width, row, y, gap);
                        }
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

        float ContractCard(Contracts.Contract contract, double now, float width, float row, float y, float gap)
        {
            string state = contract.ContractState.ToString();
            bool isOffer = string.Equals(state, "Offered", StringComparison.OrdinalIgnoreCase);
            string title = string.IsNullOrEmpty(contract.Title) ? "Untitled contract" : contract.Title;
            string summary = string.IsNullOrEmpty(contract.Synopsys) ? contract.Description : contract.Synopsys;
            if (string.IsNullOrEmpty(summary)) summary = "No contract summary available.";

            float contentWidth = width - 24;
            float summaryHeight = Mathf.Max(row, tabletText.CalcHeight(new GUIContent(summary), contentWidth));
            float height = CardHeaderHeight + row + summaryHeight + row;
            Rect c = TabletUi.CardPanel(new Rect(0, y, width, height), title);
            string localizedState = contract.LocalizedContractState;
            TabletUi.Chip(new Rect(c.xMax - Mathf.Max(110, tabletFont * 7), c.y, Mathf.Max(110, tabletFont * 7), row * .72f),
                string.IsNullOrEmpty(localizedState) ? state : localizedState,
                isOffer ? TabletUi.Accent : TabletUi.Good);

            double expiry = isOffer ? contract.DateExpire : contract.DateDeadline;
            string timeLabel = isOffer ? "Offer expires in: " : "Deadline in: ";
            string remaining = RemainingTime(expiry, now);
            TabletUi.Label(new Rect(c.x, c.y + row, contentWidth, row), timeLabel + remaining, true);
            GUI.Label(new Rect(c.x, c.y + row * 2, contentWidth, summaryHeight), summary, tabletText);

            string reward = "Funds: " + Funds(contract.FundsCompletion);
            if (contract.FundsAdvance > 0)
                reward += "  |  Advance: " + Funds(contract.FundsAdvance);
            if (contract.ScienceCompletion > 0)
                reward += "  |  Science: " + contract.ScienceCompletion.ToString("0.##", CultureInfo.InvariantCulture);
            if (contract.ReputationCompletion > 0)
                reward += "  |  Reputation: " + contract.ReputationCompletion.ToString("0.##", CultureInfo.InvariantCulture);
            TabletUi.Label(new Rect(c.x, c.y + row * 2 + summaryHeight, contentWidth, row), reward, true);
            return y + height + gap;
        }

        static string RemainingTime(double deadline, double now)
        {
            if (double.IsNaN(deadline) || double.IsInfinity(deadline) || deadline <= 0) return "no time limit";
            double remaining = deadline - now;
            if (double.IsNaN(remaining) || double.IsInfinity(remaining)) return "unavailable";
            if (remaining <= 0) return "expired";
            return remaining.ToString("#,0", CultureInfo.InvariantCulture) + " s";
        }

        static string Funds(double amount)
        {
            if (double.IsNaN(amount) || double.IsInfinity(amount)) return "--";
            return "\u00A4" + amount.ToString("#,0", CultureInfo.InvariantCulture);
        }
    }
}
