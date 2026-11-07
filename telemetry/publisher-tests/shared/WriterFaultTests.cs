using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using DesktopWidgets.Telemetry;
using Microsoft.Win32.SafeHandles;

// Test-only hooks referenced by the additively fault-injected writer copy built by run-fault.ps1.
// The production writer has no such entry points.
public static class WriterFaultProbe
{
    public static int RenameFailures, RenameFailureCode = 32, InjectedRenameFailures;
    public static bool RenameThenReportFailure;
    public static int TempAclFaults, TempAclFaultsApplied;
    public static bool NarrowUnprotectedTarget, FaultDescriptorUnprotected;
    public static int TargetSecurityFaultsApplied, FaultDescriptorRuleCount;

    // Test-only substitution of the input descriptor handed to CarryTargetDacl: a narrow private DACL WITHOUT the
    // protected flag. The target file on disk is not changed; the copy then runs the real file.SetAccessControl.
    public static FileSecurity TargetSecurityFault(FileSecurity target, SecurityIdentifier sid)
    {
        if (!NarrowUnprotectedTarget) return target;
        var narrow = new FileSecurity();
        narrow.SetSecurityDescriptorSddlForm("D:(A;;FA;;;" + sid.Value + ")", AccessControlSections.Access);
        FaultDescriptorUnprotected = !narrow.AreAccessRulesProtected;
        FaultDescriptorRuleCount = narrow.GetAccessRules(true, true, typeof(SecurityIdentifier)).Count;
        Interlocked.Increment(ref TargetSecurityFaultsApplied);
        return narrow;
    }

    public static int RenameFault(SafeFileHandle handle, string targetPath)
    {
        if (RenameThenReportFailure)
        {
            // The rename really happens, then a non-retryable failure is reported: the writer must not delete its object.
            RenameThenReportFailure = false;
            if (!RealRename(handle, targetPath)) throw new Exception("probe_rename_failed");
            return 1117;
        }
        if (RenameFailures > 0)
        {
            RenameFailures--;
            Interlocked.Increment(ref InjectedRenameFailures);
            return RenameFailureCode;
        }
        return 0;
    }

    // Reproduces the F1 trigger on the open temp: its DACL stops being private before the writer attests it.
    public static void BeforeTempAttest(FileStream file)
    {
        if (TempAclFaults <= 0) return;
        TempAclFaults--;
        var acl = file.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
        file.SetAccessControl(acl);
        Interlocked.Increment(ref TempAclFaultsApplied);
    }

    // Same documented call as the writer: FileRenameInfoEx, REPLACE_IF_EXISTS | POSIX_SEMANTICS, RootDirectory = NULL, full path.
    static bool RealRename(SafeFileHandle handle, string targetPath)
    {
        int nameBytes = targetPath.Length * 2, size = 2 * IntPtr.Size + 8 + nameBytes;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (int offset = 0; offset < size; offset++) Marshal.WriteByte(buffer, offset, 0);
            Marshal.WriteInt32(buffer, 0, 0x1 | 0x2);
            Marshal.WriteInt32(buffer, 2 * IntPtr.Size, nameBytes);
            Marshal.Copy(targetPath.ToCharArray(), 0, IntPtr.Add(buffer, 2 * IntPtr.Size + 4), targetPath.Length);
            return SetFileInformationByHandle(handle, 22, buffer, (uint)size);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, IntPtr info, uint size);
}

static class WriterFaultTests
{
    // The test checks the writer's retry logic, not the system timer: without a 1 ms request Sleep(1) can take ~15-21 ms.
    [DllImport("winmm.dll", ExactSpelling = true)] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll", ExactSpelling = true)] static extern uint timeEndPeriod(uint ms);
    static int checks;
    static void Check(bool condition, string id) { if (!condition) throw new Exception(id); checks++; }
    static void Until(Func<bool> check, string id) { var timer = Stopwatch.StartNew(); while (!check()) { if (timer.ElapsedMilliseconds > 3000) throw new Exception(id); Thread.Sleep(5); } }
    static TelemetrySnapshotWriter New(string dir) { return new TelemetrySnapshotWriter(new TelemetryWriterOptions { DirectoryPath = dir, FileName = "hardware-snapshot.json", Mode = "fixture", MinIntervalMs = 1 }); }
    static string Read(string path) { using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) using (var r = new StreamReader(f)) return r.ReadToEnd(); }
    static int Temps(string dir) { return Directory.GetFiles(dir, "*.tmp").Length; }

    static int Main(string[] args)
    {
        timeBeginPeriod(1);
        try { return Run(args); } finally { timeEndPeriod(1); }
    }

    static int Run(string[] args)
    {
        try
        {
            string root = Path.GetFullPath(args[0]);

            // F2: a transient sharing failure on the first publication is retried, not a permanent ambiguous stop.
            string firstTransient = Path.Combine(root, "first-transient");
            WriterFaultProbe.RenameFailures = 2; WriterFaultProbe.InjectedRenameFailures = 0;
            using (var w = New(firstTransient))
            {
                w.Submit(_ => "{\"first\":1}"); Until(() => w.PublishedCount == 1 || w.LastError != null, "first_transient_result");
                Check(w.PublishedCount == 1 && w.LastError == null && WriterFaultProbe.InjectedRenameFailures == 2, "first_publication_transient_sharing_retried");
                Check(Read(w.TargetPath) == "{\"first\":1}" && Temps(firstTransient) == 0, "first_publication_transient_complete");
            }

            // F2: sharing beyond the bounded retry with no prior target is an ordinary failure; the writer keeps working.
            string firstPersistent = Path.Combine(root, "first-persistent");
            WriterFaultProbe.RenameFailures = 100;
            using (var w = New(firstPersistent))
            {
                var bounded = Stopwatch.StartNew();
                w.Submit(_ => "{\"first\":2}"); Until(() => w.LastError != null, "first_persistent_result");
                Check(bounded.ElapsedMilliseconds < 1000, "first_publication_failure_bounded");
                WriterFaultProbe.RenameFailures = 0;
                Check(w.LastError == "publication_failed" && w.PublishedCount == 0, "first_publication_sharing_not_ambiguous");
                Check(!File.Exists(w.TargetPath) && Temps(firstPersistent) == 0, "first_publication_failure_leaves_nothing");
                Check(w.Submit(_ => "{\"first\":3}"), "writer_still_accepting");
                Until(() => w.PublishedCount == 1, "first_publication_recovery");
                Check(Read(w.TargetPath) == "{\"first\":3}" && w.LastError == null, "first_publication_recovered");
            }

            // F1: repeated temp ACL attestation failures do not accumulate temp files.
            string tempAcl = Path.Combine(root, "temp-acl");
            using (var w = New(tempAcl))
            {
                w.Submit(_ => "{\"v\":0}"); Until(() => w.PublishedCount == 1, "temp_acl_setup");
                WriterFaultProbe.TempAclFaultsApplied = 0; WriterFaultProbe.TempAclFaults = 5;
                for (int n = 1; n <= 5; n++)
                {
                    int expected = n;
                    w.Submit(_ => "{\"v\":" + expected + "}"); Until(() => WriterFaultProbe.TempAclFaultsApplied == expected, "temp_acl_fault_" + n);
                }
                Until(() => w.LastError == "access_denied", "temp_acl_rejected");
                Check(Read(w.TargetPath) == "{\"v\":0}", "temp_acl_failure_preserves_target");
                w.Submit(_ => "{\"v\":9}"); Until(() => w.PublishedCount == 2, "temp_acl_recovery");
                Check(Temps(tempAcl) == 0, "temp_acl_failures_bounded_no_temp_growth");
                Check(Read(w.TargetPath) == "{\"v\":9}", "temp_acl_recovered");
            }

            // P2 counterfactual (fault-injected INPUT DESCRIPTOR, not a physically unprotected file): an unprotected narrow
            // target DACL that cannot be carried exactly must be refused before rename. The former protected-only guard
            // would have let the re-inherited result through.
            string unprotected = Path.Combine(root, "unprotected-descriptor");
            using (var w = New(unprotected))
            {
                w.Submit(_ => "{\"u\":0}"); Until(() => w.PublishedCount == 1, "unprotected_setup");
                string oldSddl = File.GetAccessControl(w.TargetPath).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                WriterFaultProbe.TargetSecurityFaultsApplied = 0; WriterFaultProbe.NarrowUnprotectedTarget = true;
                w.Submit(_ => "{\"u\":1}"); Until(() => w.LastError != null, "unprotected_result");
                WriterFaultProbe.NarrowUnprotectedTarget = false;
                Check(WriterFaultProbe.TargetSecurityFaultsApplied >= 1 && WriterFaultProbe.FaultDescriptorUnprotected && WriterFaultProbe.FaultDescriptorRuleCount == 1, "unprotected_descriptor_precondition");
                Check(w.LastError == "access_denied" && w.PublishedCount == 1, "unprotected_descriptor_mismatch_rejected");
                Check(Read(w.TargetPath) == "{\"u\":0}" && File.GetAccessControl(w.TargetPath).GetSecurityDescriptorSddlForm(AccessControlSections.Access) == oldSddl, "unprotected_descriptor_target_unchanged");
                Check(Temps(unprotected) == 0, "unprotected_descriptor_no_temp");
                w.Submit(_ => "{\"u\":2}"); Until(() => w.PublishedCount == 2, "unprotected_recovery");
                Check(Read(w.TargetPath) == "{\"u\":2}" && w.LastError == null && Temps(unprotected) == 0, "unprotected_descriptor_recovered");
            }

            // Ambiguous: the rename happened but failure was reported. The canonical target is never deleted; writer stops.
            string ambiguous = Path.Combine(root, "ambiguous");
            using (var w = New(ambiguous))
            {
                w.Submit(_ => "{\"a\":0}"); Until(() => w.PublishedCount == 1, "ambiguous_setup");
                WriterFaultProbe.RenameThenReportFailure = true;
                w.Submit(_ => "{\"a\":1}"); Until(() => w.LastError != null, "ambiguous_result");
                Check(w.LastError == "replace_state_requires_inspection" && w.PublishedCount == 1, "ambiguous_stops_writer");
                Check(Read(w.TargetPath) == "{\"a\":1}" && Temps(ambiguous) == 0, "ambiguous_target_not_deleted");
                Until(() => !w.Submit(_ => "{}"), "ambiguous_writer_stopped");
            }

            Console.WriteLine("PASS writer-fault " + checks + " assertions"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex.Message); return 1; }
    }
}
