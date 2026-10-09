using System;
using System.Net;
using System.Threading;

namespace KSPChatBridge
{
    /// <summary>The only way to open an HTTP request to the Python bridge. With in-mod chat on (or AI off) every call is
    /// refused before any socket opens (live bug Oct 9: "[bridge error] ConnectFailure" in native mode).</summary>
    internal sealed class BridgeOffException : Exception
    {
        internal BridgeOffException(string path) : base(BridgeHttp.Friendly(path)) { }
    }

    internal static class BridgeHttp
    {
        internal const string Url = "http://127.0.0.1:8765/";
        /// <summary>Set by BridgeLauncher (UseBridge); false until then so nothing leaks out at load.</summary>
        internal static Func<bool> Allowed = () => false;
        internal static int Blocked;

        internal static string Friendly(string path)
        {
            string p = (path ?? "").Split('?')[0].Trim('/');
            return "Not available in-mod yet (" + (p.Length == 0 ? "bridge" : p) + ").";
        }

        internal static HttpWebRequest Create(string path)
        {
            if (!Allowed()) { Interlocked.Increment(ref Blocked); throw new BridgeOffException(path); }
            var req = (HttpWebRequest)WebRequest.Create(Url + (path ?? "").TrimStart('/'));
            req.Proxy = null;
            return req;
        }
    }
}
