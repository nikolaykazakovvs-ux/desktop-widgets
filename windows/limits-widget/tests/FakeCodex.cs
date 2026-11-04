using System;
using System.IO;
using System.Threading;

// Поддельный `codex app-server` для офлайн-тестов: JSON-RPC построчно по stdio, без сети и аккаунтов.
// FAKE_CODEX_USAGE: ok | error | hang | nulls | absent. FAKE_CODEX_LOG — файл, куда пишутся полученные запросы.
static class FakeCodex
{
    static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] != "app-server") return 2;
        string mode = Environment.GetEnvironmentVariable("FAKE_CODEX_USAGE") ?? "ok";
        string log = Environment.GetEnvironmentVariable("FAKE_CODEX_LOG");
        string line;
        while ((line = Console.In.ReadLine()) != null)
        {
            if (log != null) File.AppendAllText(log, line + Environment.NewLine);
            string id = Id(line);
            if (line.Contains("\"method\":\"initialize\""))
                Reply(id, "{\"userAgent\":\"fake\"}");
            else if (line.Contains("\"method\":\"account/rateLimits/read\""))
                Reply(id, "{\"rateLimits\":{\"limitId\":\"codex\",\"planType\":\"pro\",\"primary\":{\"windowDurationMins\":300,\"usedPercent\":12,\"resetsAt\":1893456000},\"secondary\":{\"windowDurationMins\":10080,\"usedPercent\":34,\"resetsAt\":1893456000}}}");
            else if (line.Contains("\"method\":\"account/usage/read\""))
            {
                if (mode == "hang") { Thread.Sleep(30000); continue; }
                if (mode == "error") { Console.Out.WriteLine("{\"id\":" + id + ",\"error\":{\"code\":-32000,\"message\":\"usage unavailable\"}}"); Console.Out.Flush(); continue; }
                if (mode == "nulls") { Reply(id, "{\"summary\":{\"lifetimeTokens\":null,\"peakDailyTokens\":null},\"dailyUsageBuckets\":null}"); continue; }
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                string twoDaysAgo = DateTime.Now.AddDays(-2).ToString("yyyy-MM-dd");
                Reply(id, "{\"summary\":{\"lifetimeTokens\":123456789,\"peakDailyTokens\":2500000,\"currentStreakDays\":4,\"longestStreakDays\":11,\"longestRunningTurnSec\":1830},"
                    + "\"dailyUsageBuckets\":[{\"startDate\":\"" + twoDaysAgo + "\",\"tokens\":1000000},{\"startDate\":\"" + today + "\",\"tokens\":250000}]}");
            }
        }
        return 0;
    }

    static string Id(string line)
    {
        int i = line.IndexOf("\"id\":", StringComparison.Ordinal);
        if (i < 0) return "0";
        i += 5;
        int j = i;
        while (j < line.Length && char.IsDigit(line[j])) j++;
        return line.Substring(i, j - i);
    }

    static void Reply(string id, string result)
    {
        Console.Out.WriteLine("{\"id\":" + id + ",\"result\":" + result + "}");
        Console.Out.Flush();
    }
}
