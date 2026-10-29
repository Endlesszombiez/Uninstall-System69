using System.Text.RegularExpressions;

namespace System69.Core;

/// <summary>Fail-closed eligibility policy. No directory is inferred from an executable path.</summary>
public sealed class CleanupPolicy(IEnumerable<string> applicationRoots, IEnumerable<string> protectedRoots, string ownDirectory)
{
    private readonly string[] roots = applicationRoots.Select(Canonical).ToArray();
    private readonly string[] protectedPaths = protectedRoots.Select(Canonical).ToArray();
    private readonly string self = Canonical(ownDirectory);

    public CleanupDecision Evaluate(InstalledApp app, IReadOnlyList<InstalledApp> inventory)
    {
        if (app.Protected || app.Source != PackageSource.Windows)
            return Deny("Cleanup requires an unprotected Windows registry installation.");
        if (string.IsNullOrWhiteSpace(app.InstallLocation)) return Deny("No registered installation folder. Nothing will be guessed.");
        var raw = app.InstallLocation.Trim();
        if (!Regex.IsMatch(raw, @"^[A-Za-z]:[\\/]") || raw.StartsWith(@"\\") || raw[2..].Contains(':') || raw.Split(['\\', '/']).Any(x => x is "." or ".."))
            return Deny("Network, device, relative, and traversal paths are blocked.");
        string path;
        try { path = Canonical(raw); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return Deny("Invalid installation path."); }
        var root = roots.FirstOrDefault(x => IsChild(path, x));
        if (root is null) return Deny("Folder is outside the approved application roots.");
        var relative = Path.GetRelativePath(root, path);
        if (relative.Split(Path.DirectorySeparatorChar).Length > 2) return Deny("Folder is too deeply nested to establish safe ownership.");
        if (protectedPaths.Any(x => Same(path, x) || IsChild(path, x) || IsChild(x, path)))
            return Deny("Folder overlaps a protected Windows or shared directory.");
        if (Same(path, self) || IsChild(self, path) || IsChild(path, self)) return Deny("System69 cannot clean its own installation.");
        var folderName = Normalize(Path.GetFileName(path));
        var appName = Normalize(app.Name);
        if (folderName.Length < 4 || folderName != appName)
            return Deny("Folder name does not exactly match the registered application name. Manual review is required.");
        foreach (var other in inventory.Where(x => x.Id != app.Id && !string.IsNullOrWhiteSpace(x.InstallLocation)))
        {
            string otherPath;
            try { otherPath = Canonical(other.InstallLocation!); } catch { return Deny("Another registered folder could not be validated."); }
            if (Same(path, otherPath) || IsChild(otherPath, path)) return Deny("Another installed application shares this folder or a subfolder.");
        }
        try
        {
            for (var node = new DirectoryInfo(path); node is not null; node = node.Parent)
                if (node.Exists && (node.Attributes & FileAttributes.ReparsePoint) != 0) return Deny("A symbolic link, junction, or mount point is present.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Deny("Folder attributes could not be verified."); }
        return new(true, "Eligible after successful uninstall and a second safety check.", path);
    }

    public static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    public static bool IsChild(string path, string parent) => path.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]", "");
    private static CleanupDecision Deny(string reason) => new(false, reason);
}
