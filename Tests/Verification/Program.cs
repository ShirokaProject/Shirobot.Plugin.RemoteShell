using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using RemoteShell;
using ShiroBot.SDK.Config;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;
using Tomlyn;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var root = Path.Combine(Path.GetTempPath(), "remoteshell-verify-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var template = File.ReadAllText(Path.Combine(projectRoot, "config.toml"));
var config = TomlSerializer.Deserialize<PluginConfig>(template,
    new TomlSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;
Check(!config.Enabled && config.AllowedUsers.Count == 0 && !config.AllowRawCommands && config.Commands.Count == 0,
    "Shipped TOML must be disabled, with empty users/commands and raw shell disabled.");
var defaultPolicy = ShellPolicy.From(config, root);
Check(defaultPolicy.WorkingDirectory == root, "Relative working directories must use the plugin directory.");
try
{
    ShellPolicy.From(new PluginConfig { AllowedUsers = ["123"] }, root);
    throw new Exception("Bare user IDs must not authorize across instances.");
}
catch (FormatException) { }

var replies = new List<string>();
var messageContext = DispatchProxy.Create<IMessageContext, TestProxy>();
((TestProxy)(object)messageContext).Call = (method, values) =>
{
    Check(method.Name == "ReplyAsync", "Only a plain-text reply is expected.");
    replies.Add(values!.OfType<string>().Single());
    return Task.FromResult(new SentMessage("reply"));
};
var configContext = DispatchProxy.Create<IConfigContext, TestProxy>();
((TestProxy)(object)configContext).Call = (method, _) => method.Name switch
{
    "Load" => config,
    "get_ConfigPath" => Path.Combine(root, "config.toml"),
    _ => throw new NotSupportedException(method.Name)
};
var context = DispatchProxy.Create<IBotContext, TestProxy>();
((TestProxy)(object)context).Call = (method, _) => method.Name switch
{
    "get_Config" => configContext,
    "get_PluginDirectory" => root,
    "get_Message" => messageContext,
    _ => throw new NotSupportedException(method.Name) // No implicit owner/admin bypass.
};
var plugin = new Plugin();
await plugin.OnLoad(context);
var routes = typeof(PluginBase).GetProperty("GroupCommands", BindingFlags.Instance | BindingFlags.NonPublic)!;
var group = (CommandRouter<MessageEvent>)routes.GetValue(plugin)!;
var direct = (CommandRouter<MessageEvent>)typeof(PluginBase)
    .GetProperty("DirectCommands", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!;
MessageEvent Message(string text, string? instance = "qq-main", string user = "123") => new()
{
    InstanceId = instance, Platform = "qq", SelfId = "bot", MessageId = "1",
    Channel = Channel.Group("group"), Sender = new User(user), Segments = [new TextSegment(text)]
};
async Task Send(string text, string? instance = "qq-main", string user = "123") =>
    await group.DispatchAsync(text, Message(text, instance, user));
async Task Apply(PluginConfig next) => await ((IConfigurableComponent)plugin).ApplyConfigAsync(next);
string Last() => replies[^1];

try
{
    await Send("#shell check");
    Check(Last().Contains("未启用"), "Default config must refuse execution.");
    await Apply(new PluginConfig { Enabled = true });
    await Send("#shell check");
    Check(Last().Contains("无权限"), "Enabled with an empty whitelist must refuse everyone.");

    var command = OperatingSystem.IsWindows()
        ? "'executed' | Set-Content -Path marker.txt; Write-Output 'stdout'; [Console]::Error.Write('stderr'); exit 7"
        : "printf executed > marker.txt; printf stdout; printf stderr >&2; exit 7";
    config = new PluginConfig
    {
        Enabled = true, AllowedUsers = ["qq-main:123"], WorkingDirectory = root,
        Commands = [new CommandEntry { Name = "check", Command = command }]
    };
    await Apply(config);
    foreach (var (instance, user) in new (string?, string)[] { ("qq-main", "456"), ("other", "123"), (null, "123") })
    {
        await Send("#shell check", instance, user);
        Check(Last().Contains("无权限"), "Whitelist must bind user ID and instance, rejecting missing sources.");
    }
    Check(!File.Exists(Path.Combine(root, "marker.txt")), "Unauthorized users must never start a process.");
    await Send("#shell check; echo injected");
    Check(Last().Contains("未配置此命令"), "Alias commands must not accept shell suffixes.");
    await Send("#shell check extra");
    Check(Last().Contains("未配置此命令"), "Alias commands must not accept arbitrary arguments.");
    Check(!File.Exists(Path.Combine(root, "marker.txt")), "Rejected aliases must never start a process.");
    var replyCount = replies.Count;
    await Send("#shellfish check");
    Check(replies.Count == replyCount, "Similar prefixes must not execute or reply.");
    await Send("#shell");
    Check(Last().Contains("check"), "Authorized help must list configured aliases.");
    await Send("#shell check");
    Check(File.ReadAllText(Path.Combine(root, "marker.txt")).Trim() == "executed" &&
        Last().Contains("退出码：7") && Last().Contains("stdout") && Last().Contains("stderr"),
        "Allowed commands must execute in the configured directory and return both streams and exit status.");
    await direct.DispatchAsync("#shell check", Message("#shell check") with { Channel = Channel.Direct("123") });
    Check(Last().Contains("退出码：7"), "Direct messages must use the same authorization and execution.");

    await Apply(new PluginConfig { Enabled = true, AllowedUsers = ["other:123"] });
    await Send("#shell check");
    Check(Last().Contains("无权限"), "Live whitelist changes must revoke subsequent execution.");
    await Apply(new PluginConfig { AllowedUsers = ["qq-main:123"] });
    await Send("#shell check");
    Check(Last().Contains("未启用"), "Whitelisted users still need the manual enable switch.");

    config.AllowRawCommands = true;
    await Apply(config);
    await Send(OperatingSystem.IsWindows() ? "#shell Write-Output raw" : "#shell printf raw");
    Check(Last().Contains("raw") && Last().Contains("退出码：0"), "Explicit raw mode must execute the supplied command for whitelisted users.");

    var flood = OperatingSystem.IsWindows() ? "[Console]::Write(('x' * 20000))" : "i=0; while [ $i -lt 10000 ]; do printf xx; i=$((i+1)); done";
    var bounded = await ShellRunner.RunAsync(flood, root, 10, 100, CancellationToken.None);
    Check(bounded.Truncated && bounded.Output.Length == 100, "Output must stay bounded while continuing to drain the child.");
    var slow = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 20" : "sleep 20";
    var watch = Stopwatch.StartNew();
    var timeout = await ShellRunner.RunAsync(slow, root, 1, 100, CancellationToken.None);
    Check(timeout.TimedOut && watch.Elapsed < TimeSpan.FromSeconds(5), "Timeout must stop long-running commands promptly.");
    using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
    {
        try
        {
            await ShellRunner.RunAsync(slow, root, 10, 100, cancel.Token);
            throw new Exception("Cancelled executions must not succeed.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
    }

    // One long command holds the gate; a concurrent command must be refused, not queued.
    config.Commands = [new CommandEntry { Name = "slow", Command = slow }];
    config.AllowRawCommands = false;
    config.TimeoutSeconds = 1;
    await Apply(config);
    var running = Send("#shell slow");
    await Send("#shell slow");
    Check(Last().Contains("已有命令正在执行"), "Only one command may run at a time.");
    await running;
    Check(Last().Contains("执行超时"), "Plugin must report timeouts.");
    var unloadingRun = Send("#shell slow");
    await plugin.OnUnload();
    await unloadingRun;
    Console.WriteLine("PASS: disabled template, explicit enable, instance/user whitelist, aliases/injection rejection, raw opt-in, live revocation, stdout/stderr, exit codes, output limits, timeout, cancellation, concurrency and unload.");
}
finally
{
    Directory.Delete(root, recursive: true);
}

public class TestProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Call { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!, args);
}
