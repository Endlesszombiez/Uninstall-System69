using System.IO;
using System69.Core;

namespace System69.App.Services;

public sealed class CleanupService
{
    public CleanupPolicy Policy { get; } = CreatePolicy();

    private static CleanupPolicy CreatePolicy()
    {
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var protectedNames = new[] { "Common Files", "WindowsApps", "ModifiableWindowsApps", "Windows Defender", "Windows Defender Advanced Threat Protection", "Windows Mail", "Windows Media Player", "Windows NT", "Windows Photo Viewer", "Windows Portable Devices", "Windows Security", "Windows Sidebar", "WindowsPowerShell", "Internet Explorer", "Microsoft Shared", "MSBuild", "Reference Assemblies", "dotnet", "Windows Kits", "Microsoft.NET", "PowerShell" };
        var blocked = roots.SelectMany(root => protectedNames.Select(name => Path.Combine(root, name))).Append(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).Append(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        return new(roots, blocked, AppContext.BaseDirectory);
    }

    public string Clean(InstalledApp app, IReadOnlyList<InstalledApp> inventory, string approvedPath)
        => new GuardedCleanup(Policy, InventoryService.IsRegistered).Clean(app, inventory, approvedPath);
}
