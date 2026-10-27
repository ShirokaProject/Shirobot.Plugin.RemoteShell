# RemoteShell

[提供用户在远程执行Shell管理的功能](https://github.com/ShirokaProject/Shirobot.Plugin.RemoteShell)。默认关闭，必须手动开启 `config.toml` 的 `enabled`，并配置用户白名单后才能执行。宿主 owner/admin 身份不会绕过插件白名单。

## 安装和开启

⚠️ 请谨慎开启 RemoteShell，远程执行 Shell 命令存在风险。

Release 包包含 `RemoteShell.dll` 和默认关闭的 `config.toml`，一起放入宿主的 `plugins/RemoteShell/`。已有配置请保留。修改该目录的 `config.toml`，例如：

```toml
enabled = true
allowed_users = ["qq-main:123456789"]
allow_raw_commands = false
working_directory = "."
timeout_seconds = 30
max_output_chars = 2000

[[commands]]
name = "disk"
command = "df -h"

[[commands]]
name = "uptime"
command = "uptime"
```

`qq-main` 是 Dashboard 中的适配器实例 ID，`123456789` 是平台传入的发送者用户 ID。官方 QQ 需填写该实例内实际收到的用户 ID。身份必须同时匹配实例和用户；空白名单拒绝所有用户。ID 中的冒号使用 `%3A` 编码，与宿主 `UserReference` 格式一致。

以上命令示例适用于 macOS/Linux。群聊或私聊发送：

```text
#shell
#shell disk
#shell uptime
```

不带参数时显示用法及预设命令别名。预设模式只接受完整别名：`#shell disk extra`、`#shell disk; whoami` 均不会执行。禁用状态、白名单检查、命令选择、并发检查均在创建进程之前完成。

需要白名单用户直接输入任意命令时，**另外**手动将 `allow_raw_commands` 改为 `true`：

```text
#shell pwd
#shell ls -la
```

此时别名表不参与解析，`#shell` 后面的文本直接交给 shell。自动按 `zsh → bash → pwsh → powershell → cmd → sh` 顺序选择已安装且可执行的 shell，`sh` 用于精简环境兜底。命令语法应与最终选中的 shell 一致；不加载用户 shell profile。命令使用宿主操作系统账号的权限，工作目录默认为插件数据目录；不是交互终端，不支持需要输入密码等交互的命令。

## 执行结果

```text
执行完成，退出码：0
Filesystem ...
```

所有子进程设置 `NO_COLOR=1`、`CLICOLOR=0` 并移除强制颜色环境变量。PowerShell 自动使用 Text 输出和 PlainText 渲染。标准输出和标准错误分别以流式方式清理 ANSI 颜色、光标控制和 OSC 控制串，保留中文、普通文本、换行与制表符，合并后以纯文本回传。颜色控制码不会占用输出字符额度。

合并返回标准输出和标准错误。输出默认最多 2000 个字符，超出后截断；进程输出会继续被读取，避免管道堵塞。每次只执行一个命令，后续请求返回“已有命令正在执行”，不会排队。默认 30 秒超时后尝试终止进程树；插件卸载时取消执行。主动脱离进程树的后台进程不受此机制保证，请将预设命令配置为前台任务。

没有聊天命令能够修改启用开关、白名单或命令配置。宿主支持配置热更新；更改开关及白名单作用于后续请求，已经开始的命令继续执行到结束或超时。日志只记录执行者、退出码、超时等信息，不记录命令内容或输出。

## 本地开发

使用官方 `ShiroBot.Templates` 生成，.NET 10、ShiroBot.SDK 1.0.1，与本地其他插件使用同一套开发方式。SDK 1.0.1 可直接从 nuget.org 还原。

```sh
dotnet build -c Debug
sh scripts/dev.sh --prepare   # 只准备环境
sh scripts/dev.sh             # 构建并启动
```

Rider 选择 `ShiroBot` Run/Debug 配置；独立开发 Dashboard 为 <http://127.0.0.1:7004/dashboard/>。开发目录为 `.shirobot-dev/host/<RID>/`。调试构建会复制 DLL、PDB 和首次使用的配置，保留已存在的 `config.toml`。

Windows 使用 `powershell -ExecutionPolicy Bypass -File scripts/dev.ps1`；Windows 的 IDE 启动入口应指向 `ShiroBot.exe`。

## 验证和构建

```sh
dotnet run --project Tests/Verification -p:ShiroBotDevelopmentHostEnabled=false
dotnet build -c Release
dotnet run --project scripts/version-check -c Release -- bin/Release/net10.0/RemoteShell.dll 0.1.1 0.1.1
```

验证覆盖默认关闭、空白名单、跨实例用户隔离、预设命令参数注入拦截、任意命令显式开启、配置热更新、标准输出/错误、退出码、输出限制、超时、取消、并发、卸载、shell 选择优先级、NO_COLOR 环境及跨分块 ANSI 清理。本机已实际验证 zsh/bash/pwsh/sh 的中文、颜色清理和退出码。Windows PowerShell/cmd 的参数构造有验证覆盖，仍需 Windows 实机测试。Release 输出位于 `bin/Release/net10.0/`；官方模板的 GitHub Actions 发布工作流已保留。

## 许可证

[GPL-3.0-only](LICENSE)。
