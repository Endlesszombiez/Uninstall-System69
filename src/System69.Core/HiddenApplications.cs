using System.Text.Json;

namespace System69.Core;

/// <summary>Per-user inventory preferences; hiding never changes installed software.</summary>
public sealed class HiddenApplications(string filePath)
{
    private HashSet<string> hidden = new(StringComparer.OrdinalIgnoreCase);
    public string? LoadError { get; private set; }

    public void Load()
    {
        try
        {
            hidden = File.Exists(filePath)
                ? new(JsonSerializer.Deserialize<string[]>(File.ReadAllText(filePath)) ?? throw new JsonException("Expected a list of hidden application IDs."), StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
            LoadError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadError = "Hidden app preferences could not be read. The file was left unchanged. " + ex.Message;
        }
    }

    public bool Contains(InstalledApp app) => hidden.Contains(Key(app));

    public void Set(IEnumerable<InstalledApp> apps, bool hide)
    {
        if (LoadError is not null) throw new InvalidOperationException(LoadError);
        var updated = new HashSet<string>(hidden, StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            if (hide) updated.Add(Key(app)); else updated.Remove(Key(app));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        var temporary = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(updated.Order(StringComparer.OrdinalIgnoreCase), new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n").Replace("\n", "\r\n"));
            File.Move(temporary, filePath, true);
            hidden = updated;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Key(InstalledApp app)
    {
        // Store full identities include the version; use the stable package family instead.
        var id = app.Id;
        if (app.Source == PackageSource.Store)
        {
            var parts = id.Split('_');
            if (parts.Length >= 5) id = parts[0] + "_" + parts[^1];
        }
        return app.Source + ":" + id;
    }
}
