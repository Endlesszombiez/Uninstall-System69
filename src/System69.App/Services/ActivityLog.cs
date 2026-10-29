using System.IO;
using System.Text.Json;
namespace System69.App.Services;

public static class ActivityLog
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "System69", "Logs");
    public static void Write(string name, string source, string status, string detail)
    {
        Directory.CreateDirectory(DirectoryPath);
        var line = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, name, source, status, detail });
        File.AppendAllText(Path.Combine(DirectoryPath, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".jsonl"), line + "\r\n");
    }
}
