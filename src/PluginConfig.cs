using ShiroBot.SDK.Config;

namespace RemoteShell;

[ConfigModel]
public sealed class PluginConfig
{
    [ConfigField("⚠️ 请谨慎开启，远程执行 Shell 命令存在风险。开启后仍须匹配用户白名单才能执行。", Label = "启用 RemoteShell")]
    public bool Enabled { get; set; } = false;

    [ConfigField("使用 实例ID:用户ID，例如 qq-main:123456；空白名单拒绝所有用户。", Label = "用户白名单")]
    public List<string> AllowedUsers { get; set; } = [];

    [ConfigField("关闭时只允许预设命令别名；开启后白名单用户可直接输入 shell 命令。", Label = "允许任意命令")]
    public bool AllowRawCommands { get; set; } = false;

    [ConfigField("相对路径以插件数据目录为基准；可填写绝对路径。", Label = "命令工作目录")]
    public string WorkingDirectory { get; set; } = ".";

    [ConfigField("命令执行超时后尝试终止进程树。", Label = "超时秒数", Min = 1, Max = 300)]
    public int TimeoutSeconds { get; set; } = 30;

    [ConfigField("标准输出与标准错误合计的最大字符数，超出后截断。", Label = "输出字符上限", Min = 100, Max = 10000)]
    public int MaxOutputChars { get; set; } = 2000;

    [ConfigField("任意命令关闭时，#shell 别名 只执行这里配置的完整命令，不拼接聊天参数。", Label = "预设命令")]
    public List<CommandEntry> Commands { get; set; } = [];
}

public sealed class CommandEntry
{
    [ConfigField("不包含空格的命令别名，例如 disk。", Label = "别名")]
    public string Name { get; set; } = "";

    [ConfigField("由本机 shell 执行的完整命令。", Label = "命令", Type = "text")]
    public string Command { get; set; } = "";
}
