using System.Text.Json;
using System.Diagnostics;

const int noApplicationsFound = unchecked((int)0x8A150014);
const int updateNotApplicable = unchecked((int)0x8A15002B);
const int noManifestFound = unchecked((int)0x8A150017);
const int upgradeVersionNotNewer = unchecked((int)0x8A15004F);
const int commandFailed = unchecked((int)0x8A150003);
const int multipleInstallFailed = unchecked((int)0x8A150065);

var statePath = Environment.GetEnvironmentVariable("FAKE_WINGET_STATE")
    ?? throw new InvalidOperationException("FAKE_WINGET_STATE is required.");
var logPath = Environment.GetEnvironmentVariable("FAKE_WINGET_LOG");
if (!string.IsNullOrWhiteSpace(logPath))
{
    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
    File.AppendAllText(logPath, string.Join('\t', args) + Environment.NewLine);
}

// The module-probe test pauses only this fake child; no real installer is started.
if (int.TryParse(Environment.GetEnvironmentVariable("FAKE_WINGET_WAIT_MS"), out var waitMilliseconds) &&
    waitMilliseconds > 0)
{
    var releasePath = Environment.GetEnvironmentVariable("FAKE_WINGET_RELEASE_FILE");
    var stopwatch = Stopwatch.StartNew();
    waitMilliseconds = Math.Min(waitMilliseconds, 30_000);
    while (stopwatch.ElapsedMilliseconds < waitMilliseconds &&
           (string.IsNullOrWhiteSpace(releasePath) || !File.Exists(releasePath)))
    {
        Thread.Sleep(25);
    }

    if (!string.IsNullOrWhiteSpace(releasePath) && !File.Exists(releasePath))
    {
        Console.Error.WriteLine("Fake winget timed out waiting for its test release file.");
        return commandFailed;
    }
}

var installed = File.Exists(statePath)
    ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(statePath)) ?? []
    : [];

if (args.Length == 0)
{
    Console.WriteLine("fake winget help");
    return 0;
}

var command = args[0].ToLowerInvariant();
switch (command)
{
    case "--version":
        Console.WriteLine("vFAKE");
        return 0;

    case "export":
    {
        var output = GetOption(args, "-o", "--output");
        if (string.IsNullOrWhiteSpace(output))
        {
            return commandFailed;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var document = new
        {
            Sources = new[]
            {
                new
                {
                    Packages = installed.Select(id => new { PackageIdentifier = id }).ToArray(),
                    SourceDetails = new { Name = "winget", Identifier = "fake-source" }
                }
            },
            WinGetVersion = "vFAKE"
        };
        File.WriteAllText(output, JsonSerializer.Serialize(document));
        return 0;
    }

    case "list":
    {
        var id = GetOption(args, "--id");
        return id is not null && installed.Contains(id, StringComparer.OrdinalIgnoreCase)
            ? 0
            : noApplicationsFound;
    }

    case "install":
    case "add":
    {
        var targets = GetTargets(args);
        if (targets.Any(target => target.Equals("Partial.Fail", StringComparison.OrdinalIgnoreCase)))
        {
            AddInstalled(installed, "Good.Package");
            SaveState(statePath, installed);
            return multipleInstallFailed;
        }

        foreach (var target in targets)
        {
            var resolved = target.Equals("FriendlyName", StringComparison.OrdinalIgnoreCase)
                ? "Vendor.Friendly"
                : target;
            AddInstalled(installed, resolved);
        }

        SaveState(statePath, installed);
        return 0;
    }

    case "uninstall":
    case "remove":
    case "rm":
    {
        var targets = GetTargets(args);
        installed.RemoveAll(id => targets.Contains(id, StringComparer.OrdinalIgnoreCase));
        SaveState(statePath, installed);
        return 0;
    }

    case "upgrade":
    {
        var id = GetOption(args, "--id") ?? GetTargets(args).FirstOrDefault();
        if (id is null)
        {
            return 0;
        }

        if (id.Equals("Current.Package", StringComparison.OrdinalIgnoreCase))
        {
            return updateNotApplicable;
        }

        if (id.Equals("Missing.Package", StringComparison.OrdinalIgnoreCase))
        {
            return noApplicationsFound;
        }

        if (id.Equals("NoManifest.Package", StringComparison.OrdinalIgnoreCase))
        {
            return noManifestFound;
        }

        if (id.Equals("Newer.Package", StringComparison.OrdinalIgnoreCase))
        {
            return upgradeVersionNotNewer;
        }

        if (id.Equals("Fail.Package", StringComparison.OrdinalIgnoreCase))
        {
            return commandFailed;
        }

        return 0;
    }

    default:
        Console.WriteLine(string.Join(' ', args));
        return 0;
}

static string? GetOption(IReadOnlyList<string> values, params string[] names)
{
    for (var index = 1; index < values.Count; index++)
    {
        foreach (var name in names)
        {
            if (values[index].Equals(name, StringComparison.OrdinalIgnoreCase) &&
                index + 1 < values.Count)
            {
                return values[index + 1];
            }

            if (values[index].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return values[index][(name.Length + 1)..];
            }
        }
    }

    return null;
}

static List<string> GetTargets(IReadOnlyList<string> values)
{
    var id = GetOption(values, "--id");
    if (id is not null)
    {
        return [id];
    }

    var optionsWithValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "--source", "-s", "--architecture", "-a", "--scope", "--id",
        "--version", "-v", "--log", "-o"
    };
    var targets = new List<string>();
    for (var index = 1; index < values.Count; index++)
    {
        if (values[index].StartsWith('-'))
        {
            var name = values[index].Split('=', 2)[0];
            if (optionsWithValues.Contains(name) && !values[index].Contains('='))
            {
                index++;
            }

            continue;
        }

        targets.Add(values[index]);
    }

    return targets;
}

static void AddInstalled(List<string> installed, string id)
{
    if (!installed.Contains(id, StringComparer.OrdinalIgnoreCase))
    {
        installed.Add(id);
    }
}

static void SaveState(string path, List<string> installed)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    File.WriteAllText(path, JsonSerializer.Serialize(installed));
}
