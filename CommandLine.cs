namespace WingetX;

internal static class CommandLine
{
    private static readonly HashSet<string> OptionsWithValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "-q", "--query",
        "-m", "--manifest",
        "--id",
        "--name",
        "--moniker",
        "--product-code",
        "-v", "--version",
        "-s", "--source",
        "--scope",
        "-a", "--architecture",
        "--installer-type",
        "--locale",
        "-o", "--log",
        "--custom",
        "--override",
        "-l", "--location",
        "--dependency-source",
        "--header",
        "--authentication-mode",
        "--authentication-account",
        "-r", "--rename",
        "--proxy",
        "--import-file",
        "--output"
    };

    public static bool IsHelpRequest(IEnumerable<string> arguments) =>
        arguments.Any(argument =>
            argument.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
            argument.Equals("-?", StringComparison.OrdinalIgnoreCase));

    public static bool HasOption(IEnumerable<string> arguments, params string[] names)
    {
        foreach (var argument in arguments)
        {
            foreach (var name in names)
            {
                if (argument.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    argument.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static string? GetSingleOptionValue(IReadOnlyList<string> arguments, params string[] names) =>
        GetOptionValues(arguments, names).LastOrDefault();

    public static IReadOnlyList<string> GetOptionValues(
        IReadOnlyList<string> arguments,
        params string[] names)
    {
        var results = new List<string>();
        for (var index = 1; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            foreach (var name in names)
            {
                if (argument.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    if (index + 1 < arguments.Count)
                    {
                        results.Add(arguments[index + 1]);
                    }
                }
                else if (argument.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(argument[(name.Length + 1)..]);
                }
            }
        }

        return results;
    }

    public static IReadOnlyList<string> ExtractQueries(IReadOnlyList<string> arguments)
    {
        var queries = new List<string>();
        var importCommand =
            arguments.Count > 0 &&
            arguments[0].Equals("import", StringComparison.OrdinalIgnoreCase);

        for (var index = 1; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == "--")
            {
                for (index++; index < arguments.Count; index++)
                {
                    queries.Add(arguments[index]);
                }

                break;
            }

            var optionName = GetOptionName(argument);
            if (optionName is not null)
            {
                var consumesValue =
                    OptionsWithValues.Contains(optionName) ||
                    importCommand && optionName.Equals("-i", StringComparison.OrdinalIgnoreCase);
                if (consumesValue && !argument.Contains('='))
                {
                    index++;
                }

                continue;
            }

            queries.Add(argument);
        }

        return queries;
    }

    public static string? GetImportPath(IReadOnlyList<string> arguments)
    {
        var optionPath = GetSingleOptionValue(arguments, "-i", "--import-file");
        if (!string.IsNullOrWhiteSpace(optionPath))
        {
            return optionPath;
        }

        return ExtractQueries(arguments).FirstOrDefault();
    }

    public static IReadOnlyList<string> RemoveOption(
        IReadOnlyList<string> arguments,
        params string[] optionNames)
    {
        var output = new List<string>(arguments.Count);
        foreach (var argument in arguments)
        {
            if (!optionNames.Any(name =>
                    argument.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                output.Add(argument);
            }
        }

        return output;
    }

    public static bool HasTrackedUpgradeConflict(IReadOnlyList<string> arguments)
    {
        if (HasOption(
                arguments,
                "--all",
                "--recurse",
                "-r",
                "--id",
                "--name",
                "--moniker",
                "--manifest",
                "-m",
                "--query",
                "-q",
                "--source",
                "-s",
                "--architecture",
                "-a",
                "--scope"))
        {
            return true;
        }

        return ExtractQueries(arguments).Count > 0;
    }

    public static bool ValidateCustomCommandOptions(
        IReadOnlyList<string> arguments,
        out string? error)
    {
        var options = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["--id"] = true,
            ["-s"] = true,
            ["--source"] = true,
            ["-a"] = true,
            ["--architecture"] = true,
            ["--scope"] = true,
            ["-?"] = false,
            ["--help"] = false
        };

        for (var index = 1; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            var optionName = GetOptionName(argument);
            if (optionName is null)
            {
                continue;
            }

            if (!options.TryGetValue(optionName, out var requiresValue))
            {
                error = $"未知选项“{optionName}”。";
                return false;
            }

            if (!requiresValue)
            {
                continue;
            }

            if (argument.Contains('='))
            {
                if (argument.EndsWith('='))
                {
                    error = $"选项“{optionName}”缺少值。";
                    return false;
                }

                continue;
            }

            if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith('-'))
            {
                error = $"选项“{optionName}”缺少值。";
                return false;
            }

            index++;
        }

        error = null;
        return true;
    }

    private static string? GetOptionName(string argument)
    {
        if (!argument.StartsWith('-'))
        {
            return null;
        }

        var equalsIndex = argument.IndexOf('=');
        return equalsIndex > 0 ? argument[..equalsIndex] : argument;
    }
}
