using System;
using KSPChatBridge;

internal static class ContractFormatTests
{
    static int checks;

    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("ContractFormatTests: " + name);
        checks++;
    }

    internal static void Run()
    {
        checks = 0;
        Check(ContractFormat.RemainingTime(12 * 21600 + 3 * 3600 + 5 * 60, 0, 21600) == "12d 3h 05m",
            "formats Kerbin time");
        Check(ContractFormat.RemainingTime(2 * 86400 + 3 * 3600 + 5 * 60, 0, 86400) == "2d 3h 05m",
            "formats 24-hour days");
        Check(ContractFormat.RemainingTime(3600 + 5 * 60 + 59, 0, 86400) == "0d 1h 05m",
            "truncates partial minutes");
        Check(ContractFormat.RemainingTime(0, 0, 21600) == "no time limit", "formats missing deadline");
        Check(ContractFormat.RemainingTime(10, 10, 21600) == "expired", "formats expired deadline");
        Check(ContractFormat.RemainingTime(double.NaN, 0, 21600) == "no time limit", "rejects invalid deadline");
        Check(ContractFormat.RemainingTime(100, 0, 0) == "unavailable", "rejects invalid day length");
        Check(ContractFormat.Funds(12345.6) == "\u00A412,346", "formats funds invariantly");
        Check(ContractFormat.Funds(double.NaN) == "--", "formats invalid funds");
        Console.WriteLine("ContractFormatTests: " + checks + " behavior checks passed.");
    }
}
