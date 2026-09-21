using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SVoice.Setup;

/// <summary>
/// Installs the XTTS runtime (embedded Python + dependency packs) described by
/// <c>manifest.json</c>. Archives are taken from <c>--source</c> when present
/// (offline installer) or downloaded from the manifest's base URL into the
/// user's download cache. Every archive is verified by SHA-256 before use.
/// </summary>
internal static class RuntimeInstaller
{
    private sealed record Archive(string Name, string Version, string FileName, string Sha256, long Size, string[] Backends);

    public static async Task<CommandResult> RunAsync(Options options)
    {
        var manifestPath = Path.GetFullPath(options.Require("manifest"));
        var target = Path.GetFullPath(options.Require("target"));
        var source = options.Get("source") is { } sourceOption ? Path.GetFullPath(sourceOption) : null;
        var cache = options.Get("cache") ?? Path.Combine(Program.LocalAppData, "SVoice", "Downloads");
        var requested = options.GetAll("pack").ToList();
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject()
            ?? throw new InvalidOperationException("manifest.json inválido.");

        var archives = new List<Archive>();
        var python = manifest["python"]?.AsObject() ?? throw new InvalidOperationException("manifest.json sem a seção python.");
        archives.Add(new Archive("python", python["version"]!.GetValue<string>(), python["archive"]!.GetValue<string>(), python["sha256"]!.GetValue<string>(), python["size"]!.GetValue<long>(), Array.Empty<string>()));
        var packs = manifest["packs"]?.AsObject() ?? throw new InvalidOperationException("manifest.json sem packs.");
        if (requested.Count == 0)
        {
            requested = new List<string> { "base", "torch-cpu" };
        }

        if (!requested.Contains("base"))
        {
            requested.Insert(0, "base");
        }

        foreach (var name in requested.Distinct())
        {
            if (packs[name] is not JsonObject pack)
            {
                return CommandResult.Fail($"O pack '{name}' não existe no manifesto.");
            }

            archives.Add(new Archive(
                name,
                pack["version"]!.GetValue<string>(),
                pack["archive"]!.GetValue<string>(),
                pack["sha256"]!.GetValue<string>(),
                pack["size"]!.GetValue<long>(),
                pack["backends"]?.AsArray().Select(item => item!.GetValue<string>()).ToArray() ?? Array.Empty<string>()));
        }

        var baseUrl = manifest["download_base_url"]?.GetValue<string>();
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(cache);
        var installedPath = Path.Combine(target, "installed.json");
        var installed = File.Exists(installedPath) ? JsonNode.Parse(File.ReadAllText(installedPath))?.AsObject() ?? new JsonObject() : new JsonObject();
        var installedPacks = installed["packs"]?.AsObject() ?? new JsonObject();
        installed["packs"] = installedPacks;

        var totalBytes = archives.Sum(archive => archive.Size);
        var needed = archives.Where(archive => !IsInstalled(target, archive, installedPacks)).Sum(archive => archive.Size * 3);
        if (Checks.FreeBytes(target) < needed + 512L * 1024 * 1024)
        {
            return CommandResult.Fail($"Espaço em disco insuficiente para instalar o runtime ({needed / (1024 * 1024 * 1024.0):0.0} GB necessários).");
        }

        var result = CommandResult.Success("Runtime XTTS instalado.");
        long doneBytes = 0;
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SVoice-Setup/2.0");

        foreach (var archive in archives)
        {
            if (IsInstalled(target, archive, installedPacks))
            {
                result.WithLine($"{archive.Name} {archive.Version}: já instalado");
                doneBytes += archive.Size;
                continue;
            }

            var localFile = await ObtainArchiveAsync(http, archive, source, cache, baseUrl, doneBytes, totalBytes);
            Status.Progress($"Extraindo {archive.Name} {archive.Version}…", (doneBytes + archive.Size * 0.8) / totalBytes);
            var destination = archive.Name == "python" ? Path.Combine(target, "python") : Path.Combine(target, "packs", archive.Name);
            ExtractAtomically(localFile, destination);
            installedPacks[archive.Name] = new JsonObject
            {
                ["version"] = archive.Version,
                ["sha256"] = archive.Sha256,
                ["installed_at"] = DateTimeOffset.Now.ToString("O"),
                ["backends"] = new JsonArray(archive.Backends.Select(item => (JsonNode?)item).ToArray()),
            };
            installed["runtime_version"] = manifest["runtime_version"]?.DeepClone();
            File.WriteAllText(installedPath, installed.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Copy(manifestPath, Path.Combine(target, "manifest.json"), overwrite: true);
            result.WithLine($"{archive.Name} {archive.Version}: instalado");
            doneBytes += archive.Size;
            Log.Write($"Runtime pack installed: {archive.Name} {archive.Version}");
        }

        var summary = new JsonObject();
        foreach (var (key, value) in installedPacks)
        {
            summary[key] = value?["version"]?.DeepClone();
        }

        return result.With("installed", summary);
    }

    private static bool IsInstalled(string target, Archive archive, JsonObject installedPacks)
    {
        var record = installedPacks[archive.Name]?.AsObject();
        if (record == null || record["sha256"]?.GetValue<string>() != archive.Sha256)
        {
            return false;
        }

        var directory = archive.Name == "python" ? Path.Combine(target, "python") : Path.Combine(target, "packs", archive.Name);
        var marker = archive.Name == "python" ? Path.Combine(directory, "python.exe") : Path.Combine(directory, ".svoice-pack.json");
        return File.Exists(marker);
    }

    private static async Task<string> ObtainArchiveAsync(HttpClient http, Archive archive, string? source, string cache, string? baseUrl, long doneBytes, long totalBytes)
    {
        var candidates = new List<string>();
        if (source != null)
        {
            candidates.Add(Path.Combine(source, archive.FileName));
        }

        candidates.Add(Path.Combine(cache, archive.FileName));
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) && new FileInfo(candidate).Length == archive.Size)
            {
                Status.Progress($"Verificando {archive.FileName}…", doneBytes / (double)totalBytes);
                if (await Sha256Async(candidate) == archive.Sha256)
                {
                    return candidate;
                }

                Log.Write($"Hash mismatch for cached {candidate}; deleting.");
                if (candidate.StartsWith(cache, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(candidate);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new FileNotFoundException($"O arquivo {archive.FileName} não está disponível localmente e o manifesto não define uma URL de download.");
        }

        var url = baseUrl.TrimEnd('/') + "/" + archive.FileName;
        var destination = Path.Combine(cache, archive.FileName);
        var partial = destination + ".part";
        long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset >= archive.Size)
        {
            File.Delete(partial);
            offset = 0;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (offset > 0)
                {
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
                }

                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                var resuming = offset > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
                await using var input = await response.Content.ReadAsStreamAsync();
                await using var output = new FileStream(partial, resuming ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                if (!resuming)
                {
                    offset = 0;
                }

                var buffer = new byte[1 << 20];
                var lastReport = DateTime.UtcNow;
                int read;
                while ((read = await input.ReadAsync(buffer)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read));
                    offset += read;
                    if ((DateTime.UtcNow - lastReport).TotalSeconds >= 1)
                    {
                        lastReport = DateTime.UtcNow;
                        Status.Progress($"Baixando {archive.FileName} ({offset / (1024 * 1024)} / {archive.Size / (1024 * 1024)} MB)…",
                            (doneBytes + offset * 0.8) / totalBytes);
                    }
                }

                break;
            }
            catch (Exception exception) when (attempt < 4 && exception is HttpRequestException or IOException)
            {
                Log.Write($"Download attempt {attempt} for {archive.FileName} failed: {exception.Message}");
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt));
                offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            }
        }

        if (new FileInfo(partial).Length != archive.Size)
        {
            File.Delete(partial);
            throw new IOException($"O download de {archive.FileName} terminou com tamanho inesperado.");
        }

        Status.Progress($"Verificando {archive.FileName}…", (doneBytes + archive.Size * 0.8) / totalBytes);
        if (await Sha256Async(partial) != archive.Sha256)
        {
            File.Delete(partial);
            throw new IOException($"O arquivo {archive.FileName} falhou na verificação SHA-256 e foi descartado.");
        }

        File.Move(partial, destination, overwrite: true);
        return destination;
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void ExtractAtomically(string archivePath, string destination)
    {
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var staging = destination + ".partial";
        if (Directory.Exists(staging))
        {
            DeleteDirectoryWithRetry(staging);
        }

        ZipFile.ExtractToDirectory(archivePath, staging, overwriteFiles: true);
        if (Directory.Exists(destination))
        {
            var old = destination + ".old";
            if (Directory.Exists(old))
            {
                DeleteDirectoryWithRetry(old);
            }

            MoveDirectoryWithRetry(destination, old);
            MoveDirectoryWithRetry(staging, destination);
            DeleteDirectoryWithRetry(old);
        }
        else
        {
            MoveDirectoryWithRetry(staging, destination);
        }
    }

    private static void MoveDirectoryWithRetry(string source, string destination)
    {
        RetryFileSystemOperation(() => Directory.Move(source, destination), $"mover {source} para {destination}");
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        RetryFileSystemOperation(() => Directory.Delete(path, recursive: true), $"remover {path}");
    }

    private static void RetryFileSystemOperation(Action operation, string description)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                last = exception;
                Log.Write($"Falha transitória ao {description} (tentativa {attempt}/8): {exception.Message}");
                if (attempt < 8)
                {
                    Thread.Sleep(250 * attempt);
                }
            }
        }

        throw new IOException($"Não foi possível {description} após várias tentativas.", last);
    }
}
