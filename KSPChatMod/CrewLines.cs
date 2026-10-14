using System.Collections.Generic;

namespace KSPChatBridge
{
    internal static class CrewLines
    {
        internal static List<string> Lines()
        {
            var lines = new List<string>();
            var game = HighLogic.CurrentGame;
            var crew = game == null || game.CrewRoster == null ? null : game.CrewRoster.Crew;
            if (crew == null) return lines;

            foreach (ProtoCrewMember member in crew)
            {
                if (member == null) continue;
                lines.Add(member.name + " " + member.trait + " L" + member.experienceLevel + " " + member.rosterStatus);
            }

            return lines;
        }
    }
}
