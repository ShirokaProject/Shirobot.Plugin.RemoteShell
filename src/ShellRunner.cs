using System.Diagnostics;
using System.Text;

namespace RemoteShell;

internal sealed record ShellResult(int? ExitCode, bool TimedOut, bool Truncated, string Output);

internal static class ShellRunner
{
    internal static async Task<ShellResult> RunAsync(
        string command, string workingDirectory, int timeoutSeconds, int maxOutputChars,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
        }
        else start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);

        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        if (!process.Start()) throw new InvalidOperationException("无法启动 shell 进程。");
        process.StandardInput.Close();
        var output = new BoundedOutput(maxOutputChars);
        var stdout = DrainAsync(process.StandardOutput, output, deadline.Token);
        var stderr = DrainAsync(process.StandardError, output, deadline.Token);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            // Some commands leave descendants holding the output pipes open; apply the same deadline.
            await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        finally
        {
            await deadline.CancelAsync().ConfigureAwait(false);
            // Cancellation interrupts reads without waiting for a descendant to close an inherited pipe.
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(process.HasExited ? process.ExitCode : null, timedOut, output.Truncated, output.Text);
    }

    private static async Task DrainAsync(StreamReader reader, BoundedOutput output, CancellationToken token)
    {
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
            output.Append(buffer, count);
    }

    private sealed class BoundedOutput(int limit)
    {
        private readonly Lock _gate = new();
        private readonly StringBuilder _text = new();
        internal bool Truncated { get; private set; }
        internal string Text { get { lock (_gate) return _text.ToString(); } }

        internal void Append(char[] buffer, int count)
        {
            lock (_gate)
            {
                var take = Math.Min(count, limit - _text.Length);
                _text.Append(buffer, 0, take);
                if (take != count) Truncated = true;
            }
        }
    }
}
