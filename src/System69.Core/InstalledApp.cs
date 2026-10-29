namespace System69.Core;

public enum PackageSource { Windows, Winget, Chocolatey, Store, Homebrew }

public sealed record InstalledApp(
    string Id,
    string Name,
    string Version,
    string Publisher,
    PackageSource Source,
    string? UninstallCommand = null,
    string? QuietCommand = null,
    string? InstallLocation = null,
    long SizeBytes = 0,
    bool WindowsInstaller = false,
    bool Protected = false)
{
    public bool SupportsQuiet => Source != PackageSource.Windows || WindowsInstaller || !string.IsNullOrWhiteSpace(QuietCommand);
    public string SourceLabel => Source == PackageSource.Store ? "Microsoft Store" : Source == PackageSource.Homebrew ? "Homebrew · WSL" : Source.ToString();
    public string SizeLabel => SizeBytes <= 0 ? "—" : SizeBytes >= 1L << 30 ? $"{SizeBytes / (double)(1L << 30):0.0} GB" : $"{SizeBytes / (double)(1L << 20):0} MB";
}

public sealed record ProcessResult(int ExitCode, string Output, string Error);
public sealed record UninstallCommand(string FileName, IReadOnlyList<string> Arguments, bool Quiet, string? RawArguments = null);
public sealed record CleanupDecision(bool Allowed, string Reason, string? CanonicalPath = null);
