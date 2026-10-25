using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Server.IIS;
using WebTest.Services;

var builder = WebApplication.CreateBuilder(args);
var appCfg = builder.Configuration.GetSection("App").Get<AppSettings>() ?? new AppSettings();

builder.Services.AddControllersWithViews();

// cookie / anti-forgery keys: kept in App_Data\keys and encrypted with Windows DPAPI (machine scope)
var keys = builder.Services.AddDataProtection().SetApplicationName("FileSharePortal")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys")));
if (OperatingSystem.IsWindows()) keys.ProtectKeysWithDpapi(protectToLocalMachine: true);
builder.Services.Configure<AppSettings>(builder.Configuration.GetSection("App"));
builder.Services.AddSingleton<IWinSecurity>(_ => OperatingSystem.IsWindows() ? new WinSecurity() : new FakeWinSecurity());
builder.Services.AddSingleton<LocationStore>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<FileBrowser>();
builder.Services.AddSingleton<StatusService>();
builder.Services.AddSingleton<PreviewService>();
builder.Services.AddScoped<LoginService>();
builder.Services.AddHostedService<BackgroundJobs>();

// big uploads (IIS in-process has its own 30 MB default limit)
builder.WebHost.ConfigureKestrel(o => { o.Limits.MaxRequestBodySize = null; o.AddServerHeader = false; });
builder.Services.Configure<IISServerOptions>(o => o.MaxRequestBodySize = null);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = long.MaxValue);

builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "RequestVerificationToken";
    o.Cookie.Name = "FS.AF";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
if (appCfg.RequireHttps) builder.Services.AddHttpsRedirection(o => o.HttpsPort = appCfg.HttpsPort);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Account/Login";
        o.LogoutPath = "/Account/Logout";
        o.AccessDeniedPath = "/Home/Index";
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = false;
        o.Cookie.Name = "FS.Auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.Events.OnValidatePrincipal = async ctx =>
        {
            var store = ctx.HttpContext.RequestServices.GetRequiredService<SessionStore>();
            var sid = ctx.Principal?.FindFirst("sid")?.Value;
            var touch = !ctx.Request.Path.StartsWithSegments("/Home/Status");   // background polling must not keep a session alive
            if (sid == null || store.Get(sid, touch) == null)
            {
                ctx.RejectPrincipal();
                await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseExceptionHandler("/Home/Error");
if (appCfg.RequireHttps) { app.UseHsts(); app.UseHttpsRedirection(); }

// security headers (no inline scripts or styles are used anywhere, so a strict policy is possible)
app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        var h = ctx.Response.Headers;
        h["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; " +
                                        "frame-src 'self'; object-src 'self'; frame-ancestors 'self'; base-uri 'self'; form-action 'self'";
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "SAMEORIGIN";
        h["Referrer-Policy"] = "same-origin";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        var p = ctx.Request.Path;
        if (!p.StartsWithSegments("/css") && !p.StartsWithSegments("/js") && !p.StartsWithSegments("/img"))
            h["Cache-Control"] = "no-store";
        return Task.CompletedTask;
    });
    await next();
});

app.UseStaticFiles();

// first run: no domain configured yet -> setup page
app.Use(async (ctx, next) =>
{
    var locs = ctx.RequestServices.GetRequiredService<LocationStore>();
    if (!locs.IsConfigured && !ctx.Request.Path.StartsWithSegments("/Setup")
        && !ctx.Request.Path.StartsWithSegments("/Home/Error"))
    {
        ctx.Response.Redirect("/Setup");
        return;
    }
    await next();
});

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.Run();
