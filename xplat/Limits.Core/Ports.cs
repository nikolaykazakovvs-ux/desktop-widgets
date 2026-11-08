using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopWidgets.Limits.Core
{
    // Граница ядра и платформы. Ядро описывает, ЧТО нужно (запустить CLI, записать снимок, найти файл приложения),
    // платформенный слой (Limits.Platform) решает, КАК это сделать на Windows, macOS или Linux.

    public sealed class CliResult
    {
        public int ExitCode = -1;
        public string StdOut = "", StdErr = "";
        public bool TimedOut;
        public string StartError;

        public bool Ok => StartError == null && !TimedOut && ExitCode == 0;

        public string Describe()
        {
            if (StartError != null) return "не запустился: " + StartError;
            if (TimedOut) return "не ответил вовремя";
            string first = Text.FirstLine(StdErr) ?? Text.FirstLine(StdOut);
            return "код " + ExitCode + (first != null ? ": " + first : "");
        }
    }

    public sealed class CliRequest
    {
        public string Exe;
        public IReadOnlyList<string> Args = Array.Empty<string>();
        public int TimeoutMs = 45000;
        public int MaxProcesses;                       // 0 — без лимита
        public IReadOnlyList<string> DropEnvPrefixes = Array.Empty<string>();   // напр. CLAUDE, ANTHROPIC
    }

    // Диалог по stdin/stdout построчно (stream-json): ядро пишет запрос и читает ответы до своего условия.
    public interface ICliSession : IDisposable
    {
        void WriteLine(string line);
        // null — поток закрыт или истёк срок; timedOut различает эти случаи.
        string ReadLine(int timeoutMs, out bool timedOut);
        string StdErrHint();
    }

    public interface IProcessRunner
    {
        // Поиск CLI: явный путь из конфига или имя в PATH (+ платформенные каталоги, которых нет в PATH у GUI-приложений).
        string Resolve(string configured, string name);
        CliResult Run(CliRequest request);
        ICliSession Start(CliRequest request);
    }

    public interface ITelemetrySink : IDisposable
    {
        string TargetPath { get; }
        string LastError { get; }
        long PublishedCount { get; }
        // Колбэк получает момент публикации и должен захватывать только неизменяемые данные.
        bool Submit(Func<DateTime, string> serializeImmutableProjection);
    }

    public interface IAppPaths
    {
        string ConfigDirectory { get; }        // config.json и state.json прототипа
        string TelemetryDirectory { get; }     // каталог quotas-snapshot.json (контракт 1.0.0)
        IEnumerable<string> ClaudeDesktopHistoryCandidates();
    }

    public interface ILog
    {
        void Write(string line);
    }

    public static class Text
    {
        public static string FirstLine(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            foreach (var line in s.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0) return t.Length > 200 ? t.Substring(0, 200) + "…" : t;
            }
            return null;
        }
    }
}
