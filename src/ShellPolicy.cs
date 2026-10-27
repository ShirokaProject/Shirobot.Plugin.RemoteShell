using ShiroBot.SDK.Models;

namespace RemoteShell;

internal sealed record ShellPolicy(
    bool Enabled,
    UserReference[] AllowedUsers,
    bool AllowRawCommands,
    string WorkingDirectory,
    int TimeoutSeconds,
    int MaxOutputChars,
    IReadOnlyDictionary<string, string> Commands)
{
    internal static ShellPolicy From(PluginConfig config, string pluginDirectory)
    {
        if (config.TimeoutSeconds is < 1 or > 300)
            throw new ArgumentException("timeout_seconds 必须在 1–300 之间。");
        if (config.MaxOutputChars is < 100 or > 10000)
            throw new ArgumentException("max_output_chars 必须在 100–10000 之间。");
        var users = config.AllowedUsers.Select(UserReference.Parse).ToArray();
        var commands = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in config.Commands)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Any(char.IsWhiteSpace) ||
                string.IsNullOrWhiteSpace(entry.Command) || !commands.TryAdd(entry.Name, entry.Command))
                throw new ArgumentException("commands 必须使用非空、唯一、无空白的别名及非空命令。");
        }
        if (string.IsNullOrWhiteSpace(config.WorkingDirectory))
            throw new ArgumentException("working_directory 不能为空。");
        var directory = Path.GetFullPath(config.WorkingDirectory, pluginDirectory);
        return new(config.Enabled, users, config.AllowRawCommands, directory,
            config.TimeoutSeconds, config.MaxOutputChars, commands);
    }

    internal bool Allows(MessageEvent message) =>
        !string.IsNullOrWhiteSpace(message.InstanceId) &&
        AllowedUsers.Any(user => user.Matches(new UserReference(message.InstanceId, message.Sender.Id)));

    internal string? Resolve(string input) =>
        AllowRawCommands ? input : Commands.GetValueOrDefault(input);
}
