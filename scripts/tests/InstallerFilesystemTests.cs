using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ResourcesWidgetSetup;

// Exercises the same write/remove operations used by the real installer, in one explicit scratch tree.
// No installed-profile registry writes, shortcuts, process control, widget UI or elevation.
static class InstallerFilesystemTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Reject(Action action, string message)
    {
        try { action(); }
        catch (IOException) { return; }
        throw new Exception("Expected rejection: " + message);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string file, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle file, uint control, byte[] input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);

    // NTFS junction is created only inside our scratch tree and points to a second scratch folder.
    // Uses native API, no shell and no administrator/developer-mode permission.
    static void Junction(string path, string target)
    {
        Directory.CreateDirectory(path);
        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        byte[] printable = Encoding.Unicode.GetBytes(target);
        byte[] data = new byte[16 + substitute.Length + 2 + printable.Length + 2];
        Array.Copy(BitConverter.GetBytes(0xA0000003u), 0, data, 0, 4);
        Array.Copy(BitConverter.GetBytes((ushort)(data.Length - 8)), 0, data, 4, 2);
        Array.Copy(BitConverter.GetBytes((ushort)substitute.Length), 0, data, 10, 2);
        Array.Copy(BitConverter.GetBytes((ushort)(substitute.Length + 2)), 0, data, 12, 2);
        Array.Copy(BitConverter.GetBytes((ushort)printable.Length), 0, data, 14, 2);
        Array.Copy(substitute, 0, data, 16, substitute.Length);
        Array.Copy(printable, 0, data, 16 + substitute.Length + 2, printable.Length);
        using (var handle = CreateFile(path, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
        {
            uint returned;
            Check(!handle.IsInvalid && DeviceIoControl(handle, 0x000900A4, data, (uint)data.Length, IntPtr.Zero, 0, out returned, IntPtr.Zero), "Could not create bounded junction test fixture");
        }
    }

    public static int Main(string[] args)
    {
        string scratch = Path.GetFullPath(args[0]);
        Check(!Directory.Exists(scratch) && !File.Exists(scratch), "Scratch must be new");
        Directory.CreateDirectory(scratch);
        string root = Path.GetPathRoot(scratch);
        Reject(() => InstallDirectory.ValidateInstall(root), "volume root");
        Reject(() => InstallDirectory.ValidateInstall(root.Substring(0, 2) + "relative"), "drive-relative path");
        Reject(() => InstallDirectory.ValidateInstall(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), "user profile");
        Reject(() => InstallDirectory.ValidateInstall(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")), "Downloads");
        Reject(() => InstallDirectory.ValidateInstall(@"\\localhost\share\widget"), "network share");

        string shared = Path.Combine(scratch, "shared-downloads");
        Directory.CreateDirectory(shared);
        string sentinel = Path.Combine(shared, "important-user-file.txt");
        File.WriteAllText(sentinel, "KEEP");
        File.WriteAllText(Path.Combine(shared, "ResourcesWidget.exe"), "not-an-install");
        Reject(() => Installer.InstallFiles(shared), "populated unowned directory");
        Reject(() => InstallDirectory.RemoveApplicationFiles(shared), "uninstall without ownership marker");
        Check(File.ReadAllText(sentinel) == "KEEP", "Shared sentinel was changed");

        string owned = Path.Combine(scratch, "dedicated-widget");
        Installer.InstallFiles(owned);
        Check(InstallDirectory.Owned(owned), "Installation did not create ownership marker");
        Check(File.Exists(Path.Combine(owned, "ResourcesWidget.exe")) && File.Exists(Path.Combine(owned, "Setup.exe")), "Application files not installed");
        File.WriteAllText(Path.Combine(owned, "config.json"), "{\"custom\":true}");
        File.WriteAllText(Path.Combine(owned, "state.json"), "{\"saved\":true}");
        File.WriteAllText(Path.Combine(owned, "user-document.txt"), "KEEP");
        Directory.CreateDirectory(Path.Combine(owned, "network-log"));
        File.WriteAllText(Path.Combine(owned, "network-log", "saved-day.csv"), "KEEP LOG");
        Directory.CreateDirectory(Path.Combine(owned, "unrelated-folder"));
        File.WriteAllText(Path.Combine(owned, "unrelated-folder", "sentinel.txt"), "KEEP NESTED");
        Installer.InstallFiles(owned); // real upgrade file path
        Check(File.ReadAllText(Path.Combine(owned, "config.json")) == "{\"custom\":true}", "Upgrade overwrote config");
        Check(File.ReadAllText(Path.Combine(owned, "state.json")) == "{\"saved\":true}", "Upgrade overwrote state");
        InstallDirectory.RemoveApplicationFiles(owned); // real uninstall file path
        Check(!File.Exists(Path.Combine(owned, "ResourcesWidget.exe")) && !File.Exists(Path.Combine(owned, "Setup.exe")), "Known application binaries survived removal");
        foreach (string name in new[] { "config.json", "state.json", "user-document.txt", "network-log/saved-day.csv", "unrelated-folder/sentinel.txt" })
            Check(File.Exists(Path.Combine(owned, name)), "Removal lost user file: " + name);
        Installer.InstallFiles(owned); // preserved data can be safely reused
        Check(File.ReadAllText(Path.Combine(owned, "config.json")) == "{\"custom\":true}", "Reinstall lost config");

        string outside = Path.Combine(scratch, "junction-target");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "KEEP TARGET");
        string junction = Path.Combine(scratch, "junction");
        Junction(junction, outside);
        Reject(() => Installer.InstallFiles(junction), "junction target");
        Reject(() => Installer.InstallFiles(Path.Combine(junction, "nested")), "junction ancestor");
        Reject(() => InstallDirectory.RemoveApplicationFiles(junction), "junction removal");
        string logs = Path.Combine(owned, "network-log");
        // Keep the existing real log folder; a separate owned test directory hosts a junction log entry.
        string linkedLogInstall = Path.Combine(scratch, "linked-log-install");
        Installer.InstallFiles(linkedLogInstall);
        Junction(Path.Combine(linkedLogInstall, "network-log"), outside);
        Reject(() => Installer.InstallFiles(linkedLogInstall), "junction in managed child");
        Reject(() => InstallDirectory.RemoveApplicationFiles(linkedLogInstall), "junction child before removal");
        Check(File.ReadAllText(Path.Combine(outside, "sentinel.txt")) == "KEEP TARGET", "Junction target changed");
        Check(File.ReadAllText(sentinel) == "KEEP", "Shared sentinel changed after all tests");
        Console.WriteLine("PASS: dedicated-directory/ownership/root/shared/reparse guards; real file install/upgrade; removal preserves config/state/logs/unknown files; reinstall preserves data. No profile writes or process control.");
        return 0;
    }
}
