using System.Text.RegularExpressions;

namespace System69.Core;

public static partial class CommandPolicy
{
    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-(?:[0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}\}$")]
    private static partial Regex ProductCode();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+@/-]*$")]
    private static partial Regex PackageId();
    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-(?:[0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}\}")]
    private static partial Regex EmbeddedProductCode();

    public static UninstallCommand Create(InstalledApp app)
    {
        if (app.Protected) throw new InvalidOperationException("This package is protected and cannot be removed.");
        if (app.Source == PackageSource.Chocolatey && app.Id.Equals("all", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Chocolatey all-packages identifier is blocked.");
        if (app.Source != PackageSource.Windows && !PackageId().IsMatch(app.Id))
            throw new InvalidOperationException("Invalid package identifier.");
        return app.Source switch
        {
            PackageSource.Winget => new("winget.exe", ["uninstall", "--id", app.Id, "--exact", "--silent", "--disable-interactivity"], true),
            PackageSource.Chocolatey => new("choco.exe", ["uninstall", app.Id, "--yes", "--no-progress", "--limit-output"], true),
            PackageSource.Homebrew => new("wsl.exe", ["--exec", "/home/linuxbrew/.linuxbrew/bin/brew", "uninstall", "--formula", app.Id], true),
            PackageSource.Store => StoreCommand(app),
            _ => WindowsCommand(app)
        };
    }

    private static UninstallCommand StoreCommand(InstalledApp app)
    {
        // The identifier is validated above. EncodedCommand avoids shell re-parsing.
        var script = $"$ErrorActionPreference = 'Stop'; Remove-AppxPackage -Package '{app.Id}' -ErrorAction Stop";
        return new("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script))], true);
    }

    private static UninstallCommand WindowsCommand(InstalledApp app)
    {
        if (app.WindowsInstaller)
        {
            var registeredKey = app.Id.Split('|').Last();
            var code = ProductCode().IsMatch(registeredKey) ? registeredKey : EmbeddedProductCode().Match(app.UninstallCommand ?? "").Value;
            if (!ProductCode().IsMatch(code)) throw new InvalidOperationException("The MSI product code could not be verified.");
            return new("msiexec.exe", ["/x", code, "/qn", "/norestart"], true);
        }
        var quiet = !string.IsNullOrWhiteSpace(app.QuietCommand);
        var command = quiet ? app.QuietCommand! : app.UninstallCommand;
        if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("No registered uninstall command is available.");
        var (executable, arguments) = SplitRegisteredCommand(command);
        var leaf = Path.GetFileName(executable);
        if (new[] { "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe", "reg.exe" }.Contains(leaf, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("This uninstaller uses a script host. Run it manually from Windows Settings.");
        if (!Path.IsPathFullyQualified(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The uninstaller must be an absolute path to an executable.");
        return new(executable, [], quiet, arguments);
    }

    public static (string Executable, string Arguments) SplitRegisteredCommand(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end < 0) throw new InvalidOperationException("Unbalanced quotes in uninstall command.");
            return (command[1..end], command[(end + 1)..].Trim());
        }
        // Unquoted paths containing spaces are ambiguous and must never be guessed.
        var space = command.IndexOf(' ');
        var exe = space < 0 ? command : command[..space];
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Ambiguous unquoted uninstall path; run this uninstaller manually.");
        return (exe, space < 0 ? "" : command[(space + 1)..].Trim());
    }
}
