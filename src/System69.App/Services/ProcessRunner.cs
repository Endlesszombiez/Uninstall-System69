using System.Diagnostics;
using System69.Core;

namespace System69.App.Services;

public sealed class ProcessStillRunningException(string message) : Exception(message);

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token, TimeSpan? timeout = null, string? rawArguments = null, bool quiet = true)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = quiet };
        if (rawArguments is not null) info.Arguments = rawArguments;
        else foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeoutToken = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutToken.CancelAfter(timeout ?? TimeSpan.FromMinutes(30));
        try { await process.WaitForExitAsync(timeoutToken.Token); }
        catch (OperationCanceledException)
        {
            // Never kill an uninstaller mid-transaction. Stop the queue and leave it running.
            throw new ProcessStillRunningException("Process is still running after the time limit. The queue stopped; finish the uninstall before continuing.");
        }
        return new(process.ExitCode, await stdout, await stderr);
    }
}
