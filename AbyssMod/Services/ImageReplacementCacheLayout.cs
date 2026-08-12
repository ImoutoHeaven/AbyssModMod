using System;
using System.IO;

namespace AbyssMod.Services;

/// <summary>
/// Owns the active/pending replacement snapshots. Publishing never mutates active; only the
/// next startup promotion can switch the snapshot consumed by ImageReplacementManager.
/// </summary>
public sealed class ImageReplacementCacheLayout
{
    private const string ReadyMarker = ".ready";
    private readonly string _root;
    private readonly string _backupRoot;

    public ImageReplacementCacheLayout(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Replacement cache root is required.", nameof(root));

        _root = Path.GetFullPath(root);
        ActiveRoot = Path.Combine(_root, "active");
        PendingRoot = Path.Combine(_root, "pending");
        _backupRoot = Path.Combine(_root, ".active-backup");
        Directory.CreateDirectory(_root);
    }

    public string ActiveRoot { get; }

    public string PendingRoot { get; }

    public string CreateStagingRoot()
    {
        string staging = Path.Combine(_root, $".sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        return staging;
    }

    public void PublishPending(string stagingRoot)
    {
        string staging = ValidateDirectChild(stagingRoot, ".sync-");
        if (!Directory.Exists(staging))
            throw new DirectoryNotFoundException(staging);

        File.WriteAllText(Path.Combine(staging, ReadyMarker), "1");
        if (Directory.Exists(PendingRoot))
            Directory.Delete(PendingRoot, recursive: true);
        Directory.Move(staging, PendingRoot);
    }

    public bool PromotePendingIfReady()
    {
        RecoverInterruptedPromotion();
        if (
            !Directory.Exists(PendingRoot)
            || !File.Exists(Path.Combine(PendingRoot, ReadyMarker))
        )
            return false;

        if (Directory.Exists(_backupRoot))
            Directory.Delete(_backupRoot, recursive: true);
        if (Directory.Exists(ActiveRoot))
            Directory.Move(ActiveRoot, _backupRoot);

        try
        {
            Directory.Move(PendingRoot, ActiveRoot);
            string activeMarker = Path.Combine(ActiveRoot, ReadyMarker);
            if (File.Exists(activeMarker))
                File.Delete(activeMarker);
            if (Directory.Exists(_backupRoot))
                Directory.Delete(_backupRoot, recursive: true);
            return true;
        }
        catch
        {
            if (!Directory.Exists(ActiveRoot) && Directory.Exists(_backupRoot))
                Directory.Move(_backupRoot, ActiveRoot);
            throw;
        }
    }

    private void RecoverInterruptedPromotion()
    {
        if (!Directory.Exists(_backupRoot))
            return;

        if (Directory.Exists(ActiveRoot))
            Directory.Delete(_backupRoot, recursive: true);
        else
            Directory.Move(_backupRoot, ActiveRoot);
    }

    private string ValidateDirectChild(string path, string requiredPrefix)
    {
        string fullPath = Path.GetFullPath(path);
        string parent = Path.GetDirectoryName(fullPath);
        string name = Path.GetFileName(fullPath);
        if (
            !string.Equals(parent, _root, StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith(requiredPrefix, StringComparison.Ordinal)
        )
            throw new InvalidDataException("Staging directory is outside the replacement cache.");
        return fullPath;
    }
}
