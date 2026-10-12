# wingetx v1.1.2

Windows x64 独立运行版，自带 .NET 8.0.31。升级系统 .NET 时，wingetx 不再占用被更新的系统运行库。

- `install` 自动记录可识别的安装结果。
- `upgrade` 按原参数调用 WinGet。
- `upgrade --tracked` 逐个更新跟踪包并转发用户提供的 flags。
- `upall` 逐个更新跟踪包，自动添加两个协议确认参数和 `--disable-interactivity`。
- 提供跟踪清单查看、手动纳入、取消跟踪及备份恢复。

下载 `wingetx-1.1.2-win-x64.zip` 并解压，按照包内 README 安装到 `%USERPROFILE%\.local\bin`。使用时需要 WinGet，无需安装 .NET。已有用户替换 EXE 即可，跟踪清单位于 `%LOCALAPPDATA%\wingetx`。

`SHA256SUMS.txt` 提供 ZIP 校验值。发布包附带 MIT 许可证和 .NET 第三方声明。源码仓库不包含个人清单和运行日志。

本次发布通过 88 项隔离回归检查，涵盖独立运行、参数转发、安装跟踪与退出码处理。
