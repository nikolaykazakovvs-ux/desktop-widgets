using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ResourcesWidgetSensor
{
    // One fixed, administrator-owned installation per account. No writable configuration or executable paths.
    static class SensorSecurity
    {
        public const string MarkerName = ".resources-widget-sensor";
        public const string MarkerValue = "ResourcesWidget local CPU sensor v1";
        public static readonly SecurityIdentifier SystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        public static readonly SecurityIdentifier AdminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        public static readonly SecurityIdentifier NetworkSid = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        public static string Root { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ResourcesWidgetSensor"); } }

        public static SecurityIdentifier UserSid(string value)
        {
            var sid = new SecurityIdentifier(value);
            if (!sid.IsAccountSid() || sid.AccountDomainSid == null || sid.Value.Split('-').Length != 8 || sid.Value != value)
                throw new ArgumentException("An explicit canonical Windows account SID is required.");
            return sid;
        }
        public static string UserDirectory(string sid) { return Path.Combine(Root, UserSid(sid).Value); }
        public static string SnapshotPath(string sid) { return Path.Combine(UserDirectory(sid), "temperature.json"); }
        public static string TaskName(string sid) { return "ResourcesWidgetSensor-" + UserSid(sid).Value; }

        public static void RejectReparseChain(string path)
        {
            for (var dir = new DirectoryInfo(Path.GetFullPath(path)); dir != null; dir = dir.Parent)
            {
                try
                {
                    if ((File.GetAttributes(dir.FullName) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse points are not supported in sensor paths.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
        public static void RejectReparseFile(string path)
        {
            try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Sensor files must not be links."); }
            catch (FileNotFoundException) { }
        }

        public static DirectorySecurity NewDirectorySecurity(string sid)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(AdminSid);
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(NetworkSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Deny));
            foreach (var owner in new[] { SystemSid, AdminSid }) security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            if (sid != null) security.AddAccessRule(new FileSystemAccessRule(UserSid(sid), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            else security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            return security;
        }

        public static void SecureFile(string path, string sid)
        {
            RejectReparseFile(path);
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(AdminSid);
            security.AddAccessRule(new FileSystemAccessRule(NetworkSid, FileSystemRights.FullControl, AccessControlType.Deny));
            foreach (var owner in new[] { SystemSid, AdminSid }) security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
            if (sid != null) security.AddAccessRule(new FileSystemAccessRule(UserSid(sid), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            File.SetAccessControl(path, security);
            AssertFile(path, sid);
        }

        public static void AssertDescriptor(FileSystemSecurity security, string sid, bool requireProtected)
        {
            var owner = (SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier));
            if (!owner.Equals(AdminSid) && !owner.Equals(SystemSid)) throw new IOException("Sensor path owner must be SYSTEM or Administrators.");
            if (requireProtected && !security.AreAccessRulesProtected) throw new IOException("Sensor directory inheritance must be disabled.");
            const FileSystemRights writable = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            bool denyNetwork = false, userRead = sid == null;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                var who = (SecurityIdentifier)rule.IdentityReference;
                if (rule.AccessControlType == AccessControlType.Deny && who.Equals(NetworkSid) && (rule.FileSystemRights & FileSystemRights.ReadData) != 0) denyNetwork = true;
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if (!who.Equals(AdminSid) && !who.Equals(SystemSid) && (rule.FileSystemRights & writable) != 0) throw new IOException("Non-administrator write access to sensor path.");
                if (sid != null && who.Value == sid && (rule.FileSystemRights & FileSystemRights.ReadData) != 0) userRead = true;
            }
            if (!denyNetwork || !userRead) throw new IOException("Sensor ACL must deny network access and allow the intended local account to read.");
        }

        public static void AssertDirectory(string path, string sid)
        {
            RejectReparseChain(path);
            AssertDescriptor(Directory.GetAccessControl(path, AccessControlSections.Access | AccessControlSections.Owner), sid, true);
        }
        public static void AssertFile(string path, string sid)
        {
            RejectReparseFile(path);
            AssertDescriptor(File.GetAccessControl(path, AccessControlSections.Access | AccessControlSections.Owner), sid, false);
        }
        public static void EnsureDirectory(string path, string sid)
        {
            RejectReparseChain(path);
            if (Directory.Exists(path)) AssertDirectory(path, sid);
            else { Directory.CreateDirectory(path, NewDirectorySecurity(sid)); AssertDirectory(path, sid); }
        }
        public static bool IsAdministrator()
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        }
        public static void AssertInstalledDirectory(string sid)
        {
            AssertDirectory(Root, null);
            string dir = UserDirectory(sid);
            AssertDirectory(dir, sid);
            string marker = Path.Combine(dir, MarkerName);
            AssertFile(marker, sid);
            if (File.ReadAllText(marker) != MarkerValue) throw new IOException("Sensor ownership marker mismatch.");
        }
    }
}
