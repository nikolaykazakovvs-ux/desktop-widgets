using System;
using System.IO;
using System.Threading;

// Поддельный `claude auth status --json` для офлайн-тестов: синтетический ответ, никаких аккаунтов и credentials.
// FAKE_CLAUDE_AUTH: max | loggedout | missing | unsupported | text | hang. FAKE_CLAUDE_LOG — счётчик вызовов и
// признак того, дошли ли переменные CLAUDE*/ANTHROPIC* родительской сессии.
static class FakeClaude
{
    static int Main(string[] args)
    {
        string log = Environment.GetEnvironmentVariable("FAKE_CLAUDE_LOG");
        bool leaked = Environment.GetEnvironmentVariable("CLAUDE_FAKE_PARENT") != null || Environment.GetEnvironmentVariable("ANTHROPIC_FAKE_PARENT") != null;
        if (log != null) File.AppendAllText(log, string.Join(" ", args) + (leaked ? " ENV-LEAKED" : "") + Environment.NewLine);
        // `-p "/usage"` (так же вызывается и agy): FAKE_CLI_USAGE — subscription | agy-net | hang. Служебный запрос get_usage
        // (--input-format stream-json) этот поддельный CLI не знает и сразу завершается.
        if (args.Length > 0 && args[0] == "-p" && Array.IndexOf(args, "--input-format") < 0)
        {
            string usage = Environment.GetEnvironmentVariable("FAKE_CLI_USAGE");
            if (usage == "subscription") { Console.Out.WriteLine("You are currently using your subscription to power your Claude Code usage"); return 0; }
            if (usage == "agy-net")
            {
                Console.Error.WriteLine("error: /usage failed: retrieving quota summary: failed to retrieve user quota summary: failed to get load code assist response: Post \"https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist?key=CANARYQUERYSECRET0123456789abcdef\": dial tcp: lookup cloudcode-pa.googleapis.com: no such host");
                return 1;
            }
            if (usage == "hang") { Thread.Sleep(30000); return 0; }
            return 2;
        }
        if (args.Length < 2 || args[0] != "auth" || args[1] != "status") return 2;
        string mode = Environment.GetEnvironmentVariable("FAKE_CLAUDE_AUTH") ?? "max";
        const string pii = "\"email\":\"canary.claude@example.test\",\"orgId\":\"ORG-CANARY-42\",\"orgName\":\"Canary Org\",\"accountUuid\":\"ACC-CANARY-UUID\"";
        switch (mode)
        {
            case "max": Console.Out.WriteLine("{\"loggedIn\":true,\"authMethod\":\"claude.ai\",\"subscriptionType\":\"max\"," + pii + "}"); return 0;
            case "loggedout": Console.Out.WriteLine("{\"loggedIn\":false," + pii + "}"); return 0;
            case "missing": Console.Out.WriteLine("{\"loggedIn\":true," + pii + "}"); return 0;
            case "unsupported": Console.Error.WriteLine("error: unknown option '--json' canary.claude@example.test"); return 1;
            case "text": Console.Out.WriteLine("Logged in as canary.claude@example.test (Max)"); return 0;
            case "hang": Thread.Sleep(30000); return 0;
        }
        return 3;
    }
}
