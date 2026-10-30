using System.ComponentModel;
using System.Runtime.CompilerServices;
using System69.Core;

namespace System69.App;

public class AppRow(InstalledApp app) : INotifyPropertyChanged
{
    public InstalledApp App { get; } = app;
    public string Name => App.Name;
    public string Publisher => App.Publisher;
    public string Version => App.Version;
    public string Source => App.SourceLabel;
    public string Size => App.SizeLabel;
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public string Mode => App.Protected ? "Protected" : App.SupportsQuiet ? "Silent requested" : "May show a window";
    private bool hidden;
    public bool Hidden { get => hidden; set { if (hidden == value) return; hidden = value; Changed(); Changed(nameof(VisibilityLabel)); } }
    public string VisibilityLabel => Hidden ? "Hidden" : "";
    private bool selected;
    public bool Selected { get => selected; set { if (selected == value) return; selected = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class QueueRow(InstalledApp app) : AppRow(app)
{
    private string status = "Ready";
    private string detail = "Waiting in queue";
    public string Status { get => status; set { status = value; Changed(); } }
    public string Detail { get => detail; set { detail = value; Changed(); } }
}

public sealed record ReviewRow(string Name, string Source, string Mode, string Cleanup);
