using System.Text;

namespace WingetX;

internal static class Program
{
    private static readonly HashSet<string> ValidArchitectures =
        new(StringComparer.OrdinalIgnoreCase) { "x86", "x64", "arm", "arm64" };

    private static readonly HashSet<string> ValidScopes =
        new(StringComparer.OrdinalIgnoreCase) { "user", "machine" };

    private static WingetRunner _winget = null!;
    private static TrackingStore _store = null!;

    public static int Main(string[] rawArguments)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = Encoding.UTF8;
        _winget = new WingetRunner();
        _store = new TrackingStore();

        try
        {
            if (rawArguments.Length == 0)
            {
                return _winget.Run(rawArguments);
            }

            var arguments = rawArguments.ToList();
            var command = arguments[0].ToLowerInvariant();

            if (arguments.Count == 1 && CommandLine.IsHelpRequest(arguments))
            {
                var exitCode = _winget.Run(arguments);
                PrintExtensionHelp();
                return exitCode;
            }

            if (command is "track")
            {
                return Track(arguments);
            }

            if (command is "upall")
            {
                if (CommandLine.IsHelpRequest(arguments))
                {
                    PrintUpallHelp();
                    return WingetExitCodes.Success;
                }

                return UpgradeTracked(arguments, addAutomationDefaults: true, commandLabel: "upall");
            }

            if (command is "untrack")
            {
                return Untrack(arguments);
            }

            if (command is "list" or "ls" &&
                CommandLine.HasOption(arguments, "--tracked"))
            {
                if (CommandLine.IsHelpRequest(arguments))
                {
                    PrintTrackedListHelp();
                    return WingetExitCodes.Success;
                }

                return ListTracked(arguments);
            }

            if (command is "upgrade" or "update" &&
                CommandLine.HasOption(arguments, "--tracked"))
            {
                if (CommandLine.IsHelpRequest(arguments))
                {
                    PrintTrackedUpgradeHelp();
                    return WingetExitCodes.Success;
                }

                var trackedArguments = CommandLine.RemoveOption(arguments, "--tracked");
                return UpgradeTracked(
                    trackedArguments,
                    addAutomationDefaults: false,
                    commandLabel: "--tracked");
            }

            if (CommandLine.IsHelpRequest(arguments))
            {
                var exitCode = _winget.Run(arguments);
                if (command is "install" or "add" or "upgrade" or "update" or "list" or "ls")
                {
                    PrintCommandExtensionHelp(command);
                }

                return exitCode;
            }

            return command switch
            {
                "install" or "add" or "import" or "configure" =>
                    RunInstallingCommand(arguments),
                "uninstall" or "remove" or "rm" =>
                    RunUninstallingCommand(arguments),
                _ => _winget.Run(arguments)
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"wingetx: 内部错误：{exception.Message}");
            return WingetExitCodes.InternalError;
        }
    }

    private static int RunInstallingCommand(IReadOnlyList<string> arguments)
    {
        using var lease = _store.AcquireLock(TimeSpan.FromSeconds(30));
        var document = _store.Load();
        var before = _winget.TryExportInstalled();
        var nativeExitCode = _winget.Run(arguments);
        var after = _winget.TryExportInstalled();
        var addedCount = 0;

        if (before is not null && after is not null)
        {
            var beforeKeys = before.Select(item => item.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in after.Where(item => !beforeKeys.Contains(item.Key)))
            {
                addedCount += Upsert(
                    document,
                    item.PackageIdentifier,
                    item.Source,
                    null,
                    null,
                    arguments[0].ToLowerInvariant());
            }
        }

        if (nativeExitCode == WingetExitCodes.Success)
        {
            addedCount += TrackExplicitSuccessfulInstall(document, arguments);
        }

        if (addedCount > 0)
        {
            try
            {
                _store.Save(document);
                Console.WriteLine("wingetx: 已根据本次安装结果更新受管清单。");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"wingetx: 安装已执行，但写入受管清单失败：{exception.Message}");
                return nativeExitCode == WingetExitCodes.Success
                    ? WingetExitCodes.InternalError
                    : nativeExitCode;
            }
        }
        else if (nativeExitCode == WingetExitCodes.Success &&
                 (arguments[0].Equals("install", StringComparison.OrdinalIgnoreCase) ||
                  arguments[0].Equals("add", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine(
                "wingetx: 警告：安装成功，但无法可靠解析程序包 ID；请用 “wingetx track <ID>” 手动纳入管理。");
        }

        return nativeExitCode;
    }

    private static int TrackExplicitSuccessfulInstall(
        TrackingDocument document,
        IReadOnlyList<string> arguments)
    {
        var command = arguments[0].ToLowerInvariant();
        var source = CommandLine.GetSingleOptionValue(arguments, "-s", "--source");
        var architecture = CommandLine.GetSingleOptionValue(arguments, "-a", "--architecture");
        var scope = CommandLine.GetSingleOptionValue(arguments, "--scope");
        var addedCount = 0;

        if (command is "import")
        {
            var importPath = CommandLine.GetImportPath(arguments);
            if (!string.IsNullOrWhiteSpace(importPath) && File.Exists(importPath))
            {
                try
                {
                    foreach (var item in WingetRunner.ParseImportFile(importPath))
                    {
                        addedCount += Upsert(
                            document,
                            item.PackageIdentifier,
                            item.Source,
                            null,
                            null,
                            "import");
                    }
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(
                        $"wingetx: 警告：导入成功，但读取导入清单失败：{exception.Message}");
                }
            }

            return addedCount;
        }

        if (command is not ("install" or "add") ||
            CommandLine.HasOption(arguments, "--dependencies-only"))
        {
            return addedCount;
        }

        foreach (var id in CommandLine.GetOptionValues(arguments, "--id"))
        {
            addedCount += Upsert(document, id, source, architecture, scope, "install");
        }

        foreach (var query in CommandLine.ExtractQueries(arguments))
        {
            if (_winget.IsInstalledExactId(query, source))
            {
                addedCount += Upsert(document, query, source, architecture, scope, "install");
            }
        }

        return addedCount;
    }

    private static int RunUninstallingCommand(IReadOnlyList<string> arguments)
    {
        using var lease = _store.AcquireLock(TimeSpan.FromSeconds(30));
        var document = _store.Load();
        var before = _winget.TryExportInstalled();
        var nativeExitCode = _winget.Run(arguments);
        var after = _winget.TryExportInstalled();
        var idsToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (before is not null && after is not null)
        {
            var afterKeys = after.Select(item => item.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var removed in before.Where(item => !afterKeys.Contains(item.Key)))
            {
                idsToRemove.Add(removed.PackageIdentifier);
            }
        }

        if (nativeExitCode == WingetExitCodes.Success)
        {
            foreach (var id in CommandLine.GetOptionValues(arguments, "--id"))
            {
                idsToRemove.Add(id);
            }

            var trackedIds = document.Packages
                .Select(package => package.PackageIdentifier)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var query in CommandLine.ExtractQueries(arguments))
            {
                if (trackedIds.Contains(query))
                {
                    idsToRemove.Add(query);
                }
            }
        }

        var removedCount = document.Packages.RemoveAll(package =>
            idsToRemove.Contains(package.PackageIdentifier));
        if (removedCount > 0)
        {
            try
            {
                _store.Save(document);
                Console.WriteLine($"wingetx: 已从受管清单移除 {removedCount} 个条目。");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"wingetx: 卸载已执行，但更新受管清单失败：{exception.Message}");
                return nativeExitCode == WingetExitCodes.Success
                    ? WingetExitCodes.InternalError
                    : nativeExitCode;
            }
        }

        return nativeExitCode;
    }

    private static int UpgradeTracked(
        IReadOnlyList<string> originalArguments,
        bool addAutomationDefaults,
        string commandLabel)
    {
        if (CommandLine.HasTrackedUpgradeConflict(originalArguments))
        {
            return InvalidArguments(
                $"“{commandLabel}”不能与查询、--id、--name、--manifest、--source、--scope、--architecture 或 --all 同时使用。");
        }

        using var lease = _store.AcquireLock(TimeSpan.FromSeconds(30));
        var document = _store.Load();
        if (document.Packages.Count == 0)
        {
            Console.WriteLine("wingetx: 受管清单为空。");
            return WingetExitCodes.Success;
        }

        var forwardedOptions = originalArguments.Skip(1).ToList();
        if (addAutomationDefaults)
        {
            AddDefaultSwitch(forwardedOptions, "--accept-source-agreements");
            AddDefaultSwitch(forwardedOptions, "--accept-package-agreements");
            AddDefaultSwitch(forwardedOptions, "--disable-interactivity");
        }
        var currentCount = 0;
        var unavailableCount = 0;
        var successfulCount = 0;
        var failureCount = 0;

        foreach (var package in document.Packages)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"wingetx: 正在处理 {package.PackageIdentifier}" +
                $"{FormatQualifier(package.Architecture, package.Scope)}");

            var nativeArguments = new List<string>
            {
                "upgrade",
                "--id",
                package.PackageIdentifier,
                "--exact"
            };

            AddOption(nativeArguments, "--source", package.Source);
            AddOption(nativeArguments, "--architecture", package.Architecture);
            AddOption(nativeArguments, "--scope", package.Scope);
            nativeArguments.AddRange(forwardedOptions);

            var exitCode = _winget.Run(nativeArguments);
            switch (exitCode)
            {
                case WingetExitCodes.Success:
                    successfulCount++;
                    package.LastSeenAtUtc = DateTimeOffset.UtcNow;
                    break;
                case WingetExitCodes.UpdateNotApplicable:
                case WingetExitCodes.UpgradeVersionNotNewer:
                    currentCount++;
                    package.LastSeenAtUtc = DateTimeOffset.UtcNow;
                    break;
                case WingetExitCodes.NoApplicationsFound:
                case WingetExitCodes.NoManifestFound:
                    unavailableCount++;
                    Console.Error.WriteLine(
                        $"wingetx: 跳过 {package.PackageIdentifier}：当前来源无法匹配该程序包。");
                    break;
                default:
                    failureCount++;
                    Console.Error.WriteLine(
                        $"wingetx: {package.PackageIdentifier} 失败，退出码 {FormatExitCode(exitCode)}。");
                    break;
            }
        }

        try
        {
            _store.Save(document);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"wingetx: 更新清单状态失败：{exception.Message}");
            return WingetExitCodes.InternalError;
        }

        Console.WriteLine();
        Console.WriteLine(
            $"wingetx: 完成。成功 {successfulCount}，已是最新 {currentCount}，来源不可用 {unavailableCount}，失败 {failureCount}。");

        return failureCount > 0
            ? WingetExitCodes.UpdateAllHasFailure
            : WingetExitCodes.Success;
    }

    private static int ListTracked(IReadOnlyList<string> arguments)
    {
        var remaining = CommandLine.RemoveOption(arguments, "--tracked");
        if (remaining.Count != 1)
        {
            return InvalidArguments("“list --tracked”当前不接受其他筛选参数。");
        }

        using var lease = _store.AcquireLock(TimeSpan.FromSeconds(30));
        var document = _store.Load();
        if (document.Packages.Count == 0)
        {
            Console.WriteLine("受管清单为空。");
            Console.WriteLine($"清单位置：{_store.DataPath}");
            return WingetExitCodes.Success;
        }

        var rows = document.Packages.Select(package => new[]
        {
            package.PackageIdentifier,
            package.Source ?? "-",
            package.Architecture ?? "-",
            package.Scope ?? "-",
            package.AddedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        }).ToList();

        var headers = new[] { "程序包 ID", "源", "架构", "范围", "纳入时间" };
        var widths = Enumerable.Range(0, headers.Length)
            .Select(index => Math.Max(
                DisplayWidth(headers[index]),
                rows.Max(row => DisplayWidth(row[index]))))
            .ToArray();

        PrintRow(headers, widths);
        Console.WriteLine(string.Join("  ", widths.Select(width => new string('-', width))));
        foreach (var row in rows)
        {
            PrintRow(row, widths);
        }

        Console.WriteLine();
        Console.WriteLine($"清单位置：{_store.DataPath}");
        return WingetExitCodes.Success;
    }

    private static int Track(IReadOnlyList<string> arguments)
    {
        if (CommandLine.IsHelpRequest(arguments))
        {
            PrintTrackHelp();
            return WingetExitCodes.Success;
        }

        if (!CommandLine.ValidateCustomCommandOptions(arguments, out var optionError))
        {
            return InvalidArguments(optionError!);
        }

        var ids = CommandLine.ExtractQueries(arguments)
            .Concat(CommandLine.GetOptionValues(arguments, "--id"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
        {
            return InvalidArguments("请至少提供一个程序包 ID。");
        }

        var source = CommandLine.GetSingleOptionValue(arguments, "-s", "--source");
        var architecture = CommandLine.GetSingleOptionValue(arguments, "-a", "--architecture");
        var scope = CommandLine.GetSingleOptionValue(arguments, "--scope");
        if (!ValidateQualifiers(architecture, scope))
        {
            return WingetExitCodes.InvalidArguments;
        }

        using var lease = _store.AcquireLock(TimeSpan.FromSeconds(30));
        var document = _store.Load();
        var changed = ids.Sum(id =>
            Upsert(document, id, source, architecture, scope, "manual"));
        if (changed > 0)
        {
            _store.Save(document);
        }

        Console.WriteLine($"wingetx: 已纳入 {ids.Count} 个程序包；新增或更新 {changed} 个条目。");
        return WingetExitCodes.Success;
    }

    private static int Untrack(IReadOnlyList<string> arguments)
    {
        if (CommandLine.IsHelpRequest(arguments))
        {
            PrintUntrackHelp();
            return WingetExitCodes.Success;
        }

        if (!CommandLine.ValidateCustomCommandOptions(arguments, out var optionError))
        {
            return InvalidArguments(optionError!);
        }

        var ids = CommandLine.ExtractQueries(arguments)
            .Concat(CommandLine.GetOptionValues(arguments, "--id"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0)
        {
            return InvalidArguments("请至少提供一个程序包 ID。");
        }

        var source = CommandLine.GetSingleOptionValue(arguments, "-s", "--source");
        var architecture = CommandLine.GetSingleOptionValue(arguments, "-a", "--architecture");
        var scope = CommandLine.GetSingleOptionValue(arguments, "--scope");
        if (!ValidateQualifiers(architecture, scope))
        {
            return WingetExitCodes.InvalidArguments;
        }

        using var lease = _store.AcquireLock(TimeSpan.FromSeconds(30));
        var document = _store.Load();
        var removed = document.Packages.RemoveAll(package =>
            ids.Contains(package.PackageIdentifier) &&
            MatchesOptional(package.Source, source) &&
            MatchesOptional(package.Architecture, architecture) &&
            MatchesOptional(package.Scope, scope));

        if (removed == 0)
        {
            Console.Error.WriteLine("wingetx: 受管清单中没有匹配的条目。");
            return WingetExitCodes.NoApplicationsFound;
        }

        _store.Save(document);
        Console.WriteLine($"wingetx: 已移除 {removed} 个受管条目；软件本身未被卸载。");
        return WingetExitCodes.Success;
    }

    private static int Upsert(
        TrackingDocument document,
        string packageIdentifier,
        string? source,
        string? architecture,
        string? scope,
        string addedBy)
    {
        packageIdentifier = packageIdentifier.Trim();
        source = NormalizeOptional(source);
        architecture = NormalizeOptional(architecture)?.ToLowerInvariant();
        scope = NormalizeOptional(scope)?.ToLowerInvariant();
        if (packageIdentifier.Length == 0)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var existing = document.Packages.FirstOrDefault(package =>
            package.PackageIdentifier.Equals(packageIdentifier, StringComparison.OrdinalIgnoreCase) &&
            NullableEquals(package.Source, source) &&
            NullableEquals(package.Architecture, architecture) &&
            NullableEquals(package.Scope, scope));

        if (existing is not null)
        {
            existing.LastSeenAtUtc = now;
            return 0;
        }

        var genericExisting = document.Packages.FirstOrDefault(package =>
            package.PackageIdentifier.Equals(packageIdentifier, StringComparison.OrdinalIgnoreCase) &&
            (source is null ||
             package.Source is null ||
             NullableEquals(package.Source, source)) &&
            package.Architecture is null &&
            package.Scope is null);
        if (genericExisting is not null && (architecture is not null || scope is not null))
        {
            genericExisting.Source ??= source;
            genericExisting.Architecture = architecture;
            genericExisting.Scope = scope;
            genericExisting.LastSeenAtUtc = now;
            genericExisting.AddedBy = addedBy;
            return 1;
        }

        if (architecture is null && scope is null &&
            document.Packages.Any(package =>
                package.PackageIdentifier.Equals(packageIdentifier, StringComparison.OrdinalIgnoreCase) &&
                (source is null || NullableEquals(package.Source, source))))
        {
            foreach (var package in document.Packages.Where(package =>
                         package.PackageIdentifier.Equals(packageIdentifier, StringComparison.OrdinalIgnoreCase) &&
                         (source is null || NullableEquals(package.Source, source))))
            {
                package.LastSeenAtUtc = now;
            }

            return 0;
        }

        document.Packages.Add(new TrackedPackage
        {
            PackageIdentifier = packageIdentifier,
            Source = source,
            Architecture = architecture,
            Scope = scope,
            AddedAtUtc = now,
            LastSeenAtUtc = now,
            AddedBy = addedBy
        });
        return 1;
    }

    private static bool ValidateQualifiers(string? architecture, string? scope)
    {
        if (!string.IsNullOrWhiteSpace(architecture) &&
            !ValidArchitectures.Contains(architecture))
        {
            InvalidArguments("架构必须是 x86、x64、arm 或 arm64。");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(scope) && !ValidScopes.Contains(scope))
        {
            InvalidArguments("范围必须是 user 或 machine。");
            return false;
        }

        return true;
    }

    private static int InvalidArguments(string message)
    {
        Console.Error.WriteLine($"wingetx: {message}");
        return WingetExitCodes.InvalidArguments;
    }

    private static void AddOption(List<string> arguments, string option, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            arguments.Add(option);
            arguments.Add(value);
        }
    }

    private static void AddDefaultSwitch(List<string> arguments, string option)
    {
        if (!CommandLine.HasOption(arguments, option))
        {
            arguments.Add(option);
        }
    }

    private static bool NullableEquals(string? left, string? right) =>
        string.Equals(
            NormalizeOptional(left),
            NormalizeOptional(right),
            StringComparison.OrdinalIgnoreCase);

    private static bool MatchesOptional(string? actual, string? filter) =>
        string.IsNullOrWhiteSpace(filter) || NullableEquals(actual, filter);

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FormatQualifier(string? architecture, string? scope)
    {
        var values = new[] { architecture, scope }
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var joined = string.Join(", ", values);
        return joined.Length == 0 ? string.Empty : $" [{joined}]";
    }

    private static string FormatExitCode(int exitCode) =>
        $"{exitCode} (0x{unchecked((uint)exitCode):X8})";

    private static int DisplayWidth(string value) =>
        value.Sum(character => character > 0xFF ? 2 : 1);

    private static void PrintRow(IReadOnlyList<string> values, IReadOnlyList<int> widths)
    {
        var cells = values.Select((value, index) =>
            value + new string(' ', widths[index] - DisplayWidth(value)));
        Console.WriteLine(string.Join("  ", cells));
    }

    private static void PrintExtensionHelp()
    {
        Console.WriteLine();
        Console.WriteLine("wingetx 扩展：");
        Console.WriteLine("  wingetx install <查询...>       按 winget 语法安装，并自动记录");
        Console.WriteLine("  wingetx upall                  更新受管清单中的全部程序包");
        Console.WriteLine("  wingetx upgrade --tracked      更新受管清单并透传其余选项");
        Console.WriteLine("  wingetx list --tracked         显示受管清单");
        Console.WriteLine("  wingetx track <ID...>          纳入已有程序包");
        Console.WriteLine("  wingetx untrack <ID...>        取消管理但不卸载");
    }

    private static void PrintCommandExtensionHelp(string command)
    {
        Console.WriteLine();
        if (command is "install" or "add")
        {
            Console.WriteLine("wingetx: 安装成功后会自动写入受管清单。");
        }
        else if (command is "upgrade" or "update")
        {
            Console.WriteLine("wingetx 扩展选项：--tracked  更新受管清单并透传其余选项");
        }
        else if (command is "list" or "ls")
        {
            Console.WriteLine("wingetx 扩展选项：--tracked  显示受管清单");
        }
    }

    private static void PrintTrackHelp()
    {
        Console.WriteLine("用法: wingetx track <程序包 ID...> [选项]");
        Console.WriteLine();
        Console.WriteLine("选项:");
        Console.WriteLine("  --id <程序包 ID>");
        Console.WriteLine("  -s,--source <源>");
        Console.WriteLine("  -a,--architecture <x86|x64|arm|arm64>");
        Console.WriteLine("  --scope <user|machine>");
    }

    private static void PrintUntrackHelp()
    {
        Console.WriteLine("用法: wingetx untrack <程序包 ID...> [选项]");
        Console.WriteLine("未指定架构或范围时，会移除该 ID 的所有受管条目；不会卸载软件。");
    }

    private static void PrintUpallHelp()
    {
        Console.WriteLine("用法: wingetx upall [winget upgrade 选项]");
        Console.WriteLine();
        Console.WriteLine("逐项升级受管清单，并自动接受协议、禁用交互式提示。");
        Console.WriteLine("不能与查询、选择器、--all、--source、--architecture 或 --scope 同时使用。");
    }

    private static void PrintTrackedUpgradeHelp()
    {
        Console.WriteLine("用法: wingetx upgrade --tracked [winget upgrade 选项]");
        Console.WriteLine();
        Console.WriteLine("逐项升级受管清单；--tracked 会被移除，其余选项原样传给每次 winget upgrade。");
        Console.WriteLine("不会自动接受协议，也不会自动禁用交互。");
    }

    private static void PrintTrackedListHelp()
    {
        Console.WriteLine("用法: wingetx list --tracked");
        Console.WriteLine("显示 wingetx 的受管程序包清单，不扫描全机软件。");
    }

}
