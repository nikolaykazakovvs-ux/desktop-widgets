using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using DesktopWidgets.Telemetry;

static class WriterTests
{
    static int checks;
    static void Check(bool condition, string id) { if (!condition) throw new Exception(id); checks++; }
    static void Until(Func<bool> check, string id) { var timer = Stopwatch.StartNew(); while (!check()) { if (timer.ElapsedMilliseconds > 3000) throw new Exception(id); Thread.Sleep(5); } }
    static TelemetrySnapshotWriter New(string dir, string file) { return new TelemetrySnapshotWriter(new TelemetryWriterOptions { DirectoryPath = dir, FileName = file, Mode = "fixture", MinIntervalMs = 1 }); }
    static TelemetrySnapshotWriter NewLive(string dir, string file) { return new TelemetrySnapshotWriter(new TelemetryWriterOptions { DirectoryPath = dir, FileName = file, Mode = "live", MinIntervalMs = 1 }); }
    static string Read(string path) { using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) using (var r = new StreamReader(f)) return r.ReadToEnd(); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint bufferSize);
    static string ShortPath(string path)
    {
        uint length = GetShortPathNameW(path, null, 0);
        if (length == 0 || length > 32768) throw new IOException("short_path_query_failed");
        var buffer = new StringBuilder((int)length);
        uint written = GetShortPathNameW(path, buffer, length);
        if (written == 0 || written >= length) throw new IOException("short_path_query_failed");
        return buffer.ToString(0, (int)written);
    }
    static bool PrivateAcl(FileSystemSecurity acl)
    {
        var sid = WindowsIdentity.GetCurrent().User;
        if (!sid.Equals(acl.GetOwner(typeof(SecurityIdentifier)))) return false;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            var identity = (SecurityIdentifier)rule.IdentityReference;
            if (!identity.Equals(sid) && !identity.IsWellKnown(WellKnownSidType.LocalSystemSid) && !identity.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)) return false;
        }
        return true;
    }
    static int Main(string[] args)
    {
        try
        {
            if (args[0] == "--contend")
            {
                using (var w = New(args[1], args[2])) { w.Submit(_ => "{\"child\":true}"); Until(() => w.LastError != null || w.PublishedCount > 0, "contender_ready"); return w.LastError == "writer_already_running" ? 0 : 8; }
            }
            if (args[0] == "--reject-path")
            {
                using (var w = New(args[1], "hardware-snapshot.json")) { w.Submit(_ => "{}"); Until(() => w.LastError != null || w.PublishedCount > 0, "path_result"); return w.LastError == "access_denied" ? 0 : 9; }
            }
            string root = Path.GetFullPath(args[0]);
            string dir = Path.Combine(root, "private");
            using (var w = New(dir, "hardware-snapshot.json"))
            {
                w.Submit(_ => "{\"n\":0}"); Until(() => w.PublishedCount == 1, "first_publish");
                Check(Read(w.TargetPath) == "{\"n\":0}", "first_move");
                var acl = Directory.GetAccessControl(dir);
                Check(acl.AreAccessRulesProtected && acl.GetOwner(typeof(SecurityIdentifier)).Equals(WindowsIdentity.GetCurrent().User), "private_owned_acl");
                var start = new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location, "--contend \"" + dir + "\" hardware-snapshot.json") { UseShellExecute = false, CreateNoWindow = true };
                using (var child = Process.Start(start)) { Check(child.WaitForExit(5000) && child.ExitCode == 0, "cross_process_same_path_rejected"); }
                using (var other = New(dir, "quotas-snapshot.json")) { other.Submit(_ => "{\"q\":1}"); Until(() => other.PublishedCount == 1, "independent_paths"); Check(Read(other.TargetPath) == "{\"q\":1}", "distinct_filename"); }
                var entered = new ManualResetEvent(false); var release = new ManualResetEvent(false);
                w.Submit(_ => { entered.Set(); release.WaitOne(); return "{\"n\":1}"; });
                Check(entered.WaitOne(3000), "worker_callback");
                var cost = Stopwatch.StartNew();
                for (int n = 2; n <= 1000; n++) { int frozen = n; w.Submit(_ => "{\"n\":" + frozen + "}"); }
                Check(cost.ElapsedMilliseconds < 500, "submit_nonblocking"); release.Set();
                Until(() => w.PublishedCount == 3, "latest_slot_drain");
                Check(Read(w.TargetPath) == "{\"n\":1000}", "latest_slot_value");
                // An open compatible reader sees an entire old version while the rename publishes a new one.
                using (var reader = new FileStream(w.TargetPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    w.Submit(_ => "{\"n\":1001}"); Until(() => w.PublishedCount == 4, "replace_with_reader");
                    Check(Read(w.TargetPath) == "{\"n\":1001}", "new_open_sees_new_version");
                    using (var text = new StreamReader(reader)) Check(text.ReadToEnd() == "{\"n\":1000}", "old_handle_immutable");
                }
                Check(PrivateAcl(File.GetAccessControl(w.TargetPath)), "renamed_target_acl_not_expanded");
                string old = Read(w.TargetPath);
                using (var blocker = new FileStream(w.TargetPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var bounded = Stopwatch.StartNew();
                    w.Submit(_ => "{\"n\":2000}"); Until(() => w.LastError != null, "sharing_failure");
                    Check(bounded.ElapsedMilliseconds < 1000, "sharing_failure_bounded");
                    Check(w.PublishedCount == 4, "failed_replace_not_counted");
                    Check(Directory.GetFiles(dir, "*.tmp").Length == 0, "temp_discarded_after_sharing_failure");
                }
                Check(Read(w.TargetPath) == old, "old_target_survives_ordinary_sharing_failure");
                w.Submit(_ => "{\"n\":2001}"); Until(() => w.PublishedCount == 5, "recovery_after_sharing");
                // A non-private ACE added to the directory after start rejects the next publication without touching the target.
                var dirAcl = Directory.GetAccessControl(dir);
                var everyone = new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow);
                dirAcl.AddAccessRule(everyone); Directory.SetAccessControl(dir, dirAcl);
                w.Submit(_ => "{\"n\":3000}"); Until(() => w.LastError != null, "directory_acl_change_check");
                Check(w.LastError == "access_denied" && w.PublishedCount == 5 && Read(w.TargetPath) == "{\"n\":2001}", "directory_acl_changed_after_start_rejected");
                Check(Directory.GetFiles(dir, "*.tmp").Length == 0, "no_temp_after_directory_rejection");
                dirAcl.RemoveAccessRule(everyone); Directory.SetAccessControl(dir, dirAcl);
                w.Submit(_ => "{\"n\":3001}"); Until(() => w.PublishedCount == 6, "recovery_after_directory_acl_restore");
                w.Submit(_ => new string('x', TelemetrySnapshotWriter.MaxBytes + 1)); Until(() => w.LastError == "invalid_input", "size_rejection");
                Check(Read(w.TargetPath) == "{\"n\":3001}", "oversize_preserves_target");
                Check(Directory.GetFiles(dir, "*.tmp").Length == 0, "bounded_temp_cleanup");
                entered.Dispose(); release.Dispose();
            }
            using (var restarted = New(dir, "hardware-snapshot.json")) { restarted.Submit(_ => "{\"restart\":true}"); Until(() => restarted.PublishedCount == 1, "mutex_released_on_worker"); }
            bool rejected = false;
            try { using (var bad = New("relative", "hardware-snapshot.json")) { } } catch (ArgumentException) { rejected = true; }
            Check(rejected, "relative_rejected"); rejected = false;
            try { using (var bad = New("\\\\server\\share", "hardware-snapshot.json")) { } } catch (ArgumentException) { rejected = true; }
            Check(rejected, "unc_rejected"); rejected = false;
            try { using (var bad = New(dir, "foreign.json")) { } } catch (ArgumentException) { rejected = true; }
            Check(rejected, "filename_allowlist");
            using (var balances = New(dir, "api-balances-snapshot.json")) { balances.Submit(_ => "{\"b\":1}"); Until(() => balances.PublishedCount == 1, "api_balances_name"); Check(Read(balances.TargetPath) == "{\"b\":1}", "api_balances_filename_accepted"); }
            rejected = false;
            try { using (var bad = New(dir, "api-balances-snapshot.json.bak")) { } } catch (ArgumentException) { rejected = true; }
            Check(rejected, "api_balances_name_must_be_exact");
            rejected = false;
            try { using (var bad = New(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopWidgets", "telemetry"), "hardware-snapshot.json")) { } } catch (ArgumentException) { rejected = true; }
            Check(rejected, "fixture_standard_live_directory_rejected");
            string oldEnv = Environment.GetEnvironmentVariable("DESKTOP_WIDGETS_TELEMETRY_DIR");
            try
            {
                string configured = Path.Combine(root, "configured-live"); Environment.SetEnvironmentVariable("DESKTOP_WIDGETS_TELEMETRY_DIR", configured);
                rejected = false;
                try { using (var bad = New(configured, "hardware-snapshot.json")) { } } catch (ArgumentException) { rejected = true; }
                Check(rejected, "fixture_configured_live_directory_rejected");
                string aliasLive = Path.Combine(root, "alias-live"), liveTarget;
                using (var live = NewLive(aliasLive, "hardware-snapshot.json"))
                {
                    liveTarget = live.TargetPath;
                    live.Submit(_ => "{\"live\":true}"); Until(() => live.PublishedCount == 1, "dos83_live_setup");
                }
                string alias = ShortPath(aliasLive);
                if (string.Equals(alias, aliasLive, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("SKIP dos83_alias_live_guard: no DOS short-name alias for the isolated D: test directory");
                }
                else
                {
                    Environment.SetEnvironmentVariable("DESKTOP_WIDGETS_TELEMETRY_DIR", aliasLive);
                    rejected = false;
                    try { using (var bad = New(alias, "hardware-snapshot.json")) { } } catch (ArgumentException) { rejected = true; }
                    Check(rejected, "fixture_dos83_selected_alias_rejected");
                    Environment.SetEnvironmentVariable("DESKTOP_WIDGETS_TELEMETRY_DIR", alias);
                    rejected = false;
                    try { using (var bad = New(aliasLive, "hardware-snapshot.json")) { } } catch (ArgumentException) { rejected = true; }
                    Check(rejected, "fixture_dos83_live_root_alias_rejected");
                    Environment.SetEnvironmentVariable("DESKTOP_WIDGETS_TELEMETRY_DIR", aliasLive);
                    string aliasChild = Path.Combine(alias, "forbidden-child"); rejected = false;
                    try { using (var bad = New(aliasChild, "hardware-snapshot.json")) { } } catch (ArgumentException) { rejected = true; }
                    Check(rejected && !Directory.Exists(aliasChild), "fixture_dos83_alias_child_rejected_before_create");
                    Check(Read(liveTarget) == "{\"live\":true}", "dos83_live_snapshot_preserved");
                }
            }
            finally { Environment.SetEnvironmentVariable("DESKTOP_WIDGETS_TELEMETRY_DIR", oldEnv); }
            string broad = Path.Combine(root, "broad"); Directory.CreateDirectory(broad);
            var broadAcl = Directory.GetAccessControl(broad);
            broadAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow)); Directory.SetAccessControl(broad, broadAcl);
            using (var bad = New(broad, "hardware-snapshot.json")) { bad.Submit(_ => "{}"); Until(() => bad.LastError != null, "broad_acl_check"); Check(bad.LastError == "access_denied" && !File.Exists(bad.TargetPath), "broad_acl_rejected_without_repair"); }
            string foreignDir = Path.Combine(root, "foreign-target");
            using (var good = New(foreignDir, "hardware-snapshot.json")) { good.Submit(_ => "{\"old\":true}"); Until(() => good.PublishedCount == 1, "foreign_test_setup"); }
            string foreign = Path.Combine(foreignDir, "hardware-snapshot.json"); var foreignAcl = File.GetAccessControl(foreign);
            foreignAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow)); File.SetAccessControl(foreign, foreignAcl);
            using (var bad = New(foreignDir, "hardware-snapshot.json")) { bad.Submit(_ => "{}"); Until(() => bad.LastError != null, "target_acl_check"); Check(bad.LastError == "access_denied" && Read(foreign) == "{\"old\":true}", "nonprivate_target_preserved"); }
            Check(Directory.GetFiles(foreignDir, "*.tmp").Length == 0, "temp_discarded_after_target_rejection");
            // A protected, narrowed target DACL survives replacement exactly, as it did with ReplaceFile.
            string narrowDir = Path.Combine(root, "narrowed-target");
            using (var narrow = New(narrowDir, "hardware-snapshot.json"))
            {
                narrow.Submit(_ => "{\"v\":0}"); Until(() => narrow.PublishedCount == 1, "narrow_setup");
                Check(PrivateAcl(File.GetAccessControl(narrow.TargetPath)), "first_rename_acl_not_expanded");
                var tight = new FileSecurity();
                tight.SetAccessRuleProtection(true, false);
                tight.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl, AccessControlType.Allow));
                File.SetAccessControl(narrow.TargetPath, tight);
                string narrowed = File.GetAccessControl(narrow.TargetPath).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                narrow.Submit(_ => "{\"v\":1}"); Until(() => narrow.PublishedCount == 2 || narrow.LastError != null, "narrow_publish");
                Check(narrow.PublishedCount == 2 && Read(narrow.TargetPath) == "{\"v\":1}", "narrowed_target_replaced");
                var after = File.GetAccessControl(narrow.TargetPath);
                Check(after.AreAccessRulesProtected && after.GetSecurityDescriptorSddlForm(AccessControlSections.Access) == narrowed && PrivateAcl(after), "narrowed_target_dacl_preserved");
                Check(Directory.GetFiles(narrowDir, "*.tmp").Length == 0, "narrow_no_temp");
            }
            Console.WriteLine("PASS shared-writer " + checks + " assertions"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex.Message); return 1; }
    }
}
