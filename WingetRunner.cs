using System.Diagnostics;
using System.Text.Json;

namespace WingetX;

internal sealed class WingetRunner
{
    private readonly string _executable;

    public WingetRunner()
    {
        var overridePath = Environment.GetEnvironmentVariable("WINGETX_WINGET_PATH");
        _executable = string.IsNullOrWhiteSpace(overridePath) ? "winget.exe" : overridePath;
    }

    public int Run(IReadOnlyList<string> arguments)
    {
        using var process = CreateProcess(arguments, redirectOutput: false);
        process.Start();
        process.WaitForExit();
        return process.ExitCode;
    }

    public ProcessResult RunCaptured(IReadOnlyList<string> arguments)
    {
        using var process = CreateProcess(arguments, redirectOutput: true);
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(outputTask, errorTask);
        return new ProcessResult(process.ExitCode, outputTask.Result, errorTask.Result);
    }

    public IReadOnlyList<ExportedPackage>? TryExportInstalled()
    {
        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"wingetx-export-{Environment.ProcessId}-{Guid.NewGuid():N}.json");

        try
        {
            var result = RunCaptured([
                "export",
                "--output",
                temporaryPath,
                "--accept-source-agreements",
                "--disable-interactivity"
            ]);

            if (!File.Exists(temporaryPath))
            {
                return null;
            }

            using var stream = File.OpenRead(temporaryPath);
            using var json = JsonDocument.Parse(stream);
            var packages = new List<ExportedPackage>();

            if (!TryGetPropertyIgnoreCase(json.RootElement, "Sources", out var sources) ||
                sources.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var sourceElement in sources.EnumerateArray())
            {
                string? sourceName = null;
                if (TryGetPropertyIgnoreCase(sourceElement, "SourceDetails", out var sourceDetails) &&
                    sourceDetails.ValueKind == JsonValueKind.Object)
                {
                    sourceName =
                        GetStringIgnoreCase(sourceDetails, "Name") ??
                        GetStringIgnoreCase(sourceDetails, "Identifier");
                }

                if (!TryGetPropertyIgnoreCase(sourceElement, "Packages", out var packageElements) ||
                    packageElements.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var packageElement in packageElements.EnumerateArray())
                {
                    var id = GetStringIgnoreCase(packageElement, "PackageIdentifier");
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        packages.Add(new ExportedPackage(id.Trim(), sourceName));
                    }
                }
            }

            return packages;
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // A stale temporary export is harmless and should not mask the winget result.
            }
        }
    }

    public bool IsInstalledExactId(string packageIdentifier, string? source)
    {
        var arguments = new List<string>
        {
            "list",
            "--id",
            packageIdentifier,
            "--exact",
            "--accept-source-agreements",
            "--disable-interactivity"
        };

        if (!string.IsNullOrWhiteSpace(source))
        {
            arguments.Add("--source");
            arguments.Add(source);
        }

        return RunCaptured(arguments).ExitCode == WingetExitCodes.Success;
    }

    public static IReadOnlyList<ExportedPackage> ParseImportFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var json = JsonDocument.Parse(stream);
        var packages = new List<ExportedPackage>();

        if (!TryGetPropertyIgnoreCase(json.RootElement, "Sources", out var sources) ||
            sources.ValueKind != JsonValueKind.Array)
        {
            return packages;
        }

        foreach (var sourceElement in sources.EnumerateArray())
        {
            string? sourceName = null;
            if (TryGetPropertyIgnoreCase(sourceElement, "SourceDetails", out var sourceDetails) &&
                sourceDetails.ValueKind == JsonValueKind.Object)
            {
                sourceName =
                    GetStringIgnoreCase(sourceDetails, "Name") ??
                    GetStringIgnoreCase(sourceDetails, "Identifier");
            }

            if (!TryGetPropertyIgnoreCase(sourceElement, "Packages", out var packageElements) ||
                packageElements.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var packageElement in packageElements.EnumerateArray())
            {
                var id = GetStringIgnoreCase(packageElement, "PackageIdentifier");
                if (!string.IsNullOrWhiteSpace(id))
                {
                    packages.Add(new ExportedPackage(id.Trim(), sourceName));
                }
            }
        }

        return packages;
    }

    private Process CreateProcess(IReadOnlyList<string> arguments, bool redirectOutput)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executable,
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            RedirectStandardInput = false,
            CreateNoWindow = redirectOutput
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return new Process { StartInfo = startInfo };
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? GetStringIgnoreCase(JsonElement element, string propertyName) =>
        TryGetPropertyIgnoreCase(element, propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
