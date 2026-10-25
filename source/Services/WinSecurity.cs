using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace WebTest.Services;

public enum LogonResult { Success, InvalidCredentials, Locked, Disabled, Expired, NotAllowed, NotAdmin, Blocked, Maintenance, Error }

public sealed class WinToken : IDisposable
{
    public SafeAccessTokenHandle? Handle { get; init; }
    public string? FakeUser { get; init; }          // test mode only (non-Windows)
    public void Dispose() => Handle?.Dispose();
}

public record UserInfo(string Name, bool IsAdmin, List<string> Groups);
public record DfsTarget(string Server, string Share);
public record RuleInfo(string Who, string Type, string Rights, bool Inherited, bool Applies);

/// <summary>Every Windows-only API lives behind this interface (so the site can be tested on Linux).</summary>
public interface IWinSecurity
{
    (LogonResult Result, WinToken? Token) Logon(string input, string password, string defaultDomain, bool keepCredentials);
    WinToken? FromIdentity(WindowsIdentity id);
    UserInfo Inspect(WinToken token, IEnumerable<string> adminUsers);
    T RunAs<T>(WinToken? token, Func<T> f);
    (bool Read, bool Write) Access(WinToken? token, string dir);
    (string User, List<string> Groups, List<RuleInfo> Rules) Explain(WinToken? token, string dir);
    List<DfsTarget> DfsTargets(string path);
}

public class WinSecurity : IWinSecurity
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(string user, string? domain, string password,
        int logonType, int provider, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs,
        int impersonationLevel, int tokenType, out SafeAccessTokenHandle newToken);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetDfsGetInfo(string entryPath, string? server, string? share, int level, out IntPtr buffer);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DFS_INFO_3
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string EntryPath;
        [MarshalAs(UnmanagedType.LPWStr)] public string Comment;
        public uint State;
        public uint NumberOfStorages;
        public IntPtr Storage;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DFS_STORAGE_INFO
    {
        public uint State;
        [MarshalAs(UnmanagedType.LPWStr)] public string ServerName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ShareName;
    }

    private const int LOGON_NETWORK = 3;              // no credentials kept
    private const int LOGON_NETWORK_CLEARTEXT = 8;    // Windows keeps credentials for the session (DFS hop works)

    public (LogonResult Result, WinToken? Token) Logon(string input, string password, string defaultDomain, bool keepCredentials)
    {
        input = input.Trim();
        string user = input;
        string? domain = null;
        if (input.Contains('\\')) { var p = input.Split('\\', 2); domain = p[0]; user = p[1]; }
        else if (!input.Contains('@')) user = $"{input}@{defaultDomain}";

        if (LogonUser(user, domain, password, keepCredentials ? LOGON_NETWORK_CLEARTEXT : LOGON_NETWORK, 0, out var h))
            return (LogonResult.Success, new WinToken { Handle = h });

        h?.Dispose();
        return (Marshal.GetLastWin32Error() switch
        {
            1326 => LogonResult.InvalidCredentials,
            1331 => LogonResult.Disabled,
            1909 => LogonResult.Locked,
            1330 or 1907 => LogonResult.Expired,
            1327 or 1328 or 1329 or 1385 or 1386 => LogonResult.NotAllowed,
            _ => LogonResult.Error
        }, null);
    }

    /// <summary>Single sign-on: copy the token IIS gave us (it is disposed after the request).</summary>
    public WinToken? FromIdentity(WindowsIdentity id)
    {
        // 3 = SecurityDelegation (lets Kerberos delegation work when configured), 2 = TokenImpersonation
        if (DuplicateTokenEx(id.AccessToken.DangerousGetHandle(), 0xF01FF, IntPtr.Zero, 3, 2, out var h))
            return new WinToken { Handle = h };
        return null;
    }

    public T RunAs<T>(WinToken? token, Func<T> f) => WindowsIdentity.RunImpersonated(token!.Handle!, f);

    private static string NameOf(SecurityIdentifier sid)
    {
        try { return sid.Translate(typeof(NTAccount)).Value; } catch { return sid.Value; }
    }

    public UserInfo Inspect(WinToken token, IEnumerable<string> adminUsers)
        => RunAs(token, () =>
        {
            var id = WindowsIdentity.GetCurrent();
            var name = id.Name;
            var sids = id.Groups!.Cast<SecurityIdentifier>().ToList();
            bool admin = sids.Any(g =>
            {
                var v = g.Value;
                return v == "S-1-5-32-544" ||
                       (v.StartsWith("S-1-5-21-") && (v.EndsWith("-512") || v.EndsWith("-519")));
            });
            if (!admin)
            {
                var sam = name.Contains('\\') ? name.Split('\\')[1] : name;
                admin = adminUsers.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                                            a.Equals(sam, StringComparison.OrdinalIgnoreCase));
            }
            return new UserInfo(name, admin, sids.Select(NameOf).ToList());
        });

    private static (HashSet<SecurityIdentifier>, DirectoryInfo) Ctx(string dir)
    {
        var id = WindowsIdentity.GetCurrent();
        var sids = new HashSet<SecurityIdentifier>(id.Groups!.Cast<SecurityIdentifier>()) { id.User! };
        return (sids, new DirectoryInfo(dir));
    }

    public (bool Read, bool Write) Access(WinToken? token, string dir)
        => RunAs(token, () =>
        {
            try
            {
                var (sids, di) = Ctx(dir);
                FileSystemRights allow = 0, deny = 0;
                foreach (FileSystemAccessRule r in di.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)))
                {
                    if ((r.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
                    if (!sids.Contains((SecurityIdentifier)r.IdentityReference)) continue;
                    if (r.AccessControlType == AccessControlType.Allow) allow |= r.FileSystemRights; else deny |= r.FileSystemRights;
                }
                var eff = allow & ~deny;
                return (eff.HasFlag(FileSystemRights.ListDirectory),
                        eff.HasFlag(FileSystemRights.WriteData) && eff.HasFlag(FileSystemRights.AppendData));
            }
            catch { return (true, false); }
        });

    public (string User, List<string> Groups, List<RuleInfo> Rules) Explain(WinToken? token, string dir)
        => RunAs(token, () =>
        {
            var (sids, di) = Ctx(dir);
            var rules = new List<RuleInfo>();
            foreach (FileSystemAccessRule r in di.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                var sid = (SecurityIdentifier)r.IdentityReference;
                rules.Add(new RuleInfo(NameOf(sid), r.AccessControlType.ToString(), r.FileSystemRights.ToString(),
                    r.IsInherited, sids.Contains(sid) && (r.PropagationFlags & PropagationFlags.InheritOnly) == 0));
            }
            return (WindowsIdentity.GetCurrent().Name, sids.Select(NameOf).OrderBy(x => x).ToList(), rules);
        });

    /// <summary>Real DFS targets of a namespace path (so "Down" means the file server is really down).</summary>
    public List<DfsTarget> DfsTargets(string path)
    {
        var list = new List<DfsTarget>();
        if (!path.StartsWith("\\\\")) return list;
        var p = path.TrimEnd('\\');
        while (p.Length > 2 && p.IndexOf('\\', 2) > 0)
        {
            try
            {
                if (NetDfsGetInfo(p, null, null, 3, out var buf) == 0)
                {
                    try
                    {
                        var info = Marshal.PtrToStructure<DFS_INFO_3>(buf);
                        var size = Marshal.SizeOf<DFS_STORAGE_INFO>();
                        for (int i = 0; i < info.NumberOfStorages; i++)
                        {
                            var s = Marshal.PtrToStructure<DFS_STORAGE_INFO>(info.Storage + i * size);
                            if (!string.IsNullOrEmpty(s.ServerName)) list.Add(new DfsTarget(s.ServerName, s.ShareName ?? ""));
                        }
                    }
                    finally { NetApiBufferFree(buf); }
                    if (list.Count > 0) return list;
                }
            }
            catch { }
            var i2 = p.LastIndexOf('\\');
            if (i2 <= 2) break;
            p = p[..i2];
        }
        return list;
    }
}

/// <summary>Used ONLY when running on non-Windows (automated tests). Password "pw" signs in.</summary>
public class FakeWinSecurity : IWinSecurity
{
    public (LogonResult Result, WinToken? Token) Logon(string input, string password, string defaultDomain, bool keepCredentials)
    {
        var u = input.Contains('\\') ? input.Split('\\')[1] : input.Split('@')[0];
        if (password == "pw") return (LogonResult.Success, new WinToken { FakeUser = u });
        return (LogonResult.InvalidCredentials, null);
    }
    public WinToken? FromIdentity(WindowsIdentity id) => null;
    public UserInfo Inspect(WinToken t, IEnumerable<string> adminUsers)
    {
        var u = t.FakeUser ?? "user";
        var groups = new List<string> { "TEST\\Domain Users" };
        if (u == "u1") groups.Add("TEST\\Sales");
        if (u == "u3") groups.Add("TEST\\Blocked");
        return new UserInfo("TEST\\" + u, u.StartsWith("admin") || adminUsers.Contains("TEST\\" + u), groups);
    }
    public T RunAs<T>(WinToken? token, Func<T> f) => f();
    public (bool Read, bool Write) Access(WinToken? token, string dir) => (true, !dir.Contains("HR"));
    public (string User, List<string> Groups, List<RuleInfo> Rules) Explain(WinToken? token, string dir)
        => ("TEST\\fake", new List<string> { "TEST\\Domain Users" },
            new List<RuleInfo> { new("TEST\\Domain Users", "Allow", "Modify", true, true) });
    public List<DfsTarget> DfsTargets(string path)
        => path.Contains("dfs-test") ? new() { new("127.0.0.1", "d"), new("nohost.invalid", "d") }
         : path.Contains("dfs-down") ? new() { new("nohost.invalid", "d"), new("nohost2.invalid", "d") }
         : new();
}
