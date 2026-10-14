namespace KSPChatBridge
{
    public partial class NativeFlightController
    {
        string rocketMode = "off";

        internal string RocketCommand(string name)
        {
            if (name == "rocket_stop")
            {
                rocketMode = "off";
                ChatLog.Write("rocket", "stop");
                return "Rocket autopilot off.";
            }

            return null;
        }
    }
}
