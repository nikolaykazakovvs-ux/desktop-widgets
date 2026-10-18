using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace ResourcesWidgetSetup
{
    static class Authenticode
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct FileInfo
        {
            public uint Size;
            [MarshalAs(UnmanagedType.LPWStr)] public string Path;
            public IntPtr File, KnownSubject;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TrustData
        {
            public uint Size;
            public IntPtr PolicyCallback, SipClient;
            public uint UiChoice, RevocationChecks, UnionChoice;
            public IntPtr File;
            public uint StateAction;
            public IntPtr StateData, UrlReference;
            public uint ProviderFlags, UiContext;
        }
        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int WinVerifyTrust(IntPtr window, [In] ref Guid action, [In] ref TrustData data);
        public static bool VerifyPawnIo(string path)
        {
            var info = new FileInfo { Size = (uint)Marshal.SizeOf(typeof(FileInfo)), Path = path };
            IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileInfo)));
            try
            {
                Marshal.StructureToPtr(info, pointer, false);
                var data = new TrustData { Size = (uint)Marshal.SizeOf(typeof(TrustData)), UiChoice = 2, UnionChoice = 1, File = pointer };
                var policy = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
                if (WinVerifyTrust(new IntPtr(-1), ref policy, ref data) != 0) return false;
                using (var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)))
                    return string.Equals(cert.GetNameInfo(X509NameType.SimpleName, false), "namazso.eu", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
            finally { Marshal.DestroyStructure(pointer, typeof(FileInfo)); Marshal.FreeHGlobal(pointer); }
        }
    }
}
