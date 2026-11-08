using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

// ВРЕМЕННАЯ КОПИЯ telemetry/publisher-shared/TelemetrySnapshotWriter.cs для .NET 10 (прототип Avalonia).
// Отличия только в API: ACL-методы .NET Framework (Directory/File.GetAccessControl, new Mutex(..., security))
// заменены на FileSystemAclExtensions и MutexAcl. При полном переносе — один общий файл с #if NETFRAMEWORK.
namespace DesktopWidgets.Limits.Platform.WindowsTelemetry
{
    [SupportedOSPlatform("windows")]
    public sealed class TelemetryWriterOptions
    {
        public string DirectoryPath;
        public string FileName;
        public string Mode = "live";
        public int MinIntervalMs = 1000;
    }

    // One frozen projection in flight and one pending slot. All serialization and file I/O are on worker.
    // The callback must capture only privately owned immutable primitive data, never live UI/collector state.
    [SupportedOSPlatform("windows")]
    public sealed class TelemetrySnapshotWriter : IDisposable
    {
        public const int MaxBytes = 1024 * 1024;
        // Temp handle: write, attest and carry the replaced DACL by handle; DELETE renames or discards this exact object.
        const uint TempAccess = 0x40000000 | 0x00010000 | 0x00020000 | 0x00040000, CreateNew = 1, OpenAlways = 4, TempFlags = 0x80 | 0x00200000;
        const int FileRenameInfo = 3, FileDispositionInfo = 4, FileRenameInfoEx = 22;
        const int RenameReplaceIfExists = 0x1, RenamePosixSemantics = 0x2;
        readonly object gate = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Thread worker;
        readonly string directory, mutexName;
        readonly string[] fixtureLiveRoots;
        readonly int interval;
        Func<DateTime, string> pending;
        bool stopping;
        volatile string lastError;
        long publishedCount;
        public string TargetPath { get; private set; }
        public string LastError { get { return lastError; } }
        public long PublishedCount { get { return Interlocked.Read(ref publishedCount); } }

        public TelemetrySnapshotWriter(TelemetryWriterOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (options.FileName != "hardware-snapshot.json" && options.FileName != "quotas-snapshot.json") throw new ArgumentException("file_name");
            if (options.Mode != "live" && options.Mode != "fixture") throw new ArgumentException("mode");
            if (options.MinIntervalMs < 1 || options.MinIntervalMs > 86400000) throw new ArgumentException("interval");
            string selected = options.DirectoryPath;
            if (options.Mode == "fixture" && string.IsNullOrEmpty(selected)) throw new ArgumentException("fixture_directory_required");
            if (string.IsNullOrEmpty(selected)) selected = Environment.GetEnvironmentVariable("DESKTOP_WIDGETS_TELEMETRY_DIR");
            if (string.IsNullOrEmpty(selected)) selected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopWidgets", "telemetry");
            if (!Path.IsPathRooted(selected) || selected.StartsWith("\\\\", StringComparison.Ordinal) || selected.Length < 3 || selected[1] != ':' || selected[2] != '\\') throw new ArgumentException("absolute_local_directory_required");
            directory = Path.GetFullPath(selected).TrimEnd(Path.DirectorySeparatorChar);
            if (directory.Length < 4 || new DriveInfo(Path.GetPathRoot(directory)).DriveType != DriveType.Fixed) throw new ArgumentException("local_fixed_directory_required");
            if (options.Mode == "fixture")
            {
                string standardLive = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopWidgets", "telemetry"));
                string configuredLive = Environment.GetEnvironmentVariable("DESKTOP_WIDGETS_TELEMETRY_DIR");
                var roots = new List<string> { standardLive };
                if (!string.IsNullOrEmpty(configuredLive) && Path.IsPathRooted(configuredLive)) roots.Add(Path.GetFullPath(configuredLive));
                fixtureLiveRoots = roots.ToArray();
                AssertFixtureLiveGuard();
            }
            TargetPath = Path.Combine(directory, options.FileName);
            interval = options.MinIntervalMs;
            string sid = WindowsIdentity.GetCurrent().User.Value;
            using (var hash = SHA256.Create()) mutexName = "Global\\DesktopWidgets.Telemetry." + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(sid + "|" + TargetPath.ToUpperInvariant()))).Replace("-", "");
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
            Mutex mutex = null;
            FileStream pathLock = null;
            bool owned = false;
            try
            {
                var sid = WindowsIdentity.GetCurrent().User;
                var security = new MutexSecurity();
                security.AddAccessRule(new MutexAccessRule(sid, MutexRights.FullControl, AccessControlType.Allow));
                bool created;
                mutex = MutexAcl.Create(false, mutexName, out created, security);
                try { owned = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) { lastError = "writer_already_running"; return; }
                PrepareDirectory(directory, sid);
                AssertFixtureLiveGuard();
                AssertTarget(sid);
                // A held per-target file handle also excludes DOS short-name/path aliases and different
                // Windows logon sessions. The file is a bounded, fixed name in the verified private directory.
                string lockPath = Path.Combine(directory, "." + Path.GetFileName(TargetPath) + ".writer.lock");
                if (File.Exists(lockPath))
                {
                    if ((File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("lock_reparse");
                }
                // Created with this user as explicit owner (see CreateOwned); an already existing lock keeps its own
                // owner and is attested below, so a foreign-owned lock still fails closed.
                SafeFileHandle lockHandle = CreateOwned(lockPath, 0x80000000 | 0x40000000, OpenAlways, 0x80, sid);
                if (lockHandle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error(); lockHandle.Dispose();
                    if (error == 32 || error == 33) { lastError = "writer_already_running"; return; }
                    throw Win32Failure(error);
                }
                pathLock = new FileStream(lockHandle, FileAccess.ReadWrite);
                AssertPrivate(pathLock.GetAccessControl(), sid);
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
                        byte[] bytes = encoding.GetBytes(json);
                        Publish(bytes, sid);
                        Interlocked.Increment(ref publishedCount);
                        lastError = null;
                    }
                    catch (UnauthorizedAccessException) { lastError = "access_denied"; }
                    catch (AmbiguousReplaceException) { lastError = "replace_state_requires_inspection"; return; }
                    // Stopping keeps at most one undeletable temp instead of one more per publication.
                    catch (TempCleanupException) { lastError = "temp_cleanup_failed"; return; }
                    catch (Exception) { lastError = "publication_failed"; }
                }
            }
            catch (UnauthorizedAccessException) { lastError = "access_denied"; }
            catch (Exception) { lastError = "publisher_start_failed"; }
            finally
            {
                lock (gate) { stopping = true; pending = null; }
                if (pathLock != null) pathLock.Dispose();
                // Mutex ownership belongs to this thread; only this thread releases it.
                if (owned) mutex.ReleaseMutex();
                if (mutex != null) mutex.Dispose();
                wake.Dispose();
            }
        }

        void Publish(byte[] bytes, SecurityIdentifier sid)
        {
            AssertNoReparse(directory);
            AssertPrivate(new DirectoryInfo(directory).GetAccessControl(), sid);
            // Target is checked immediately before every rename attempt below. A second
            // identical check here adds a filesystem/security query without protecting a mutation.
            string temp = Path.Combine(directory, "." + Path.GetFileName(TargetPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            SafeFileHandle handle = null;
            FileStream file = null;
            bool renamed = false, renameAttempted = false;
            try
            {
                // CREATE_NEW never opens an existing name or link. The temp stays exclusively open until it is renamed
                // or discarded through this handle, so no path is re-resolved for it after a failed check.
                handle = CreateOwned(temp, TempAccess, CreateNew, TempFlags, sid);
                if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); handle = null; throw Win32Failure(error); }
                file = new FileStream(handle, FileAccess.Write);
                file.Write(bytes, 0, bytes.Length); file.Flush();
                // Attest the exclusively held file itself, before it can become the target.
                FileSecurity tempSecurity;
                AssertPrivate(tempSecurity = file.GetAccessControl(), sid);
                var retry = Stopwatch.StartNew();
                int[] waits = { 1, 2, 4 };
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    if (attempt > 0 && retry.ElapsedMilliseconds >= 25) throw new IOException("retry_deadline");
                    AssertNoReparse(directory);
                    FileSecurity targetSecurity = AssertTarget(sid);
                    if (targetSecurity != null) tempSecurity = CarryTargetDacl(file, tempSecurity, targetSecurity, sid);
                    renameAttempted = true;
                    int code = RenameToTarget(handle, TargetPath);
                    if (code == 0)
                    {
                        renamed = true;
                        return;
                    }
                    // A failed rename leaves the canonical target untouched; only bounded sharing conflicts are retried.
                    if ((code != 32 && code != 33) || attempt == 3 || retry.ElapsedMilliseconds + waits[attempt] >= 25) throw Win32Failure(code);
                    Thread.Sleep(waits[attempt]);
                }
            }
            finally
            {
                try { if (handle != null && !renamed) Discard(handle, temp, renameAttempted); }
                finally { if (file != null) file.Dispose(); else if (handle != null) handle.Dispose(); }
            }
        }

        FileSecurity AssertTarget(SecurityIdentifier sid)
        {
            if (Directory.Exists(TargetPath)) throw new UnauthorizedAccessException("target_directory");
            if (!File.Exists(TargetPath)) return null;
            if ((File.GetAttributes(TargetPath) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("target_reparse");
            FileSecurity security;
            AssertPrivate(security = new FileInfo(TargetPath).GetAccessControl(), sid);
            return security;
        }

        // Rename keeps the temp's own DACL, while ReplaceFile kept the replaced file's DACL. A target DACL that already
        // passed AssertPrivate (e.g. protected/narrowed) is carried onto the still-exclusive temp and attested again.
        static FileSecurity CarryTargetDacl(FileStream file, FileSecurity temp, FileSecurity target, SecurityIdentifier sid)
        {
            string wanted = target.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            if (wanted == temp.GetSecurityDescriptorSddlForm(AccessControlSections.Access)) return temp;
            var carried = new FileSecurity();
            carried.SetSecurityDescriptorSddlForm(wanted, AccessControlSections.Access);
            file.SetAccessControl(carried);
            FileSecurity applied = file.GetAccessControl();
            AssertPrivate(applied, sid);
            // Fail closed for every target DACL: SetSecurityInfo may re-inherit directory ACEs into an unprotected DACL.
            if (applied.GetSecurityDescriptorSddlForm(AccessControlSections.Access) != wanted) throw new UnauthorizedAccessException("target_acl_not_preserved");
            return applied;
        }

        // Rename of the exclusively held temp onto exactly the absolute TargetPath that AssertTarget just checked
        // (RootDirectory = NULL), in the temp's own directory; never a copy across volumes. FileRenameInfoEx with POSIX
        // semantics replaces a target that compatible readers hold with FILE_SHARE_DELETE (their handles keep the old
        // complete file); without the extended class, classic replace-if-exists.
        static int RenameToTarget(SafeFileHandle handle, string targetPath)
        {
            int code = SetRename(handle, FileRenameInfoEx, RenameReplaceIfExists | RenamePosixSemantics, targetPath);
            if (code == 1 || code == 50 || code == 87) code = SetRename(handle, FileRenameInfo, 1, targetPath);
            return code;
        }

        static int SetRename(SafeFileHandle handle, int infoClass, int flags, string targetPath)
        {
            // FILE_RENAME_INFO: union{ReplaceIfExists,Flags} DWORD, HANDLE RootDirectory (NULL), DWORD FileNameLength,
            // WCHAR FileName[] followed by a zero terminator inside the zeroed buffer.
            int nameBytes = targetPath.Length * 2, nameOffset = 2 * IntPtr.Size + 4, size = 2 * IntPtr.Size + 8 + nameBytes;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                for (int offset = 0; offset < size; offset++) Marshal.WriteByte(buffer, offset, 0);
                Marshal.WriteInt32(buffer, 0, flags);
                Marshal.WriteInt32(buffer, 2 * IntPtr.Size, nameBytes);
                Marshal.Copy(targetPath.ToCharArray(), 0, IntPtr.Add(buffer, nameOffset), targetPath.Length);
                return SetFileInformationByHandle(handle, infoClass, buffer, (uint)size) ? 0 : Marshal.GetLastWin32Error();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        // Deletes exactly the object this publication created, through its own DELETE handle and without an ACL re-check
        // by path. After a failed rename the handle must still carry the generated temp name; otherwise the object may
        // already be the canonical target and nothing is deleted.
        static void Discard(SafeFileHandle handle, string temp, bool renameAttempted)
        {
            if (renameAttempted && !HandleHasName(handle, Path.GetFileName(temp))) throw new AmbiguousReplaceException();
            byte delete = 1;
            if (!SetFileInformationByHandle(handle, FileDispositionInfo, ref delete, 1)) throw new TempCleanupException();
        }

        static bool HandleHasName(SafeFileHandle handle, string name)
        {
            try { return string.Equals(Path.GetFileName(FinalDirectoryPath(handle)), name, StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        static Exception Win32Failure(int code)
        {
            if (code == 5) return new UnauthorizedAccessException("win32_access_denied");
            return new IOException("win32_" + code, unchecked((int)0x80070000) | code);
        }

        static void PrepareDirectory(string path, SecurityIdentifier sid)
        {
            AssertNoReparse(path);
            if (!Directory.Exists(path))
            {
                var acl = new DirectorySecurity();
                acl.SetAccessRuleProtection(true, false);
                acl.SetOwner(sid);
                var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(path).Create(acl);
            }
            AssertNoReparse(path);
            AssertPrivate(new DirectoryInfo(path).GetAccessControl(), sid);
        }

        void AssertFixtureLiveGuard()
        {
            if (fixtureLiveRoots == null) return;
            string selected = CanonicalDirectoryPath(directory);
            foreach (string liveRoot in fixtureLiveRoots)
            {
                if (Within(selected, CanonicalDirectoryPath(liveRoot))) throw new ArgumentException("fixture_live_directory_forbidden");
            }
        }

        static string CanonicalDirectoryPath(string path)
        {
            string current = NormalizeDirectoryPath(path);
            var tail = new List<string>();
            while (true)
            {
                using (var handle = CreateFileW(current, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
                {
                    if (!handle.IsInvalid)
                    {
                        string resolved = FinalDirectoryPath(handle);
                        for (int index = tail.Count - 1; index >= 0; index--) resolved = Path.Combine(resolved, tail[index]);
                        return resolved;
                    }
                    int error = Marshal.GetLastWin32Error();
                    if (error != 2 && error != 3) throw CanonicalPathError();
                }
                DirectoryInfo parent = Directory.GetParent(current);
                if (parent == null) throw CanonicalPathError();
                string name = Path.GetFileName(current);
                if (string.IsNullOrEmpty(name)) throw CanonicalPathError();
                tail.Add(name);
                current = parent.FullName;
            }
        }

        static string FinalDirectoryPath(SafeFileHandle handle)
        {
            int capacity = 256;
            while (true)
            {
                var buffer = new StringBuilder(capacity);
                uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)capacity, 0);
                if (length == 0) throw CanonicalPathError();
                if (length < (uint)capacity)
                {
                    string final = buffer.ToString(0, (int)length);
                    const string prefix = "\\\\?\\";
                    if (final.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("absolute_local_directory_required");
                    if (!final.StartsWith(prefix, StringComparison.Ordinal)) throw CanonicalPathError();
                    string dos = final.Substring(prefix.Length);
                    if (dos.Length < 3 || dos[1] != ':' || dos[2] != '\\') throw CanonicalPathError();
                    return NormalizeDirectoryPath(dos);
                }
                if (length > 32768) throw CanonicalPathError();
                capacity = (int)length;
            }
        }

        static string NormalizeDirectoryPath(string path)
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
        }

        static IOException CanonicalPathError() { return new IOException("canonical_path_resolution_failed"); }

        // Without a security descriptor a new file is owned by the token's default owner. For an elevated member of
        // Administrators (UAC off, e.g. hosted CI runners) that is BUILTIN\Administrators, which AssertPrivate rightly
        // rejects as foreign_owner. The descriptor sets only the owner to this user's SID (always a permitted owner);
        // no DACL is present, so the file inherits exactly the verified private directory ACEs. Existing files
        // (OPEN_ALWAYS on a present lock) ignore it and keep being attested by owner/DACL.
        static SafeFileHandle CreateOwned(string path, uint access, uint disposition, uint flags, SecurityIdentifier owner)
        {
            var descriptor = new RawSecurityDescriptor(ControlFlags.SelfRelative, owner, null, null, null);
            byte[] binary = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(binary, 0);
            var pin = GCHandle.Alloc(binary, GCHandleType.Pinned);
            try
            {
                var attributes = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Descriptor = pin.AddrOfPinnedObject(), InheritHandle = 0 };
                return CreateFileW(path, access, 0, ref attributes, disposition, flags, IntPtr.Zero);
            }
            finally { pin.Free(); }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int InheritHandle; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string path, uint desiredAccess, uint shareMode, ref SecurityAttributes securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string path, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint bufferSize, uint flags);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, IntPtr info, uint size);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref byte info, uint size);

        static bool Within(string path, string root)
        {
            root = root.TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(path, root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        internal static void AssertNoReparse(string path)
        {
            var current = new DirectoryInfo(path);
            while (current != null)
            {
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("reparse_directory");
                current = current.Parent;
            }
        }

        static void AssertPrivate(FileSystemSecurity acl, SecurityIdentifier sid)
        {
            if (!sid.Equals(acl.GetOwner(typeof(SecurityIdentifier)))) throw new UnauthorizedAccessException("foreign_owner");
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                var identity = (SecurityIdentifier)rule.IdentityReference;
                if (!identity.Equals(sid) && !identity.IsWellKnown(WellKnownSidType.LocalSystemSid) && !identity.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)) throw new UnauthorizedAccessException("non_private_acl");
            }
        }

        sealed class AmbiguousReplaceException : IOException { }
        sealed class TempCleanupException : IOException { }

        public void Dispose()
        {
            lock (gate) { if (stopping) return; stopping = true; pending = null; wake.Set(); }
            if (Thread.CurrentThread != worker) worker.Join(1500);
        }
    }
}
