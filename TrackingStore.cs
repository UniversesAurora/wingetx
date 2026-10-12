using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WingetX;

internal sealed class TrackingStore
{
    private const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string DataDirectory { get; }
    public string DataPath { get; }
    public string BackupPath => DataPath + ".bak";
    private bool _loadedFromBackup;

    public TrackingStore()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("WINGETX_DATA_DIR");
        DataDirectory = string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "wingetx")
            : Path.GetFullPath(overrideDirectory);
        DataPath = Path.Combine(DataDirectory, "packages.json");
    }

    public IDisposable AcquireLock(TimeSpan timeout)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(DataPath.ToUpperInvariant())))[..20];
        var mutex = new Mutex(false, $@"Local\wingetx-{hash}");
        var acquired = false;

        try
        {
            try
            {
                acquired = mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                throw new TimeoutException("等待另一个 wingetx 进程释放清单锁超时。");
            }

            return new MutexLease(mutex);
        }
        catch
        {
            if (!acquired)
            {
                mutex.Dispose();
            }

            throw;
        }
    }

    public TrackingDocument Load()
    {
        if (!File.Exists(DataPath))
        {
            return new TrackingDocument();
        }

        try
        {
            _loadedFromBackup = false;
            return DeserializeAndValidate(DataPath);
        }
        catch (Exception primaryException) when (
            primaryException is not NotSupportedException &&
            File.Exists(BackupPath))
        {
            try
            {
                var restored = DeserializeAndValidate(BackupPath);
                _loadedFromBackup = true;
                Console.Error.WriteLine(
                    $"wingetx: 警告：主清单损坏，已从备份读取。原错误：{primaryException.Message}");
                return restored;
            }
            catch (Exception backupException)
            {
                throw new InvalidDataException(
                    $"主清单和备份均无法读取。主清单：{primaryException.Message}；备份：{backupException.Message}",
                    backupException);
            }
        }
    }

    public void Save(TrackingDocument document)
    {
        document.SchemaVersion = SupportedSchemaVersion;
        document.UpdatedAtUtc = DateTimeOffset.UtcNow;
        document.Packages = document.Packages
            .OrderBy(package => package.PackageIdentifier, StringComparer.OrdinalIgnoreCase)
            .ThenBy(package => package.Architecture, StringComparer.OrdinalIgnoreCase)
            .ThenBy(package => package.Scope, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Directory.CreateDirectory(DataDirectory);
        var temporaryPath = Path.Combine(
            DataDirectory,
            $".packages.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

        try
        {
            var json = JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine;
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(true);
            }

            if (File.Exists(DataPath))
            {
                if (_loadedFromBackup && File.Exists(BackupPath))
                {
                    var corruptPath = Path.Combine(
                        DataDirectory,
                        $"packages.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.json");
                    File.Move(DataPath, corruptPath);
                    File.Copy(BackupPath, DataPath, true);
                }

                try
                {
                    File.Replace(temporaryPath, DataPath, BackupPath, true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(DataPath, BackupPath, true);
                    File.Move(temporaryPath, DataPath, true);
                }
                catch (IOException)
                {
                    File.Copy(DataPath, BackupPath, true);
                    File.Move(temporaryPath, DataPath, true);
                }
            }
            else
            {
                File.Move(temporaryPath, DataPath);
            }

            _loadedFromBackup = false;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static TrackingDocument DeserializeAndValidate(string path)
    {
        var json = File.ReadAllText(path, Encoding.UTF8);
        var document = JsonSerializer.Deserialize<TrackingDocument>(json, JsonOptions)
            ?? throw new InvalidDataException("JSON 内容为空。");

        if (document.SchemaVersion != SupportedSchemaVersion)
        {
            throw new NotSupportedException(
                $"不支持清单架构版本 {document.SchemaVersion}；当前仅支持 {SupportedSchemaVersion}。");
        }

        document.Packages ??= [];
        foreach (var package in document.Packages)
        {
            if (string.IsNullOrWhiteSpace(package.PackageIdentifier))
            {
                throw new InvalidDataException("清单中存在空的 packageIdentifier。");
            }

            package.PackageIdentifier = package.PackageIdentifier.Trim();
            package.Source = NormalizeOptional(package.Source);
            package.Architecture = NormalizeOptional(package.Architecture);
            package.Scope = NormalizeOptional(package.Scope);
        }

        return document;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                mutex.ReleaseMutex();
            }
            finally
            {
                mutex.Dispose();
            }
        }
    }
}
