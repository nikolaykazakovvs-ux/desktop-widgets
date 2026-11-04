using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// Поддельный `kimi` для офлайн-тестов: никакой сети, кроме loopback, никаких аккаунтов.
//   kimi web --no-open  — сервер: печатает «токен» в stdout (виджет обязан его не читать), пишет server.token (если нет)
//                         и запись экземпляра в KIMI_CODE_HOME/server/instances, запускает потомка (проверка дерева),
//                         отвечает на /api/v1/healthz, /api/v1/auth, /api/v1/oauth/usage.
//   kimi login          — печатает адрес и код подтверждения, выходит с 0.
//   kimi child-sleep    — потомок сервера, просто живёт.
// FAKE_KIMI_MODE: ok | noauth | crash | silent (не регистрируется). FAKE_KIMI_MARK — каталог для pid-меток.
static class FakeKimi
{
    static string token;

    static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0] : "";
        if (cmd == "child-sleep") { Thread.Sleep(Timeout.Infinite); return 0; }
        if (cmd == "login")
        {
            Console.Error.WriteLine("Open https://auth.example.test/device in your browser");
            Console.Error.WriteLine("Then enter code: WXYZ-1234");
            return 0;
        }
        if (cmd != "web") return 2;
        string mode = Environment.GetEnvironmentVariable("FAKE_KIMI_MODE") ?? "ok";
        string home = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        string mark = Environment.GetEnvironmentVariable("FAKE_KIMI_MARK");
        int pid = Process.GetCurrentProcess().Id;
        if (mark != null) File.AppendAllText(Path.Combine(mark, "server-pids.txt"), pid + Environment.NewLine);
        Console.WriteLine("Kimi web ready. token=CANARY-BEARER-TOKEN-" + pid);
        if (mode == "crash") return 3;

        string tokenFile = Path.Combine(home, "server.token");
        if (!File.Exists(tokenFile)) File.WriteAllText(tokenFile, "fake-server-token-" + Guid.NewGuid().ToString("N"));
        token = File.ReadAllText(tokenFile).Trim();

        var child = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "child-sleep") { UseShellExecute = false, CreateNoWindow = true });
        if (mark != null) File.AppendAllText(Path.Combine(mark, "child-pids.txt"), child.Id + Environment.NewLine);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        if (mode != "silent")
        {
            string dir = Path.Combine(home, "server", "instances");
            Directory.CreateDirectory(dir);
            double hb = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            File.WriteAllText(Path.Combine(dir, "fake-" + pid + ".json"),
                "{\"host\":\"127.0.0.1\",\"port\":" + port + ",\"pid\":" + pid + ",\"heartbeat_at\":" + ((long)hb) + "}");
        }
        while (true)
        {
            var client = listener.AcceptTcpClient();
            ThreadPool.QueueUserWorkItem(_ => Serve(client, mode));
        }
    }

    static void Serve(TcpClient client, string mode)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                string first = reader.ReadLine() ?? "";
                string auth = null, line;
                while (!string.IsNullOrEmpty(line = reader.ReadLine()))
                    if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) auth = line.Substring(14).Trim();
                string path = first.Split(' ').Length > 1 ? first.Split(' ')[1] : "/";
                bool authed = mode == "noauth" || auth == "Bearer " + token;
                int status = 200;
                string body;
                if (path.StartsWith("/api/v1/healthz")) body = "{\"code\":0,\"data\":{\"ok\":true}}";
                else if (!authed) { status = 401; body = "{\"code\":40101,\"msg\":\"unauthorized\"}"; }
                else if (path.StartsWith("/api/v1/auth")) body = "{\"code\":0,\"data\":{}}";
                else if (path.StartsWith("/api/v1/oauth/usage"))
                    body = Environment.GetEnvironmentVariable("FAKE_KIMI_USAGE")
                        ?? "{\"code\":0,\"data\":{\"kind\":\"ok\",\"quota\":{\"usages\":{\"limit5h\":{\"usedRatio\":0.25},\"monthTotal\":{\"usedRatio\":0.5},\"monthCode\":{\"usedRatio\":0.2}}}}}";
                else { status = 404; body = "{}"; }
                var bytes = Encoding.UTF8.GetBytes(body);
                var head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " X\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                stream.Write(head, 0, head.Length);
                stream.Write(bytes, 0, bytes.Length);
            }
            catch
            {
            }
        }
    }
}
