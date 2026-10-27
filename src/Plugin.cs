using System.ComponentModel;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

[assembly: ShiroBotApiCompatibility("1.0", "1.0")]

namespace RemoteShell;

[BotPlugin("RemoteShell",
    Name = "RemoteShell",
    Version = "0.1.0",
    Author = "ShirokaProject",
    GithubRepo = "ShirokaProject/Shirobot.Plugin.RemoteShell",
    Description = "手动启用并配置用户白名单后，执行预设命令或 shell 命令。",
    Category = PluginCategory.Utility,
    IsPluginSingleFile = true)]
public sealed class Plugin : PluginBase<PluginConfig>
{
    private ShellPolicy _policy = null!;
    private readonly SemaphoreSlim _execution = new(1, 1);
    private readonly CancellationTokenSource _stop = new();

    protected override Task OnConfigLoadedAsync(PluginConfig config, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _policy = ShellPolicy.From(config, Context.PluginDirectory);
        return Task.CompletedTask;
    }

    protected override Task OnConfigChangedAsync(PluginConfig previous, PluginConfig current,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _policy, ShellPolicy.From(current, Context.PluginDirectory));
        return Task.CompletedTask;
    }

    protected override void ConfigureRoutes()
    {
        GroupCommands.MapPrefix("#shell", HandleShellAsync);
        DirectCommands.MapPrefix("#shell", HandleShellAsync);
    }

    private async Task HandleShellAsync(MessageEvent message)
    {
        var text = message.GetPlainText().Trim();
        const string prefix = "#shell";
        if (text.Length > prefix.Length && !char.IsWhiteSpace(text[prefix.Length])) return;
        var policy = Volatile.Read(ref _policy);
        if (!policy.Enabled)
        {
            await Context.Message.ReplyAsync(message, "RemoteShell 未启用，请先手动修改 config.toml。");
            return;
        }
        if (!policy.Allows(message))
        {
            await Context.Message.ReplyAsync(message, "无权限执行命令。");
            return;
        }
        var input = text[prefix.Length..].Trim();
        if (input.Length == 0)
        {
            var help = policy.AllowRawCommands
                ? "用法：#shell <命令>"
                : "用法：#shell <别名>\n可用命令：" + (policy.Commands.Count == 0
                    ? "未配置" : string.Join("、", policy.Commands.Keys));
            await Context.Message.ReplyAsync(message, help);
            return;
        }
        var command = policy.Resolve(input);
        if (command is null)
        {
            await Context.Message.ReplyAsync(message, "未配置此命令别名；当前只允许执行 config.toml 中的预设命令，不接受额外参数。");
            return;
        }
        if (_stop.IsCancellationRequested) return;
        if (!await _execution.WaitAsync(0))
        {
            await Context.Message.ReplyAsync(message, "已有命令正在执行，请稍后再试。");
            return;
        }
        try
        {
            var result = await ShellRunner.RunAsync(command, policy.WorkingDirectory,
                policy.TimeoutSeconds, policy.MaxOutputChars, _stop.Token);
            var status = result.TimedOut ? $"执行超时（{policy.TimeoutSeconds} 秒）"
                : $"执行完成，退出码：{result.ExitCode?.ToString() ?? "未知"}";
            var output = string.IsNullOrWhiteSpace(result.Output) ? "（无输出）" : result.Output.TrimEnd();
            await Context.Message.ReplyAsync(message,
                $"{status}\n{output}" + (result.Truncated ? "\n（输出已截断）" : ""));
            // Do not log command bodies or output: they may contain credentials.
            BotLog.Info($"[RemoteShell] 实例 {message.InstanceId} 用户 {message.Sender.Id} 执行结束，退出码 {result.ExitCode}，超时 {result.TimedOut}。");
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            BotLog.Warning($"[RemoteShell] 命令执行失败：{error.GetType().Name}。");
            await Context.Message.ReplyAsync(message, "执行失败，请检查工作目录、shell 和宿主进程权限。");
        }
        finally { _execution.Release(); }
    }

    protected override async Task OnUnloadAsync()
    {
        await _stop.CancelAsync();
        await _execution.WaitAsync();
        _execution.Release();
        _stop.Dispose();
    }
}
