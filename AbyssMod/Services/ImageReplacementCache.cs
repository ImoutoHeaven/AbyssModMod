using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace AbyssMod.Services;

/// <summary>从翻译 CDN 同步带哈希校验的图片替换文件到本地缓存。</summary>
public sealed class ImageReplacementCache
{
    private const int SupportedManifestVersion = 1;
    private const long MaxManifestBytes = 1024 * 1024;
    private const long MaxImageBytes = 64L * 1024 * 1024;
    private const string RemoteDirectory = "replacements";
    private const string ReplacementManifestFile = "manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly string _cdn;
    private readonly string _language;
    private readonly ImageReplacementCacheLayout _layout;
    private readonly HttpClient _client;

    public ImageReplacementCache(
        string cdn,
        string language,
        ImageReplacementCacheLayout layout,
        HttpClient client
    )
    {
        _cdn = cdn.TrimEnd('/');
        _language = language;
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _client = client;
    }

    public async Task SyncAsync()
    {
        string stagingRoot = null;

        try
        {
            stagingRoot = _layout.CreateStagingRoot();
            Directory.CreateDirectory(_layout.ActiveRoot);

            Manifest mainManifest = await DownloadMainManifestAsync();
            var hashes = ValidateHashes(mainManifest?.Replacements);
            if (!hashes.TryGetValue(ReplacementManifestFile, out string manifestHash))
                throw new InvalidDataException(
                    $"main manifest has no hash for {RemoteDirectory}/{ReplacementManifestFile}"
                );

            byte[] replacementManifestBytes = await GetVerifiedFileAsync(
                ReplacementManifestFile,
                manifestHash,
                MaxManifestBytes
            );
            var replacementManifest = JsonSerializer.Deserialize<ImageReplacementManifest>(
                replacementManifestBytes,
                JsonOptions
            );
            if (replacementManifest == null)
                throw new InvalidDataException("replacement manifest root is null");
            if (replacementManifest.Version != SupportedManifestVersion)
                throw new InvalidDataException(
                    $"unsupported replacement manifest version {replacementManifest.Version}; "
                        + $"expected {SupportedManifestVersion}"
                );

            var requiredFiles = CollectRequiredFiles(replacementManifest);
            foreach (string relativeFile in requiredFiles)
                if (!hashes.ContainsKey(relativeFile))
                    throw new InvalidDataException(
                        $"main manifest has no hash for {RemoteDirectory}/{relativeFile}"
                    );

            int downloaded = 0;
            int cacheHits = 0;
            bool manifestChanged = !IsActiveFileValid(
                ReplacementManifestFile,
                manifestHash
            );
            bool snapshotChanged = manifestChanged;

            foreach (string relativeFile in requiredFiles)
            {
                if (IsActiveFileValid(relativeFile, hashes[relativeFile]))
                {
                    CopyActiveFileToStaging(stagingRoot, relativeFile);
                    cacheHits++;
                    continue;
                }

                byte[] bytes = await DownloadVerifiedFileAsync(
                    relativeFile,
                    hashes[relativeFile],
                    MaxImageBytes
                );
                await WriteStagedFileAsync(stagingRoot, relativeFile, bytes);
                downloaded++;
                snapshotChanged = true;
            }

            await WriteStagedFileAsync(
                stagingRoot,
                ReplacementManifestFile,
                replacementManifestBytes
            );
            if (!snapshotChanged)
            {
                cacheHits++;
                Logger.Info(
                    $"Image replacement cache is current: cacheHits={cacheHits}"
                );
                return;
            }

            _layout.PublishPending(stagingRoot);
            Logger.Info(
                "Image replacement update staged for next startup: "
                    + $"downloaded={downloaded + (manifestChanged ? 1 : 0)}, "
                    + $"cacheHits={cacheHits + (manifestChanged ? 0 : 1)}"
            );
        }
        catch (Exception e)
        {
            Logger.Warn(
                $"Image replacement background sync failed; active cache unchanged: {e.Message}"
            );
        }
        finally
        {
            try
            {
                if (!string.IsNullOrEmpty(stagingRoot) && Directory.Exists(stagingRoot))
                    Directory.Delete(stagingRoot, true);
            }
            catch (Exception e)
            {
                Logger.Warn($"Failed to clean image replacement staging directory: {e.Message}");
            }
        }
    }

    private async Task<Manifest> DownloadMainManifestAsync()
    {
        string url = TranslationPaths.BuildRemoteUrl(
            _cdn,
            TranslationPaths.Manifest,
            _language
        );
        url = AppendQueryParameter(
            url,
            "cacheBust",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()
        );
        byte[] bytes = await DownloadBytesAsync(url, MaxManifestBytes);
        return JsonSerializer.Deserialize<Manifest>(bytes, JsonOptions)
            ?? throw new InvalidDataException("remote main manifest root is null");
    }

    private Dictionary<string, string> ValidateHashes(Dictionary<string, string> source)
    {
        if (source == null || source.Count == 0)
            throw new InvalidDataException("main manifest has no replacement hashes");

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relativeFile, hash) in source)
        {
            string normalized;
            try
            {
                normalized = NormalizeRelativeFile(relativeFile, allowManifest: true);
            }
            catch (InvalidDataException e)
            {
                Logger.Warn(
                    $"Ignored unsupported replacement hash path '{relativeFile}': {e.Message}"
                );
                continue;
            }
            if (!IsMd5(hash))
                throw new InvalidDataException(
                    $"invalid MD5 for {RemoteDirectory}/{relativeFile}"
                );
            if (!result.TryAdd(normalized, hash.ToLowerInvariant()))
                throw new InvalidDataException(
                    $"duplicate replacement hash path: {relativeFile}"
                );
        }
        return result;
    }

    private static HashSet<string> CollectRequiredFiles(ImageReplacementManifest manifest)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (manifest.NovelBackgrounds != null)
            foreach (string file in manifest.NovelBackgrounds.Values)
                result.Add(NormalizeRelativeFile(file, allowManifest: false));

        if (manifest.SpriteNames != null)
            foreach (string file in manifest.SpriteNames.Values)
                result.Add(NormalizeRelativeFile(file, allowManifest: false));

        if (manifest.UiComponents != null)
            foreach (var rule in manifest.UiComponents)
                if (rule != null)
                    result.Add(NormalizeRelativeFile(rule.File, allowManifest: false));

        return result;
    }

    private async Task<byte[]> GetVerifiedFileAsync(
        string relativeFile,
        string expectedHash,
        long maxBytes
    )
    {
        string localPath = ResolveActivePath(relativeFile);
        if (IsActiveFileValid(relativeFile, expectedHash))
            return await File.ReadAllBytesAsync(localPath);
        return await DownloadVerifiedFileAsync(relativeFile, expectedHash, maxBytes);
    }

    private async Task<byte[]> DownloadVerifiedFileAsync(
        string relativeFile,
        string expectedHash,
        long maxBytes
    )
    {
        string url = BuildReplacementUrl(relativeFile, expectedHash);
        byte[] bytes = await DownloadBytesAsync(url, maxBytes);
        string actualHash = HashBytes(bytes);
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"hash mismatch for {RemoteDirectory}/{relativeFile}; "
                    + $"expected={expectedHash}, actual={actualHash}"
            );
        return bytes;
    }

    private async Task<byte[]> DownloadBytesAsync(string url, long maxBytes)
    {
        using var response = await _client.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead
        );
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"GET {url} returned {(int)response.StatusCode} {response.StatusCode}"
            );
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException($"download exceeds {maxBytes} bytes: {url}");

        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        if (bytes.LongLength > maxBytes)
            throw new InvalidDataException($"download exceeds {maxBytes} bytes: {url}");
        return bytes;
    }

    private bool IsActiveFileValid(string relativeFile, string expectedHash)
    {
        try
        {
            string localPath = ResolveActivePath(relativeFile);
            return File.Exists(localPath)
                && string.Equals(
                    HashFile(localPath),
                    expectedHash,
                    StringComparison.OrdinalIgnoreCase
                );
        }
        catch
        {
            return false;
        }
    }

    private void CopyActiveFileToStaging(string stagingRoot, string relativeFile)
    {
        string source = ResolveActivePath(relativeFile);
        string destination = ResolveUnderRoot(stagingRoot, relativeFile);
        string directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.Copy(source, destination, overwrite: true);
    }

    private async Task WriteStagedFileAsync(
        string stagingRoot,
        string relativeFile,
        byte[] bytes
    )
    {
        string stagedPath = ResolveUnderRoot(stagingRoot, relativeFile);
        string directory = Path.GetDirectoryName(stagedPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(stagedPath, bytes);
    }

    private string ResolveActivePath(string relativeFile) =>
        ResolveUnderRoot(_layout.ActiveRoot, relativeFile);

    private static string ResolveUnderRoot(string root, string relativeFile)
    {
        string fullRoot = Path.GetFullPath(root);
        string candidate = Path.GetFullPath(
            Path.Combine(fullRoot, relativeFile.Replace('/', Path.DirectorySeparatorChar))
        );
        string rootPrefix = fullRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            ) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"replacement file escapes cache root: {relativeFile}");
        return candidate;
    }

    private string BuildReplacementUrl(string relativeFile, string expectedHash)
    {
        string url =
            $"{_cdn}/{RemoteDirectory}/"
            + string.Join(
                "/",
                relativeFile.Split('/').Select(Uri.EscapeDataString)
            );
        return AppendQueryParameter(url, "hash", expectedHash);
    }

    private static string AppendQueryParameter(string url, string name, string value) =>
        url
        + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?")
        + Uri.EscapeDataString(name)
        + "="
        + Uri.EscapeDataString(value);

    private static string NormalizeRelativeFile(string relativeFile, bool allowManifest)
    {
        if (string.IsNullOrWhiteSpace(relativeFile))
            throw new InvalidDataException("replacement file path is empty");

        string normalized = relativeFile.Replace('\\', '/');
        if (
            normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Contains(':')
            || normalized.IndexOf('\0') >= 0
            || normalized.Split('/').Any(part => part is "" or "." or "..")
        )
            throw new InvalidDataException($"invalid replacement file path: {relativeFile}");

        string extension = Path.GetExtension(normalized);
        bool isManifest = string.Equals(
            normalized,
            ReplacementManifestFile,
            StringComparison.OrdinalIgnoreCase
        );
        if (isManifest && allowManifest)
            return ReplacementManifestFile;
        if (
            !string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase)
        )
            throw new InvalidDataException(
                $"unsupported replacement file extension: {relativeFile}"
            );
        return normalized;
    }

    private static bool IsMd5(string value) =>
        value != null
        && value.Length == 32
        && value.All(character =>
            character is >= '0' and <= '9'
            || character is >= 'a' and <= 'f'
            || character is >= 'A' and <= 'F'
        );

    private static string HashFile(string path) =>
        Convert.ToHexString(MD5.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string HashBytes(byte[] bytes) =>
        Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
}
