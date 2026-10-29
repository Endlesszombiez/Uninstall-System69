using System.IO;

namespace System69.Core;

public sealed class GuardedCleanup(CleanupPolicy policy, Func<InstalledApp, bool> isRegistered)
{
    public string Clean(InstalledApp app, IReadOnlyList<InstalledApp> inventory, string approvedPath)
    {
        if (isRegistered(app)) return "Cleanup skipped: Windows still reports the application installed.";
        var decision = policy.Evaluate(app, inventory);
        if (!decision.Allowed || decision.CanonicalPath is null) return "Cleanup blocked: " + decision.Reason;
        var original = decision.CanonicalPath;
        if (!CleanupPolicy.Same(original, approvedPath)) return "Cleanup blocked: installation path changed.";
        if (!Directory.Exists(original)) return "No installation folder remains.";
        InspectTree(original);
        // An atomic, same-volume rename detaches the approved app folder from its original path.
        var quarantine = Path.Combine(Path.GetDirectoryName(original)!, ".system69-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.Move(original, quarantine);
        try
        {
            var entries = InspectTree(quarantine);
            foreach (var entry in entries.OrderByDescending(x => x.Length))
            {
                VerifyAncestors(entry, quarantine);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("A link appeared during cleanup.");
                if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(entry, false);
                else File.Delete(entry);
            }
            VerifyAncestors(quarantine, quarantine);
            Directory.Delete(quarantine, false);
            return "Verified leftover folder removed.";
        }
        catch (Exception ex)
        {
            try
            {
                if (!Directory.Exists(original) && Directory.Exists(quarantine)) Directory.Move(quarantine, original);
                return "Cleanup incomplete; remaining files at " + (Directory.Exists(quarantine) ? quarantine : original) + ". " + ex.Message;
            }
            catch { return "Cleanup incomplete; remaining files at " + quarantine + ". " + ex.Message; }
        }
    }

    private static List<string> InspectTree(string root)
    {
        List<string> entries = [];
        Stack<(string Path, int Depth)> pending = new();
        pending.Push((root, 0));
        long bytes = 0;
        while (pending.TryPop(out var current))
        {
            VerifyAncestors(current.Path, root);
            if (current.Depth > 64) throw new IOException("Cleanup stopped: excessive directory depth.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(current.Path))
            {
                if (!CleanupPolicy.IsChild(Path.GetFullPath(entry), root)) throw new IOException("An entry escaped the approved folder.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Cleanup blocked: folder contains a symbolic link or junction.");
                if (entries.Count >= 50000) throw new IOException("Cleanup blocked: more than 50,000 entries.");
                entries.Add(entry);
                if ((attributes & FileAttributes.Directory) != 0) pending.Push((entry, current.Depth + 1));
                else bytes += new FileInfo(entry).Length;
                if (bytes > 20L * 1024 * 1024 * 1024) throw new IOException("Cleanup blocked: folder exceeds the 20 GB safety limit.");
            }
        }
        return entries;
    }

    private static void VerifyAncestors(string path, string root)
    {
        if (!CleanupPolicy.Same(path, root) && !CleanupPolicy.IsChild(path, root)) throw new IOException("Path escaped the cleanup boundary.");
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("A parent directory became a link.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("A link is not eligible for cleanup.");
    }
}
