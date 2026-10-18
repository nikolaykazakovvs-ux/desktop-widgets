using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Web.Script.Serialization;
using ResourcesWidget;
using ResourcesWidgetSensor;
using ResourcesWidgetSetup;

static class LocalSensorContractTests
{
    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct SidAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct Request { public uint DesiredAccess; public IntPtr PrincipalSelfSid, ObjectTypeList; public uint ObjectTypeListLength; public IntPtr OptionalArguments; }
    [StructLayout(LayoutKind.Sequential)] struct Reply { public uint ResultListLength; public IntPtr GrantedAccessMask, SaclEvaluationResults, Error; }
    [DllImport("authz.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool AuthzInitializeResourceManager(uint flags, IntPtr accessCheck, IntPtr computeGroups, IntPtr freeGroups, string name, out IntPtr manager);
    [DllImport("authz.dll", SetLastError = true)] static extern bool AuthzInitializeContextFromSid(uint flags, IntPtr sid, IntPtr manager, IntPtr expiration, Luid id, IntPtr arguments, out IntPtr context);
    [DllImport("authz.dll", SetLastError = true)] static extern bool AuthzAddSidsToContext(IntPtr context, [In] SidAttributes[] sids, uint count, IntPtr restricted, uint restrictedCount, out IntPtr result);
    [DllImport("authz.dll", SetLastError = true)] static extern bool AuthzAccessCheck(uint flags, IntPtr context, ref Request request, IntPtr audit, byte[] security, IntPtr optional, uint optionalCount, ref Reply reply, IntPtr result);
    [DllImport("authz.dll")] static extern bool AuthzFreeContext(IntPtr context);
    [DllImport("authz.dll")] static extern bool AuthzFreeResourceManager(IntPtr manager);
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static IntPtr SidPointer(SecurityIdentifier sid) { var bytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(bytes, 0); var p = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, p, bytes.Length); return p; }
    static bool MayRead(IntPtr context, byte[] descriptor)
    {
        var request = new Request { DesiredAccess = 1 };
        IntPtr mask = Marshal.AllocHGlobal(4), error = Marshal.AllocHGlobal(4);
        try
        {
            var reply = new Reply { ResultListLength = 1, GrantedAccessMask = mask, Error = error };
            Check(AuthzAccessCheck(0, context, ref request, IntPtr.Zero, descriptor, IntPtr.Zero, 0, ref reply, IntPtr.Zero), "AuthzAccessCheck failed");
            return Marshal.ReadInt32(error) == 0 && (Marshal.ReadInt32(mask) & 1) != 0;
        }
        finally { Marshal.FreeHGlobal(mask); Marshal.FreeHGlobal(error); }
    }
    static void AclProof(string sid)
    {
        var descriptor = SensorSecurity.NewDirectorySecurity(sid);
        SensorSecurity.AssertDescriptor(descriptor, sid, true);
        IntPtr user = SidPointer(new SecurityIdentifier(sid)), network = SidPointer(SensorSecurity.NetworkSid), manager = IntPtr.Zero, local = IntPtr.Zero, remote = IntPtr.Zero;
        try
        {
            Check(AuthzInitializeResourceManager(1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, "ResourcesWidget tests", out manager), "Authz manager failed");
            Check(AuthzInitializeContextFromSid(2, user, manager, IntPtr.Zero, new Luid(), IntPtr.Zero, out local), "Authz local context failed");
            Check(AuthzAddSidsToContext(local, new[] { new SidAttributes { Sid = network, Attributes = 4 } }, 1, IntPtr.Zero, 0, out remote), "Authz network context failed");
            var bytes = descriptor.GetSecurityDescriptorBinaryForm();
            Check(MayRead(local, bytes), "Intended local account cannot read protected snapshot ACL");
            Check(!MayRead(remote, bytes), "NETWORK token can read protected snapshot ACL");
        }
        finally
        {
            if (remote != IntPtr.Zero) AuthzFreeContext(remote);
            if (local != IntPtr.Zero) AuthzFreeContext(local);
            if (manager != IntPtr.Zero) AuthzFreeResourceManager(manager);
            Marshal.FreeHGlobal(user); Marshal.FreeHGlobal(network);
        }
        descriptor.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.Write, AccessControlType.Allow));
        bool rejected = false;
        try { SensorSecurity.AssertDescriptor(descriptor, sid, true); } catch (IOException) { rejected = true; }
        Check(rejected, "User-writable sensor ACL accepted");
    }
    static string Json(string sid, DateTime at, object value, string status)
    {
        return new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "schema", 1 }, { "userSid", sid }, { "utc", at.ToString("o") }, { "temperatureC", value }, { "sensor", "CPU (Tctl/Tdie)" }, { "status", status } });
    }
    public static int Main(string[] args)
    {
        string sid = WindowsIdentity.GetCurrent().User.Value, label;
        DateTime now = DateTime.UtcNow;
        Check(LocalSensorSnapshot.Parse(Json(sid, now, 47.25, "ok"), sid, now, out label) == 47.25, "Valid fresh snapshot rejected");
        Check(!LocalSensorSnapshot.Parse(Json(sid, now.AddSeconds(-6), 47, "ok"), sid, now, out label).HasValue, "Stale snapshot accepted");
        Check(!LocalSensorSnapshot.Parse(Json(sid, now.AddSeconds(1), 47, "ok"), sid, now, out label).HasValue, "Future snapshot accepted");
        Check(!LocalSensorSnapshot.Parse(Json(sid, now, 47, "error"), sid, now, out label).HasValue, "Error snapshot kept old temperature");
        Check(!LocalSensorSnapshot.Parse(Json(sid, now, null, "no_data"), sid, now, out label).HasValue, "No-data snapshot accepted");
        Check(!LocalSensorSnapshot.Parse(Json(sid, now, 9999, "ok"), sid, now, out label).HasValue, "Out-of-range temperature accepted");
        Check(!LocalSensorSnapshot.Parse(Json(sid, now, "47", "ok"), sid, now, out label).HasValue, "String temperature accepted");
        Check(!LocalSensorSnapshot.Parse("{broken", sid, now, out label).HasValue, "Malformed JSON accepted");
        Check(!LocalSensorSnapshot.Parse(new string('x', 2049), sid, now, out label).HasValue, "Oversized snapshot accepted");
        Check(!LocalSensorSnapshot.Parse(Json("S-1-5-21-1-2-3-1001", now, 47, "ok"), sid, now, out label).HasValue, "Foreign account snapshot accepted");
        AclProof(sid);
        Check(Authenticode.VerifyPawnIo(args[0]), "Pinned PawnIO signature trust verification failed");
        Console.WriteLine("PASS: fresh/local snapshot, stale/future/malformed/status/size/range/account rejection; Windows Authz intended-local read allowed, NETWORK SID context denied; user-write ACL rejected; native PawnIO WinVerifyTrust validated. Authz is a synthetic access-token proof, not a live SMB test.");
        return 0;
    }
}
