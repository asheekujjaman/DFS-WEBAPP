using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace WebTest.Services;

public record LoginOutcome(LogonResult Result, string Display, bool IsAdmin);

public class LoginService
{
    private readonly IWinSecurity _win;
    private readonly SessionStore _store;
    private readonly LocationStore _locs;
    private readonly AuditLog _audit;
    private readonly LoginThrottle _throttle;
    private readonly AppSettings _app;

    public LoginService(IWinSecurity win, SessionStore store, LocationStore locs, AuditLog audit,
        LoginThrottle throttle, IOptions<AppSettings> app)
    { _win = win; _store = store; _locs = locs; _audit = audit; _throttle = throttle; _app = app.Value; }

    private static string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "?";

    /// <summary>The password is used only for this one Windows call and is never stored or logged.</summary>
    public async Task<LoginOutcome> LoginAsync(HttpContext ctx, string username, string password, string domain, bool requireAdmin = false)
    {
        var ip = Ip(ctx);
        if (_throttle.IsBlocked(ip, username))
        {
            _audit.Write("signin-blocked", username, ip, "", "Too many failed attempts", false);
            return new(LogonResult.Blocked, "", false);
        }

        var (res, token) = _win.Logon(username, password, domain, _app.LogonMode.Equals("Credentials", StringComparison.OrdinalIgnoreCase));
        if (res != LogonResult.Success || token == null)
        {
            _throttle.Fail(ip, username);
            _audit.Write("signin-fail", username, ip, "", res.ToString(), false);
            return new(res, "", false);
        }
        return await FinishAsync(ctx, token, username, requireAdmin);
    }

    /// <summary>Completes sign-in for a token (password sign-in or Windows single sign-on).</summary>
    public async Task<LoginOutcome> FinishAsync(HttpContext ctx, WinToken token, string typedName, bool requireAdmin)
    {
        var ip = Ip(ctx);
        var set = _locs.Settings;
        var info = _win.Inspect(token, _locs.AdminUsers());

        LogonResult? deny = null;
        if (requireAdmin && !info.IsAdmin) deny = LogonResult.NotAdmin;
        else if (set.Maintenance && !info.IsAdmin) deny = LogonResult.Maintenance;
        else if (!info.IsAdmin && !UiHelper.InGroups(info.Groups, set.AllowedGroups)) deny = LogonResult.NotAllowed;
        if (deny != null)
        {
            token.Dispose();
            _audit.Write("signin-denied", info.Name, ip, "", deny.ToString()!, false);
            return new(deny.Value, info.Name, false);
        }

        var sid = _store.Add(info, token, ip, ctx.Request.Headers.UserAgent.ToString());
        var claims = new List<Claim> { new(ClaimTypes.Name, info.Name), new("DisplayName", info.Name), new("sid", sid) };
        if (info.IsAdmin) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        var id = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(id),
            new AuthenticationProperties { IsPersistent = false });

        _throttle.Reset(typedName);
        _audit.Write("signin-ok", info.Name, ip, "", info.IsAdmin ? "admin" : "", true);
        return new(LogonResult.Success, info.Name, info.IsAdmin);
    }

    // one generic message for credential problems (no account enumeration); details go to the audit log
    public static string Message(LogonResult r) => r switch
    {
        LogonResult.Blocked => "Too many failed attempts. Please wait a few minutes and try again.",
        LogonResult.Maintenance => "The portal is in maintenance mode. Please try again later.",
        LogonResult.NotAllowed => "You are not allowed to use this portal.",
        LogonResult.NotAdmin => "This account is not a Domain Admin. Setup needs a domain administrator.",
        LogonResult.Error => "Cannot reach Active Directory for this domain. Try again later.",
        _ => "Sign-in failed. Check your username and password, or contact your administrator."
    };
}
