using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DesktopWidgets.Limits.Core;

namespace DesktopWidgets.Limits.Platform
{
    // Поиск и запуск CLI на любой ОС.
    // Главное отличие macOS: приложение, запущенное из Finder/Dock/автозапуска, получает урезанный PATH (/usr/bin:/bin:/usr/sbin:/sbin),
    // поэтому claude из ~/.local/bin или Homebrew без дополнительных каталогов не находится. Те же каталоги добавляются
    // в PATH дочернего процесса — CLI на Node (#!/usr/bin/env node) иначе не запустится.
    public sealed class ProcessRunner : IProcessRunner
    {
        static readonly string[] WindowsExts = { ".exe", ".cmd", ".bat" };
        static readonly TimeSpan MissRetry = TimeSpan.FromMinutes(5);
        readonly object gate = new object();
        readonly Dictionary<string, (string Path, DateTime At)> resolved = new Dictionary<string, (string, DateTime)>(StringComparer.Ordinal);
        readonly ILog log;
        bool warnedMaxProcesses;

        public ProcessRunner(ILog log) { this.log = log; }

        public int Searches { get; private set; }

        public static IReadOnlyList<string> ExtraDirectories()
        {
            if (OperatingSystem.IsWindows())
                return new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
                    Path.Combine(Os.Home, ".local", "bin")
                };
            return new[]
            {
                Path.Combine(Os.Home, ".local", "bin"),
                Path.Combine(Os.Home, ".claude", "local"),
                Path.Combine(Os.Home, ".npm-global", "bin"),
                Path.Combine(Os.Home, ".bun", "bin"),
                Path.Combine(Os.Home, ".volta", "bin"),
                "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin"
            };
        }

        public static List<string> SearchDirectories()
        {
            var dirs = new List<string>();
            foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Concat(ExtraDirectories()))
            {
                string t = d.Trim().Trim('"');
                if (t.Length > 0 && Path.IsPathRooted(t) && !dirs.Contains(t)) dirs.Add(t);
            }
            return dirs;
        }

        public static string ExpandPath(string p)
        {
            p = Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'));
            if (p == "~" || p.StartsWith("~/", StringComparison.Ordinal)) p = Os.Home + p.Substring(1);
            return p;
        }

        static bool IsExecutable(string path)
        {
            if (!File.Exists(path)) return false;
            if (OperatingSystem.IsWindows()) return true;
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }

        public string Resolve(string configured, string name)
        {
            if (!string.IsNullOrEmpty(configured))
            {
                string p = ExpandPath(configured);
                return IsExecutable(p) ? p : null;   // явный путь не кэшируется
            }
            lock (gate)
            {
                if (resolved.TryGetValue(name, out var hit))
                {
                    if (hit.Path != null && IsExecutable(hit.Path)) return hit.Path;
                    if (hit.Path == null && DateTime.UtcNow - hit.At < MissRetry) return null;
                }
                Searches++;
                string found = null;
                foreach (var dir in SearchDirectories())
                {
                    var names = OperatingSystem.IsWindows() ? WindowsExts.Select(e => name + e) : new[] { name };
                    found = names.Select(n => Path.Combine(dir, n)).FirstOrDefault(IsExecutable);
                    if (found != null) break;
                }
                resolved[name] = (found, DateTime.UtcNow);
                return found;
            }
        }

        ProcessStartInfo StartInfo(CliRequest req, bool stdin)
        {
            var psi = new ProcessStartInfo(req.Exe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Os.Home
            };
            foreach (var a in req.Args) psi.ArgumentList.Add(a);
            foreach (var k in psi.Environment.Keys.ToArray())
                if (req.DropEnvPrefixes.Any(p => k.StartsWith(p, StringComparison.OrdinalIgnoreCase))) psi.Environment.Remove(k);
            var path = new List<string> { Path.GetDirectoryName(req.Exe) };
            path.AddRange(SearchDirectories());
            psi.Environment["PATH"] = string.Join(Path.PathSeparator, path.Distinct());
            psi.Environment["NO_COLOR"] = "1";
            if (req.MaxProcesses > 0 && !warnedMaxProcesses)
            {
                warnedMaxProcesses = true;
                log.Write("maxProcesses: лимит процессов (Windows Job) в прототипе не реализован — дерево процессов завершается целиком по тайм-ауту");
            }
            return psi;
        }

        public CliResult Run(CliRequest req)
        {
            var res = new CliResult();
            Process p;
            try { p = Process.Start(StartInfo(req, false)); }
            catch (Exception ex) { res.StartError = ex.Message; return res; }
            using (p)
            {
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(req.TimeoutMs))
                {
                    res.TimedOut = true;
                    KillTree(p);
                }
                else p.WaitForExit();
                try { if (stdout.Wait(2000)) res.StdOut = stdout.Result; } catch { }
                try { if (stderr.Wait(2000)) res.StdErr = stderr.Result; } catch { }
                if (!res.TimedOut) res.ExitCode = p.ExitCode;
            }
            return res;
        }

        public ICliSession Start(CliRequest req) => new Session(Process.Start(StartInfo(req, true)));

        public static void KillTree(Process p)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch { }
        }

        sealed class Session : ICliSession
        {
            readonly Process p;
            readonly BlockingCollection<string> lines = new BlockingCollection<string>();
            readonly System.Threading.Tasks.Task<string> stderr;

            public Session(Process p)
            {
                this.p = p;
                stderr = p.StandardError.ReadToEndAsync();
                new Thread(() =>
                {
                    try { string l; while ((l = p.StandardOutput.ReadLine()) != null) lines.Add(l); }
                    catch { }
                    finally { lines.CompleteAdding(); }
                }) { IsBackground = true, Name = "cli-stdout" }.Start();
            }

            public void WriteLine(string line)
            {
                p.StandardInput.WriteLine(line);
                p.StandardInput.Flush();
            }

            public string ReadLine(int timeoutMs, out bool timedOut)
            {
                timedOut = false;
                if (lines.TryTake(out string line, Math.Max(0, timeoutMs))) return line;
                timedOut = !lines.IsCompleted;
                return null;
            }

            // Только на пути ошибки: закрыть stdin, дождаться выхода (или завершить дерево) и взять первую строку stderr.
            public string StdErrHint()
            {
                try { p.StandardInput.Close(); } catch { }
                try { if (!p.WaitForExit(3000)) KillTree(p); } catch { }
                try { return stderr.Wait(500) ? Text.FirstLine(stderr.Result) : null; }
                catch { return null; }
            }

            public void Dispose()
            {
                try { p.StandardInput.Close(); } catch { }
                try { if (!p.WaitForExit(3000)) KillTree(p); } catch { }
                p.Dispose();
            }
        }
    }
}
