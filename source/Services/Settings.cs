namespace WebTest.Services;

/// <summary>Values from appsettings.json (section "App"). Changed by editing the file, not in the website.</summary>
public class AppSettings
{
    public string DataFile { get; set; } = "App_Data/config.json";
    public bool RequireHttps { get; set; } = true;
    /// <summary>Port of the HTTPS binding in IIS (used for the HTTP -> HTTPS redirect).</summary>
    public int HttpsPort { get; set; } = 443;
    /// <summary>Credentials = Windows keeps the session credentials (DFS works with no delegation setup).
    /// Network = no credentials kept at all (needs Kerberos delegation for remote file servers).</summary>
    public string LogonMode { get; set; } = "Credentials";
    public bool WindowsSso { get; set; } = false;
    public int StatusPort { get; set; } = 445;
}
