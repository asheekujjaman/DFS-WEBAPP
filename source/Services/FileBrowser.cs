using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;

namespace WebTest.Services;

public record FileEntry(string Name, string RelPath, bool IsDir, long Size, DateTime Modified);

public class AccessReport
{
    public string Loc { get; set; } = "";
    public string LocName { get; set; } = "";
    public string Path { get; set; } = "";
    public string User { get; set; } = "";
    public List<string> Groups { get; set; } = new();
    public List<RuleInfo> Rules { get; set; } = new();
    public bool Read { get; set; }
    public bool Write { get; set; }
    public string? Error { get; set; }
}

public class RecycleMeta
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string OriginalRel { get; set; } = "";
    public bool IsDir { get; set; }
    public string DeletedBy { get; set; } = "";
    public DateTime DeletedAt { get; set; }
}

/// <summary>
/// Every file operation runs AS the signed-in user (impersonation), so NTFS permissions decide.
/// Works for local, UNC and DFS roots. Delete goes to a hidden ".recycle" folder (undo / restore).
/// </summary>
public class FileBrowser
{
    private static readonly char Sep = System.IO.Path.DirectorySeparatorChar;
    public const string BinName = ".recycle";
    private readonly IWinSecurity _win;
    private readonly ConcurrentDictionary<string, (string Session, string Loc, string Original, string Item, DateTime Expires)> _undo = new();

    public FileBrowser(IWinSecurity win) => _win = win;

    private T As<T>(UserSession s, Func<T> f) => _win.RunAs(s.Token, f);
    private void As(UserSession s, Action a) => _win.RunAs<int>(s.Token, () => { a(); return 0; });

    // ---------- paths ----------
    public static string Root(Location l) => System.IO.Path.GetFullPath(l.Path).TrimEnd(Sep);

    public string Normalize(string? rel)
        => string.Join('/', (rel ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));

    private static void EnsureInside(string root, string full)
    {
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(root + Sep, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Invalid path.");
    }

    private string Resolve(string root, string? rel)
    {
        var n = Normalize(rel);
        if (n.Split('/').Any(p => p.Equals(BinName, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("Invalid path.");
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, n.Replace('/', Sep)));
        EnsureInside(root, full);
        return full;
    }

    private static string Rel(string root, string full)
        => full.Length <= root.Length ? "" : full[(root.Length + 1)..].Replace('\\', '/');

    private static string Clean(string name)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }

    private static bool CanList(string dir)
    {
        try { using var e = Directory.EnumerateFileSystemEntries(dir).GetEnumerator(); e.MoveNext(); return true; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return true; }
    }

    private static bool Hidden(FileSystemInfo i)
        => (i.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0
           || i.Name.Equals(BinName, StringComparison.OrdinalIgnoreCase);

    // ---------- browse ----------
    public List<FileEntry> List(UserSession s, Location l, string rel)
    {
        var root = Root(l);
        var full = Resolve(root, rel);
        return As(s, () =>
        {
            var di = new DirectoryInfo(full);
            var list = new List<FileEntry>();
            foreach (var d in di.EnumerateDirectories())
            {
                if (Hidden(d) || !CanList(d.FullName)) continue;
                list.Add(new FileEntry(d.Name, Rel(root, d.FullName), true, 0, d.LastWriteTime));
            }
            foreach (var f in di.EnumerateFiles())
                if (!Hidden(f)) list.Add(new FileEntry(f.Name, Rel(root, f.FullName), false, f.Length, f.LastWriteTime));
            return list;
        });
    }

    public FileStream OpenRead(UserSession s, Location l, string rel)
    {
        var full = Resolve(Root(l), rel);
        return As(s, () => new FileStream(full, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    public (long Length, DateTime Modified) Info(UserSession s, Location l, string rel)
    {
        var full = Resolve(Root(l), rel);
        return As(s, () => { var fi = new FileInfo(full); if (!fi.Exists) throw new FileNotFoundException(); return (fi.Length, fi.LastWriteTimeUtc); });
    }

    // ---------- write ----------
    public FileStream OpenWrite(UserSession s, Location l, string dirRel, string relativePath)
    {
        var root = Root(l);
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != "." && p != "..").Select(Clean).Where(p => p.Length > 0).ToArray();
        if (parts.Length == 0) throw new ArgumentException("Invalid file name.");
        if (parts.Any(p => p.Equals(BinName, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Invalid file name.");

        var targetDir = Resolve(root, dirRel);
        foreach (var p in parts[..^1]) targetDir = System.IO.Path.Combine(targetDir, p);
        var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(targetDir, parts[^1]));
        EnsureInside(root, target);

        return As(s, () =>
        {
            Directory.CreateDirectory(targetDir);
            return new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        });
    }

    public void CreateFolder(UserSession s, Location l, string dirRel, string name)
    {
        var root = Root(l);
        name = Clean(name);
        if (name.Length == 0 || name == "." || name == ".." || name.Equals(BinName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid folder name.");
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(Resolve(root, dirRel), name));
        EnsureInside(root, full);
        As(s, () => { Directory.CreateDirectory(full); });
    }

    // ---------- delete / recycle / undo ----------
    public void DeletePermanent(UserSession s, Location l, string rel)
    {
        var root = Root(l);
        var full = Resolve(root, rel);
        if (full.Equals(root, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Cannot delete the root.");
        As(s, () => { if (Directory.Exists(full)) Directory.Delete(full, true); else File.Delete(full); });
    }

    /// <summary>Moves an item into "&lt;location&gt;\.recycle". Returns an undo id.</summary>
    public string Recycle(UserSession s, Location l, string rel)
    {
        var root = Root(l);
        var full = Resolve(root, rel);
        if (full.Equals(root, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Cannot delete the root.");
        var bin = System.IO.Path.Combine(root, BinName);
        var id = Guid.NewGuid().ToString("N")[..12];
        var name = System.IO.Path.GetFileName(full);
        var dest = System.IO.Path.Combine(bin, id + "_" + name);
        var metaFile = System.IO.Path.Combine(bin, id + ".json");

        As(s, () =>
        {
            bool isDir = Directory.Exists(full);
            if (!isDir && !File.Exists(full)) throw new FileNotFoundException();
            if (!Directory.Exists(bin)) Directory.CreateDirectory(bin);
            File.WriteAllText(metaFile, JsonSerializer.Serialize(new RecycleMeta
            { Id = id, Name = name, OriginalRel = Normalize(rel), IsDir = isDir, DeletedBy = s.Display, DeletedAt = DateTime.UtcNow }));
            try
            {
                if (isDir) Directory.Move(full, dest); else File.Move(full, dest);
            }
            catch { try { File.Delete(metaFile); } catch { } throw; }
        });

        foreach (var k in _undo.Where(x => x.Value.Expires < DateTime.UtcNow).Select(x => x.Key).ToList()) _undo.TryRemove(k, out _);
        _undo[id] = (s.Id, l.Id, full, dest, DateTime.UtcNow.AddMinutes(10));
        return id;
    }

    public void Undo(UserSession s, Location l, string id)
    {
        if (!_undo.TryGetValue(id, out var u) || u.Session != s.Id || u.Loc != l.Id || u.Expires < DateTime.UtcNow)
            throw new InvalidOperationException("Undo is no longer available. Ask an administrator to restore it.");
        As(s, () =>
        {
            if (Directory.Exists(u.Original) || File.Exists(u.Original))
                throw new IOException("An item with the same name already exists.");
            if (Directory.Exists(u.Item)) Directory.Move(u.Item, u.Original); else File.Move(u.Item, u.Original);
        });
        _undo.TryRemove(id, out _);
    }

    public List<RecycleMeta> ListRecycle(UserSession s, Location l)
    {
        var bin = System.IO.Path.Combine(Root(l), BinName);
        return As(s, () =>
        {
            var list = new List<RecycleMeta>();
            if (!Directory.Exists(bin)) return list;
            foreach (var f in Directory.EnumerateFiles(bin, "*.json"))
            {
                try { var m = JsonSerializer.Deserialize<RecycleMeta>(File.ReadAllText(f)); if (m != null) list.Add(m); } catch { }
            }
            return list.OrderByDescending(x => x.DeletedAt).ToList();
        });
    }

    public void Restore(UserSession s, Location l, string id)
    {
        var root = Root(l);
        var bin = System.IO.Path.Combine(root, BinName);
        As(s, () =>
        {
            var metaFile = System.IO.Path.Combine(bin, System.IO.Path.GetFileName(id) + ".json");
            var m = JsonSerializer.Deserialize<RecycleMeta>(File.ReadAllText(metaFile)) ?? throw new FileNotFoundException();
            var item = System.IO.Path.Combine(bin, m.Id + "_" + m.Name);
            var target = Resolve(root, m.OriginalRel);
            if (Directory.Exists(target) || File.Exists(target)) throw new IOException("An item with the same name already exists at the original location.");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            if (m.IsDir) Directory.Move(item, target); else File.Move(item, target);
            File.Delete(metaFile);
        });
    }

    /// <summary>Runs as the app identity (background job): removes items older than the retention.</summary>
    public int PurgeRecycle(Location l, int days)
    {
        int n = 0;
        var bin = System.IO.Path.Combine(Root(l), BinName);
        if (!Directory.Exists(bin)) return 0;
        foreach (var f in Directory.EnumerateFiles(bin, "*.json").ToList())
        {
            try
            {
                var m = JsonSerializer.Deserialize<RecycleMeta>(File.ReadAllText(f));
                if (m == null) continue;
                var item = System.IO.Path.Combine(bin, m.Id + "_" + m.Name);
                bool gone = !Directory.Exists(item) && !File.Exists(item);
                if (!gone && m.DeletedAt > DateTime.UtcNow.AddDays(-Math.Max(1, days))) continue;
                if (Directory.Exists(item)) Directory.Delete(item, true); else if (File.Exists(item)) File.Delete(item);
                File.Delete(f);
                n++;
            }
            catch { }
        }
        return n;
    }

    // ---------- sizes ----------
    public (int Files, int Folders, bool Truncated) Count(UserSession s, Location l, string rel, int max = 5000)
    {
        var full = Resolve(Root(l), rel);
        return As(s, () =>
        {
            int files = 0, folders = 0;
            var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var e in new DirectoryInfo(full).EnumerateFileSystemInfos("*", opt))
            {
                if (e is DirectoryInfo) folders++; else files++;
                if (files + folders >= max) return (files, folders, true);
            }
            return (files, folders, false);
        });
    }

    public (long Bytes, bool Exceeded) FolderSize(UserSession s, Location l, string rel, long limit)
    {
        var full = Resolve(Root(l), rel);
        return As(s, () =>
        {
            long total = 0;
            var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var f in new DirectoryInfo(full).EnumerateFiles("*", opt))
            {
                total += f.Length;
                if (total > limit) return (total, true);
            }
            return (total, false);
        });
    }

    public FileStream ZipFolder(UserSession s, Location l, string rel)
    {
        var full = Resolve(Root(l), rel);
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fs-" + Guid.NewGuid().ToString("N") + ".zip");
        var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
        try
        {
            As(s, () =>
            {
                new DirectoryInfo(full).EnumerateFileSystemInfos().FirstOrDefault();   // throws if no access
                var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden };
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var path in Directory.EnumerateFileSystemEntries(full, "*", opt))
                {
                    var name = System.IO.Path.GetRelativePath(full, path).Replace('\\', '/');
                    if (name.Split('/').Any(p => p.Equals(BinName, StringComparison.OrdinalIgnoreCase))) continue;
                    if (Directory.Exists(path)) { zip.CreateEntry(name + "/"); continue; }
                    try
                    {
                        using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                        using var dst = entry.Open();
                        src.CopyTo(dst);
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }
            });
            fs.Position = 0;
            return fs;
        }
        catch { fs.Dispose(); throw; }
    }

    // ---------- permissions ----------
    public (bool Read, bool Write) GetAccess(UserSession s, Location l, string rel)
    {
        string full;
        try { full = Resolve(Root(l), rel); } catch { return (false, false); }
        return _win.Access(s.Token, full);
    }

    public AccessReport Explain(UserSession s, Location l, string rel)
    {
        var full = Resolve(Root(l), rel);
        var rep = new AccessReport { Loc = l.Id, LocName = l.Name, Path = Normalize(rel) };
        (rep.Read, rep.Write) = GetAccess(s, l, rel);
        try { (rep.User, rep.Groups, rep.Rules) = _win.Explain(s.Token, full); }
        catch (Exception ex) { rep.Error = ex is UnauthorizedAccessException ? "Cannot read the folder permissions." : "Permissions are not available for this folder."; }
        return rep;
    }
}
