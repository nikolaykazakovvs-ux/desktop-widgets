using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using DesktopWidgets.Limits.Core;

namespace DesktopWidgets.Limits.Platform
{
    // Писатель снимка для macOS/Linux — аналог Windows-писателя с ACL (WindowsTelemetryWriter.cs) на правах POSIX:
    //   • каталог: владелец — текущий пользователь, права 0700, не символьная ссылка;
    //   • файл: 0600, публикация через временный файл (O_CREAT|O_EXCL) + fsync + rename(2) — читатель видит
    //     только целый старый или целый новый снимок;
    //   • один writer на файл: эксклюзивный flock на .<имя>.writer.lock (FileShare.None в .NET на Unix — это flock);
    //   • fixture-режим не может писать в живой каталог (как и на Windows);
    //   • один снимок в работе и один ожидающий слот, не чаще MinIntervalMs; сериализация и I/O — в фоновом потоке.
    [UnsupportedOSPlatform("windows")]
    public sealed class UnixTelemetryWriter : ITelemetrySink
    {
        public const int MaxBytes = 1024 * 1024;
        const UnixFileMode DirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        const UnixFileMode FileMode600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        readonly object gate = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Thread worker;
        readonly string directory;
        readonly int interval;
        readonly uint uid;
        Func<DateTime, string> pending;
        bool stopping;
        volatile string lastError;
        long publishedCount;

        public string TargetPath { get; }
        public string LastError => lastError;
        public long PublishedCount => Interlocked.Read(ref publishedCount);

        public UnixTelemetryWriter(string directoryPath, string fileName, string mode, int minIntervalMs = 1000)
        {
            if (fileName != "hardware-snapshot.json" && fileName != "quotas-snapshot.json") throw new ArgumentException("file_name");
            if (mode != "live" && mode != "fixture") throw new ArgumentException("mode");
            if (minIntervalMs < 1 || minIntervalMs > 86400000) throw new ArgumentException("interval");
            if (mode == "fixture" && string.IsNullOrEmpty(directoryPath)) throw new ArgumentException("fixture_directory_required");
            string selected = string.IsNullOrEmpty(directoryPath) ? AppPaths.StandardTelemetryDirectory() : directoryPath;
            if (!Path.IsPathRooted(selected)) throw new ArgumentException("absolute_local_directory_required");
            directory = Path.GetFullPath(selected).TrimEnd('/');
            if (directory.Length < 2) throw new ArgumentException("absolute_local_directory_required");
            if (!UnixStat.Available) throw new PlatformNotSupportedException("unix_stat_unavailable");
            uid = UnixStat.EffectiveUid;
            if (mode == "fixture")
            {
                var live = new List<string> { AppPaths.StandardTelemetryDirectory() };
                string env = Environment.GetEnvironmentVariable("DESKTOP_WIDGETS_TELEMETRY_DIR");
                if (!string.IsNullOrEmpty(env) && Path.IsPathRooted(env)) live.Add(env);
                string canonical = Canonical(directory);
                foreach (var root in live)
                    if (Within(canonical, Canonical(root))) throw new ArgumentException("fixture_live_directory_forbidden");
            }
            TargetPath = Path.Combine(directory, fileName);
            interval = minIntervalMs;
            worker = new Thread(Run) { IsBackground = true, Name = "telemetry-writer", Priority = ThreadPriority.BelowNormal };
            worker.Start();
        }

        public bool Submit(Func<DateTime, string> serializeImmutableProjection)
        {
            if (serializeImmutableProjection == null) return false;
            lock (gate)
            {
                if (stopping) return false;
                pending = serializeImmutableProjection;
                wake.Set();
                return true;
            }
        }

        void Run()
        {
            FileStream pathLock = null;
            try
            {
                PrepareDirectory();
                AssertTarget();
                string lockPath = Path.Combine(directory, "." + Path.GetFileName(TargetPath) + ".writer.lock");
                var li = UnixStat.LStat(lockPath);
                if (li.HasValue && (!li.Value.IsFile || li.Value.Uid != uid)) throw new UnauthorizedAccessException("lock_not_private");
                try
                {
                    pathLock = new FileStream(lockPath, new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite,
                        Share = FileShare.None, UnixCreateMode = FileMode600 });
                }
                catch (IOException) { lastError = "writer_already_running"; return; }
                var clock = Stopwatch.StartNew();
                long next = 0;
                while (true)
                {
                    Func<DateTime, string> value;
                    lock (gate) { if (stopping) return; value = pending; }
                    if (value == null) { wake.WaitOne(); continue; }
                    long delay = next - clock.ElapsedMilliseconds;
                    if (delay > 0) { wake.WaitOne((int)Math.Min(delay, int.MaxValue)); continue; }
                    lock (gate) { if (stopping) return; value = pending; pending = null; }
                    next = clock.ElapsedMilliseconds + interval;
                    try
                    {
                        string json = value(DateTime.UtcNow);
                        var encoding = new UTF8Encoding(false, true);
                        int byteCount = encoding.GetByteCount(json);
                        if (byteCount == 0 || byteCount > MaxBytes) { lastError = "invalid_input"; continue; }
                        Publish(encoding.GetBytes(json));
                        Interlocked.Increment(ref publishedCount);
                        lastError = null;
                    }
                    catch (UnauthorizedAccessException) { lastError = "access_denied"; }
                    catch (Exception) { lastError = "publication_failed"; }
                }
            }
            catch (UnauthorizedAccessException) { lastError = "access_denied"; }
            catch (Exception) { lastError = "publisher_start_failed"; }
            finally
            {
                lock (gate) { stopping = true; pending = null; }
                pathLock?.Dispose();
                wake.Dispose();
            }
        }

        void Publish(byte[] bytes)
        {
            AssertDirectory();
            string temp = Path.Combine(directory, "." + Path.GetFileName(TargetPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            bool renamed = false;
            try
            {
                // CreateNew = O_CREAT|O_EXCL: существующее имя или ссылка не открываются.
                using (var fs = new FileStream(temp, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                    Share = FileShare.None, UnixCreateMode = FileMode600 }))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }
                var ti = UnixStat.LStat(temp);
                if (!ti.HasValue || !ti.Value.IsFile || ti.Value.Uid != uid || (ti.Value.Permissions & 0x3F) != 0) throw new UnauthorizedAccessException("temp_not_private");
                AssertTarget();
                File.Move(temp, TargetPath, true);   // rename(2) в том же каталоге — атомарно
                renamed = true;
            }
            finally
            {
                if (!renamed) try { File.Delete(temp); } catch { }
            }
        }

        void PrepareDirectory()
        {
            if (UnixStat.LStat(directory) == null)
            {
                string parent = Path.GetDirectoryName(directory);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                Directory.CreateDirectory(directory, DirMode);
            }
            AssertDirectory();
        }

        void AssertDirectory()
        {
            var di = UnixStat.LStat(directory);
            if (!di.HasValue || di.Value.IsLink || !di.Value.IsDir) throw new UnauthorizedAccessException("directory_not_private");
            if (di.Value.Uid != uid) throw new UnauthorizedAccessException("foreign_owner");
            if ((di.Value.Permissions & 0x3F) != 0) throw new UnauthorizedAccessException("non_private_mode");   // group/other: ни одного бита
        }

        void AssertTarget()
        {
            var ti = UnixStat.LStat(TargetPath);
            if (!ti.HasValue) return;
            if (ti.Value.IsLink || !ti.Value.IsFile) throw new UnauthorizedAccessException("target_not_regular");
            if (ti.Value.Uid != uid) throw new UnauthorizedAccessException("foreign_owner");
            if ((ti.Value.Permissions & 0x3F) != 0) throw new UnauthorizedAccessException("non_private_mode");
        }

        // Путь с раскрытыми ссылками на существующей части (/var → /private/var на macOS).
        static string Canonical(string path)
        {
            string full = Path.GetFullPath(path).TrimEnd('/');
            var tail = new List<string>();
            string current = full;
            while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
            {
                tail.Insert(0, Path.GetFileName(current));
                current = Path.GetDirectoryName(current);
            }
            if (string.IsNullOrEmpty(current)) current = "/";
            string resolved = "/";
            foreach (var part in current.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                string next = Path.Combine(resolved, part);
                var target = new DirectoryInfo(next).ResolveLinkTarget(true);
                resolved = target != null ? Path.GetFullPath(target.FullName) : next;
            }
            foreach (var t in tail) resolved = Path.Combine(resolved, t);
            return resolved.TrimEnd('/');
        }

        static bool Within(string path, string root) =>
            string.Equals(path, root, StringComparison.Ordinal) || path.StartsWith(root + "/", StringComparison.Ordinal);

        public void Dispose()
        {
            lock (gate) { if (stopping) return; stopping = true; pending = null; wake.Set(); }
            if (Thread.CurrentThread != worker) worker.Join(1500);
        }
    }
}
