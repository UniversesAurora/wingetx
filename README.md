# wingetx

为 WinGet 添加一份明确的程序包跟踪清单。通过 `wingetx install` 安装后自动记录，之后用 `wingetx upall` 更新清单内的软件。WinGet 会列出它能识别的软件；wingetx 从开始使用时记录安装结果，已有软件可手动纳入。

## 安装与更新

需要 Windows x64 和可运行的 `winget.exe`。从 [Releases](https://github.com/UniversesAurora/wingetx/releases) 下载 `wingetx-1.1.2-win-x64.zip` 并解压。发布包自带 .NET 运行时，无需另外安装 SDK 或运行库。

在解压目录用 PowerShell 7 执行：

```powershell
$binDir = Join-Path $env:USERPROFILE '.local\bin'
New-Item -ItemType Directory -Path $binDir -Force | Out-Null
$targetExe = Join-Path $binDir 'wingetx.exe'
if (Test-Path -LiteralPath $targetExe) {
    Copy-Item -LiteralPath $targetExe -Destination "$targetExe.$(Get-Date -Format yyyyMMddHHmmss).bak"
}
Copy-Item -LiteralPath .\wingetx.exe -Destination $targetExe -Force
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (($userPath -split ';') -notcontains $binDir) {
    [Environment]::SetEnvironmentVariable('Path', (([string]$userPath).TrimEnd(';') + ';' + $binDir).TrimStart(';'), 'User')
}
```

替换文件前，请先让正在运行的 wingetx 命令结束。安装后重新打开终端，运行 `wingetx --help`。升级 wingetx 本身时，下载新 Release 并替换 EXE；程序包清单保存在另一个目录。

## 使用说明

```powershell
# 安装并自动跟踪
wingetx install --id GitHub.cli --exact --source winget
# 查看清单
wingetx list --tracked
# 更新清单中的全部程序包，自动确认协议并禁用 WinGet 交互提示
wingetx upall
# 更新清单中的全部程序包，只转发你提供的其他 flags
wingetx upgrade --tracked --silent
# 普通 upgrade 保持 WinGet 原有行为
wingetx upgrade --id GitHub.cli --exact
```

`upall` 和 `upgrade --tracked` 都逐条调用 `winget upgrade --id <ID> --exact`，再按记录添加源、架构和范围。只有 `upall` 自动添加 `--accept-source-agreements --accept-package-agreements --disable-interactivity`；`--tracked` 会被移除，其余 flags 转发给每个程序包。

这两个受管升级命令不接受查询、包选择器、`--all`、`--source`、`--architecture` 或 `--scope`，因为条件由清单决定。普通 `wingetx upgrade --all` 仍会执行 WinGet 自身的全量升级。

`--silent` 控制安装器界面，`--disable-interactivity` 控制 WinGet 提示；管理员授权仍取决于 Windows 和安装器。

已有程序包可以手动记录；同一 ID 的不同架构可以分别纳入：

```powershell
wingetx track junegunn.fzf --source winget --architecture x64 --scope user
wingetx track Microsoft.DotNet.DesktopRuntime.8 --source winget --architecture x64 --scope machine
wingetx track Microsoft.DotNet.DesktopRuntime.8 --source winget --architecture x86 --scope machine
wingetx untrack junegunn.fzf
wingetx uninstall --id GitHub.cli --exact
```

`untrack` 只删除清单条目，不卸载软件。通过 wingetx 成功卸载的已识别程序包会从清单移除。

## 数据与限制

清单位于 `%LOCALAPPDATA%\wingetx\packages.json`，备份为同目录的 `packages.json.bak`。写入使用临时文件替换和互斥锁；主文件损坏时会尝试读取备份。源码和 Release 不包含个人清单。

`install` / `add` 根据安装前后的 WinGet 导出结果和显式 ID 记录程序包；`import` / `configure` 也会根据结果记录。部分安装失败时，已成功安装且能解析的部分仍可能被记录。成功但无法可靠解析 ID 时会提示手动 `track`。

直接调用 `winget`、官网安装器或其他管理器不会自动记录。导出差异可能包含新安装的依赖；源无法导出时记录能力会受限。手动 `track` 不验证包是否已安装。未记录架构或范围的条目由 WinGet 选择。

逐包升级的“已经是最新”和“当前源无法匹配”分别计数；其他失败显示原始退出码。出现失败时最终返回 WinGet 的聚合升级失败码，普通透传命令保留 WinGet 退出码。

## 从源码构建

需要 Windows x64、PowerShell 7、.NET 8 SDK 和用于下载运行时包的网络连接：

```powershell
git clone git@github.com:UniversesAurora/wingetx.git
cd wingetx
pwsh -NoProfile -File .\Publish-Standalone.ps1
```

产物为 `publish-standalone\wingetx.exe`。项目拒绝依赖系统运行库的发布方式；系统 .NET 更新时不会占用正在被替换的系统运行库。独立运行时的安全更新需要重新构建和发布 wingetx。

## 发布包与许可证

Release ZIP 包含 EXE、本说明、MIT 许可证和 .NET 第三方声明，并提供独立的 `SHA256SUMS.txt`。下载后用 `Get-FileHash -Algorithm SHA256` 核对 ZIP。编译产物和测试日志不提交到 Git。

维护者构建并测试后运行 `pwsh -NoProfile -File .\Package-Release.ps1`，在 `dist` 生成 ZIP 和校验文件。使用 `gh release create v1.1.2 .\dist\wingetx-1.1.2-win-x64.zip .\dist\SHA256SUMS.txt --verify-tag --title "wingetx v1.1.2" --notes-file .\RELEASE-NOTES.md` 上传发布包；执行前需已推送对应 Git 标签。

wingetx 源码采用 [MIT 许可证](LICENSE)。WinGet 和 .NET 运行时采用各自许可证。

## Developer notes

WinGet wrapper with an explicit tracked-package allowlist. Native command syntax
and the existing tracking database remain unchanged in version 1.1.2.

- `wingetx upgrade ...` forwards the native command.
- `wingetx upgrade --tracked ...` upgrades each tracked package and forwards only
  the user-supplied extra flags, alongside that package's exact selectors.
- `wingetx upall ...` does the same and adds `--accept-source-agreements`,
  `--accept-package-agreements`, and `--disable-interactivity`.

## Standalone publishing

Run with PowerShell 7 and the .NET 8 SDK:

```powershell
pwsh -NoProfile -File .\Publish-Standalone.ps1
```

Deploy the resulting `publish-standalone/wingetx.exe`. Publishing is deliberately
self-contained and single-file, including native runtime libraries. A build guard
rejects framework-dependent publishing, including `--self-contained false`.

The old framework-dependent build used the installed system .NET 8 runtime.
On 2026-09-02, SDK servicing removed runtime 8.0.29 and Windows Restart Manager
closed wingetx because it held that runtime's files. The installer still succeeded,
but the front-end reported an abandoned installation. The standalone Windows
build instead has CoreCLR and the JIT statically linked into its executable and
loads managed assemblies from the bundle. Any additional bundled native libraries
that require extraction use a private per-bundle temporary directory. It does not
use `Program Files/dotnet`.

Bundled runtime security fixes require rebuilding and redeploying wingetx with a
serviced SDK/runtime; updating system .NET does not update this private runtime.
Do not trim this reflection-based JSON implementation without compatibility tests.

## Regression tests

```powershell
pwsh -NoProfile -File .\tests\Test-Standalone.ps1 -ExePath .\publish-standalone\wingetx.exe
```

Tests use a fake winget and isolated databases. They check the loaded runtime
locations, flag forwarding, tracking, and exit codes without installing or
upgrading real packages.
