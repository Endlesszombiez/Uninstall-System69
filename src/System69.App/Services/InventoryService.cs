using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using System69.Core;

namespace System69.App.Services;

public sealed class InventoryService
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public List<string> Notices { get; } = [];

    public async Task<List<InstalledApp>> ScanAsync(bool includeBrew, Action<string> progress, CancellationToken token)
    {
        Notices.Clear();
        progress("Reading Windows installations…");
        var apps = await Task.Run(ReadWindowsInventory, token);
        progress("Reading package managers and Microsoft Store…");
        var scans = await Task.WhenAll(ReadWinget(token), ReadChocolatey(token), ReadStore(token), includeBrew ? ReadBrew(token) : Task.FromResult(new List<InstalledApp>()));
        foreach (var scan in scans) apps.AddRange(scan);
        return apps.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Source).ToList();
    }

    public static List<InstalledApp> ReadWindowsInventory()
    {
        List<InstalledApp> apps = [];
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(UninstallPath);
            if (uninstall is null) continue;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key is null || key.GetValue("SystemComponent") is int system && system == 1 || key.GetValue("ParentKeyName") is not null) continue;
                    var displayName = key.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName)) continue;
                    var command = key.GetValue("UninstallString") as string;
                    var quiet = key.GetValue("QuietUninstallString") as string;
                    if (string.IsNullOrWhiteSpace(command) && string.IsNullOrWhiteSpace(quiet)) continue;
                    if (apps.Any(x => x.Name == displayName && x.UninstallCommand == command)) continue;
                    var id = $"{hive}|{view}|{name}";
                    var installLocation = key.GetValue("InstallLocation") as string;
                    var isSelf = displayName.Equals("System69 Uninstaller", StringComparison.OrdinalIgnoreCase);
                    try { isSelf |= !string.IsNullOrWhiteSpace(installLocation) && (CleanupPolicy.Same(CleanupPolicy.Canonical(installLocation), CleanupPolicy.Canonical(AppContext.BaseDirectory)) || CleanupPolicy.IsChild(CleanupPolicy.Canonical(AppContext.BaseDirectory), CleanupPolicy.Canonical(installLocation))); } catch { isSelf = true; }
                    var size = key.GetValue("EstimatedSize") is int kb ? Math.Max(0L, (long)kb) * 1024 : 0;
                    apps.Add(new(id, displayName, key.GetValue("DisplayVersion") as string ?? "", key.GetValue("Publisher") as string ?? "Unknown publisher", PackageSource.Windows,
                        command, quiet, installLocation, size, key.GetValue("WindowsInstaller") is int msi && msi == 1,
                        isSelf || key.GetValue("NoRemove") is int noRemove && noRemove == 1 || key.GetValue("ReleaseType") is string release && (release.Contains("Update", StringComparison.OrdinalIgnoreCase) || release.Contains("Hotfix", StringComparison.OrdinalIgnoreCase))));
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { /* An inaccessible entry is not actionable. */ }
            }
        }
        return apps;
    }

    public static bool IsRegistered(InstalledApp app)
    {
        if (app.Source != PackageSource.Windows) return true;
        var parts = app.Id.Split('|', 3);
        if (parts.Length != 3 || !Enum.TryParse<RegistryHive>(parts[0], out var hive) || !Enum.TryParse<RegistryView>(parts[1], out var view)) return true;
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var key = root.OpenSubKey(UninstallPath + @"\" + parts[2]);
        return key is not null;
    }

    private async Task<List<InstalledApp>> ReadWinget(CancellationToken token)
    {
        var temp = Path.Combine(Path.GetTempPath(), "system69-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var result = await ProcessRunner.RunAsync("winget.exe", ["export", "--output", temp, "--include-versions", "--disable-interactivity"], token, TimeSpan.FromSeconds(90));
            if (result.ExitCode != 0 || !File.Exists(temp)) { Notice("WinGet inventory unavailable. Check winget sources and agreements in a terminal."); return []; }
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(temp, token));
            List<InstalledApp> apps = [];
            foreach (var source in doc.RootElement.GetProperty("Sources").EnumerateArray())
            foreach (var package in source.GetProperty("Packages").EnumerateArray())
            {
                var id = package.GetProperty("PackageIdentifier").GetString()!;
                apps.Add(new(id, id, package.TryGetProperty("Version", out var version) ? version.GetString() ?? "" : "", "WinGet package", PackageSource.Winget));
            }
            return apps;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Notice("WinGet: " + ex.Message); return []; }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private async Task<List<InstalledApp>> ReadChocolatey(CancellationToken token)
    {
        try
        {
            var result = await ProcessRunner.RunAsync("choco.exe", ["list", "--limit-output"], token, TimeSpan.FromSeconds(45));
            if (result.ExitCode != 0) { Notice("Chocolatey inventory failed. Chocolatey 2.x is required."); return []; }
            return result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('|')).Where(x => x.Length == 2)
                .Select(x => new InstalledApp(x[0], x[0], x[1], "Chocolatey package", PackageSource.Chocolatey)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Notice("Chocolatey: " + ex.Message); return []; }
    }

    private async Task<List<InstalledApp>> ReadStore(CancellationToken token)
    {
        const string script = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $friendly = @{}; try { $shell = New-Object -ComObject Shell.Application; foreach ($item in $shell.Namespace('shell:AppsFolder').Items()) { $id = $item.ExtendedProperty('System.AppUserModel.ID'); if ($id) { $friendly[$id.Split('!')[0]] = $item.Name } } } catch {}; @(Get-AppxPackage | Where-Object { -not $_.NonRemovable -and -not $_.IsFramework -and -not $_.IsResourcePackage } | Select-Object Name,Version,Publisher,PackageFullName,@{Name='DisplayName';Expression={if ($friendly.ContainsKey($_.PackageFamilyName)) { $friendly[$_.PackageFamilyName] } else { $_.Name }}}) | ConvertTo-Json -Compress";
        try
        {
            var result = await ProcessRunner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], token, TimeSpan.FromSeconds(45));
            if (result.ExitCode != 0) { Notice("Microsoft Store inventory unavailable."); return []; }
            if (string.IsNullOrWhiteSpace(result.Output)) return [];
            using var doc = JsonDocument.Parse(result.Output.TrimStart('\uFEFF'));
            var entries = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToArray() : [doc.RootElement];
            return entries.Select(x => new InstalledApp(x.GetProperty("PackageFullName").GetString()!, x.GetProperty("DisplayName").GetString() ?? x.GetProperty("Name").GetString()!, x.GetProperty("Version").GetString() ?? "", x.GetProperty("Publisher").GetString() ?? "Microsoft Store", PackageSource.Store)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Notice("Microsoft Store: " + ex.Message); return []; }
    }

    private async Task<List<InstalledApp>> ReadBrew(CancellationToken token)
    {
        try
        {
            var result = await ProcessRunner.RunAsync("wsl.exe", ["--exec", "/home/linuxbrew/.linuxbrew/bin/brew", "list", "--formula", "--versions"], token, TimeSpan.FromSeconds(45));
            if (result.ExitCode != 0) { Notice("Homebrew requires the default WSL distro and /home/linuxbrew/.linuxbrew/bin/brew."); return []; }
            return result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(x => x.Length >= 2)
                .Select(x => new InstalledApp(x[0], x[0], string.Join(", ", x.Skip(1)), "Default WSL distribution", PackageSource.Homebrew)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Notice("Homebrew: " + ex.Message); return []; }
    }

    private void Notice(string message) { lock (Notices) Notices.Add(message); }
}
