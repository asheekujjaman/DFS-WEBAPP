using System.ComponentModel.DataAnnotations;
using WebTest.Services;

namespace WebTest.Models;

public class LoginViewModel
{
    [Required(ErrorMessage = "Choose a domain")] public string LocationId { get; set; } = "";
    [Required(ErrorMessage = "Username is required")] public string Username { get; set; } = "";
    [Required(ErrorMessage = "Password is required")][DataType(DataType.Password)] public string Password { get; set; } = "";
    public string? ReturnUrl { get; set; }
}

public class SetupViewModel
{
    public string? CompanyName { get; set; }
    [Required(ErrorMessage = "Name is required")] public string Name { get; set; } = "";
    [Required(ErrorMessage = "Domain is required")] public string Domain { get; set; } = "";
    [Required(ErrorMessage = "DFS / share path is required")] public string Path { get; set; } = "";
    public string? WebUrl { get; set; }
    [Required(ErrorMessage = "Admin username is required")] public string AdminUser { get; set; } = "";
    [Required(ErrorMessage = "Admin password is required")][DataType(DataType.Password)] public string AdminPassword { get; set; } = "";
}

public class LocationEditModel
{
    public string? Id { get; set; }
    [Required(ErrorMessage = "Name is required")] public string Name { get; set; } = "";
    [Required(ErrorMessage = "Domain is required")] public string Domain { get; set; } = "";
    [Required(ErrorMessage = "DFS / share path is required")] public string Path { get; set; } = "";
    public string? WebUrl { get; set; }
    public bool Enabled { get; set; } = true;
    public string DeleteMode { get; set; } = "Recycle";
    public string? AllowedGroups { get; set; }
}

public class BrowseViewModel
{
    public Location Loc { get; set; } = new();
    public string Path { get; set; } = "";
    public string? Error { get; set; }
    public bool CanWrite { get; set; }
    public bool CanDelete { get; set; }
    public bool ZipEnabled { get; set; } = true;
    public bool PreviewEnabled { get; set; } = true;
    public List<FileEntry> Entries { get; set; } = new();
}

public class HomeViewModel
{
    public List<Location> Locations { get; set; } = new();
    public string Banner { get; set; } = "";
}

public class DashboardViewModel
{
    public int ActiveUsers { get; set; }
    public List<(Location Loc, LocStatus? Status)> Locs { get; set; } = new();
    public int Failed24h { get; set; }
    public int UploadsToday { get; set; }
    public int DownloadsToday { get; set; }
    public List<AuditEvent> Recent { get; set; } = new();
}

public class SettingsViewModel
{
    public string? CompanyName { get; set; }
    public string? Banner { get; set; }
    public bool ZipEnabled { get; set; }
    [Range(1, 1000000)] public int ZipMaxMb { get; set; }
    [Range(0, 1000000)] public int MaxUploadMb { get; set; }
    public string? BlockedExtensions { get; set; }
    public bool PreviewEnabled { get; set; }
    [Range(1, 10000)] public int PreviewMaxMb { get; set; }
    public string? LibreOfficePath { get; set; }
    [Range(1, 3650)] public int RecycleDays { get; set; }
    [Range(1, 1440)] public int IdleMinutes { get; set; }
    [Range(1, 168)] public int AbsoluteHours { get; set; }
    public bool Maintenance { get; set; }
    public bool KickUsers { get; set; }
    public string? AllowedGroups { get; set; }
    [Range(1, 100)] public int MaxFailedLogins { get; set; }
    [Range(1, 1440)] public int LockoutMinutes { get; set; }
    [Range(7, 3650)] public int AuditDays { get; set; }
    public string? AdminUsers { get; set; }
}
