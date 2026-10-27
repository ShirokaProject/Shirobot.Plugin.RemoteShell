using System.Diagnostics;
using System.Text;

namespace RemoteShell;

internal enum ShellKind { Zsh, Bash, PowerShell, Cmd, Sh }

internal sealed record ShellExecutable(ShellKind Kind, string Path);

internal static class ShellEnvironment
{
    private static readonly (string Name, ShellKind Kind)[] Priority =
    [
        ("zsh", ShellKind.Zsh), ("bash", ShellKind.Bash),
        ("pwsh", ShellKind.PowerShell), ("powershell", ShellKind.PowerShell),
        ("cmd", ShellKind.Cmd), ("sh", ShellKind.Sh)
    ];

    internal static ShellExecutable Select(Func<string, string?>? find = null)
    {
        find ??= FindExecutable;
        foreach (var (name, kind) in Priority)
            if (find(name) is { } path) return new(kind, path);
        throw new InvalidOperationException("未找到可用的 shell（zsh/bash/pwsh/powershell/cmd/sh）。");
    }

    internal static string? FindExecutable(string name)
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim('"')).Concat(SystemDirectories());
        var fileName = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (var directory in directories.Distinct())
        {
            // Do not discover a replacement shell from a command's writable working directory.
            if (!System.IO.Path.IsPathFullyQualified(directory)) continue;
            var path = System.IO.Path.Combine(directory, fileName);
            try
            {
                if (!File.Exists(path)) continue;
                if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) &
                    (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0) continue;
                return path;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private static IEnumerable<string> SystemDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return system;
            yield return System.IO.Path.Combine(system, "WindowsPowerShell", "v1.0");
            yield return System.IO.Path.Combine(programs, "PowerShell", "7");
            yield return System.IO.Path.Combine(programs, "Git", "bin");
        }
        else
        {
            yield return "/bin";
            yield return "/usr/bin";
            yield return "/usr/local/bin";
            yield return "/opt/homebrew/bin";
        }
    }

    internal static ProcessStartInfo CreateStartInfo(ShellExecutable shell, string command, string workingDirectory)
    {
        var start = new ProcessStartInfo
        {
            FileName = shell.Path, WorkingDirectory = workingDirectory,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment["NO_COLOR"] = "1";
        start.Environment["CLICOLOR"] = "0";
        start.Environment.Remove("FORCE_COLOR");
        start.Environment.Remove("CLICOLOR_FORCE");
        switch (shell.Kind)
        {
            case ShellKind.Zsh:
                start.ArgumentList.Add("-f"); // Skip user startup files.
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add(command);
                break;
            case ShellKind.Bash:
                start.ArgumentList.Add("--noprofile");
                start.ArgumentList.Add("--norc");
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add(command);
                break;
            case ShellKind.PowerShell:
                start.ArgumentList.Add("-NoLogo");
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-NonInteractive");
                start.ArgumentList.Add("-OutputFormat");
                start.ArgumentList.Add("Text");
                start.ArgumentList.Add("-EncodedCommand");
                const string setup = "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); " +
                    "$OutputEncoding = [Console]::OutputEncoding; $ProgressPreference = 'SilentlyContinue'; " +
                    "if ($null -ne $PSStyle) { $PSStyle.OutputRendering = 'PlainText' };\n";
                start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(setup + command)));
                break;
            case ShellKind.Cmd:
                // cmd.exe does not follow the C argv quoting convention used by ArgumentList.
                // /s removes only these outer quotes, leaving the command's own quotes intact.
                start.Arguments = "/d /s /c \"chcp 65001 >nul & " + command + "\"";
                break;
            default:
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add(command);
                break;
        }
        return start;
    }
}
