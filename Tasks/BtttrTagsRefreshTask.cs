using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.BtttrTags.Common;
using Jellyfin.Plugin.BtttrTags.Configuration;
using Jellyfin.Plugin.BtttrTags.Drawing;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BtttrTags.Tasks;

public class BtttrTagsRefreshTask : IScheduledTask
{
    private static readonly SemaphoreSlim _posterLock = new(1, 1);

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IApplicationPaths _applicationPaths;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BtttrTagsRefreshTask> _logger;

    public BtttrTagsRefreshTask(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IApplicationPaths applicationPaths,
        IHttpClientFactory httpClientFactory,
        ILogger<BtttrTagsRefreshTask> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _applicationPaths = applicationPaths;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string Name => "BtttrTags - Update Poster Overlays";
    public string Key => "BtttrTagsRefresh";
    public string Description => "Generates and updates dynamic hanging tags on your posters while protecting your original artwork.";
    public string Category => "BtttrTags";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4.5).Ticks
            }
        };
    }

    public string GetBackupDirectory()
    {
        string dir = Path.Combine(_applicationPaths.DataPath, "BtttrTagsBackups");
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return dir;
    }

    private static string ComputeFileHash(string filePath)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(filePath);
        byte[] hash = md5.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

    private static string ComputeBytesHash(byte[] bytes)
    {
        byte[] hash = MD5.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static string DetectImageMimeType(string filePath)
    {
        byte[] header = new byte[12];
        int read;
        using (var stream = File.OpenRead(filePath))
        {
            read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        }

        var bytes = header.AsSpan(0, read);
        if (bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 })) return "image/png";
        if (bytes.StartsWith("GIF8"u8)) return "image/gif";
        if (read >= 12 && bytes.StartsWith("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        return "image/jpeg";
    }

    private static void CopyFileAtomic(string sourcePath, string destinationPath)
    {
        string tempPath = destinationPath + ".tmp";
        File.Copy(sourcePath, tempPath, true);
        File.Move(tempPath, destinationPath, true);
    }

    private static PosterSidecar? ReadSidecar(string hashPath)
    {
        if (!File.Exists(hashPath)) return null;

        var lines = File.ReadAllLines(hashPath);
        string Line(int index) => lines.Length > index ? lines[index].Trim() : string.Empty;

        var sidecar = new PosterSidecar(Line(0), Line(1), Line(2), Line(3));
        return sidecar.Hash.Length == 0 && !sidecar.WasInterrupted ? null : sidecar;
    }

    private static void WriteSidecar(string hashPath, PosterSidecar sidecar)
    {
        string tempPath = hashPath + ".tmp";
        File.WriteAllLines(tempPath, new[] { sidecar.Hash, sidecar.Tag, sidecar.Gold, sidecar.PendingHash });
        File.Move(tempPath, hashPath, true);
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _posterLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await UpdatePostersAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _posterLock.Release();
        }
    }

    private async Task UpdatePostersAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("BtttrTags: Starting poster update task.");

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        string cacheDir = Path.Combine(_applicationPaths.DataPath, "BtttrTagsCache");
        var tagResolver = new TagResolver(_httpClientFactory, _logger, cacheDir);
        if (!await tagResolver.InitializeDataAsync(config, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("BtttrTags: Skipping this run because an enabled tag list could not be downloaded and has no saved copy.");
            progress?.Report(100);
            return;
        }

        string backupDir = GetBackupDirectory();

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[]
            {
                Jellyfin.Data.Enums.BaseItemKind.Movie,
                Jellyfin.Data.Enums.BaseItemKind.Series
            },
            IsVirtualItem = false,
            Recursive = true
        });

        int total = items.Count;
        if (total == 0)
        {
            progress?.Report(100);
            return;
        }

        int done = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                string backupPath = Path.Combine(backupDir, $"{item.Id:N}.jpg");
                string hashPath = Path.Combine(backupDir, $"{item.Id:N}.hash");
                var tag = tagResolver.GetTagForItem(item, config);

                if (tag != null)
                {
                    await ApplyTagAsync(item, tag, backupPath, hashPath).ConfigureAwait(false);
                }
                else if (File.Exists(backupPath))
                {
                    await RestoreOrKeepPosterAsync(item, backupPath, hashPath).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "BtttrTags: Error processing poster for [{Name}] ({ItemId})", item.Name, item.Id.ToString("N"));
            }

            done++;
            progress?.Report((double)done / total * 100.0);
        }

        _logger.LogInformation("BtttrTags: Finished poster update. Processed {Done}/{Total} items.", done, total);
    }

    private async Task ApplyTagAsync(BaseItem item, TagResult tag, string backupPath, string hashPath)
    {
        string itemId = item.Id.ToString("N");
        string? currentPosterPath = item.GetImagePath(ImageType.Primary);
        if (string.IsNullOrEmpty(currentPosterPath) || !File.Exists(currentPosterPath))
        {
            _logger.LogWarning("BtttrTags: [{Name}] ({ItemId}) qualifies for '{Tag}' but no primary poster image file was found.", item.Name, itemId, tag.Text);
            return;
        }

        string currentFileHash = ComputeFileHash(currentPosterPath);
        string goldText = tag.IsGold.ToString();
        var sidecar = ReadSidecar(hashPath);
        bool hasBackup = File.Exists(backupPath);
        bool needsRender = true;

        if (sidecar == null)
        {
            if (hasBackup)
            {
                _logger.LogInformation("BtttrTags: [{Name}] ({ItemId}) -> Backup found without a sidecar. Rendering from the backup.", item.Name, itemId);
            }
            else
            {
                CopyFileAtomic(currentPosterPath, backupPath);
            }
        }
        else if (sidecar.WasInterrupted && hasBackup)
        {
            _logger.LogWarning("BtttrTags: [{Name}] ({ItemId}) -> Previous poster save did not finish. Rendering again from the clean backup.", item.Name, itemId);
        }
        else if (string.Equals(currentFileHash, sidecar.Hash, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(sidecar.Tag, tag.Text, StringComparison.Ordinal) &&
                string.Equals(sidecar.Gold, goldText, StringComparison.Ordinal))
            {
                needsRender = false;
            }
        }
        else if (hasBackup && string.Equals(currentFileHash, ComputeFileHash(backupPath), StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("BtttrTags: [{Name}] ({ItemId}) -> Poster reverted to the original, likely by a library refresh. Tagging it again.", item.Name, itemId);
        }
        else
        {
            _logger.LogInformation("BtttrTags: [{Name}] ({ItemId}) -> User modified or replaced poster detected (hash {Curr} != expected {Stored}). Updating clean backup.",
                item.Name, itemId, currentFileHash, sidecar.Hash);

            CopyFileAtomic(currentPosterPath, backupPath);
        }

        if (!needsRender)
        {
            return;
        }

        byte[] newPosterBytes;
        using (var origStream = File.OpenRead(backupPath))
        {
            newPosterBytes = PosterOverlayDrawer.RenderPosterWithTag(origStream, tag);
        }

        string newHash = ComputeBytesHash(newPosterBytes);
        var previous = sidecar ?? new PosterSidecar(string.Empty, string.Empty, string.Empty, string.Empty);
        WriteSidecar(hashPath, previous with { PendingHash = newHash });

        using (var mem = new MemoryStream(newPosterBytes))
        {
            await SavePosterAsync(item, mem, "image/jpeg").ConfigureAwait(false);
        }

        WriteSidecar(hashPath, new PosterSidecar(newHash, tag.Text, goldText, string.Empty));

        _logger.LogInformation("BtttrTags: [{Name}] ({ItemId}) -> Applied tag '{Tag}' (cache busted).", item.Name, itemId, tag.Text);
    }

    private async Task RestoreOrKeepPosterAsync(BaseItem item, string backupPath, string hashPath)
    {
        string itemId = item.Id.ToString("N");
        string? currentPosterPath = item.GetImagePath(ImageType.Primary);
        var sidecar = ReadSidecar(hashPath);
        bool shouldRestore = true;

        if (sidecar != null && !sidecar.WasInterrupted && !string.IsNullOrEmpty(currentPosterPath) && File.Exists(currentPosterPath))
        {
            string currentHash = ComputeFileHash(currentPosterPath);
            if (!string.Equals(currentHash, sidecar.Hash, StringComparison.OrdinalIgnoreCase))
            {
                shouldRestore = false;
                if (string.Equals(currentHash, ComputeFileHash(backupPath), StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("BtttrTags: [{Name}] ({ItemId}) -> Poster is already the clean original.", item.Name, itemId);
                }
                else
                {
                    _logger.LogInformation("BtttrTags: [{Name}] ({ItemId}) -> User already replaced poster. Preserving user poster.", item.Name, itemId);
                }
            }
        }

        if (shouldRestore)
        {
            var previous = sidecar ?? new PosterSidecar(string.Empty, string.Empty, string.Empty, string.Empty);
            WriteSidecar(hashPath, previous with { PendingHash = ComputeFileHash(backupPath) });

            string mimeType = DetectImageMimeType(backupPath);
            using (var origStream = File.OpenRead(backupPath))
            {
                await SavePosterAsync(item, origStream, mimeType).ConfigureAwait(false);
            }

            _logger.LogInformation("BtttrTags: [{Name}] ({ItemId}) -> Restored clean original poster.", item.Name, itemId);
        }

        DeleteFileLogged(backupPath, item);
        DeleteFileLogged(hashPath, item);
    }

    private async Task SavePosterAsync(BaseItem item, Stream image, string mimeType)
    {
        await _providerManager.SaveImage(item, image, mimeType, ImageType.Primary, null, CancellationToken.None).ConfigureAwait(false);

        var imgInfo = item.GetImageInfo(ImageType.Primary, 0);
        if (imgInfo != null)
        {
            var now = DateTime.UtcNow;
            imgInfo.DateModified = now;
            if (!string.IsNullOrEmpty(imgInfo.Path) && File.Exists(imgInfo.Path))
            {
                try
                {
                    File.SetLastWriteTimeUtc(imgInfo.Path, now);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "BtttrTags: [{Name}] ({ItemId}) -> Could not update the poster file time.", item.Name, item.Id.ToString("N"));
                }
            }
        }

        await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.ImageUpdate, CancellationToken.None).ConfigureAwait(false);
    }

    private void DeleteFileLogged(string path, BaseItem item)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BtttrTags: [{Name}] ({ItemId}) -> Could not delete {Path}.", item.Name, item.Id.ToString("N"), path);
        }
    }

    public async Task<bool> TryRestoreAllPostersAsync(CancellationToken cancellationToken)
    {
        if (!await _posterLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            string backupDir = GetBackupDirectory();

            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[]
                {
                    Jellyfin.Data.Enums.BaseItemKind.Movie,
                    Jellyfin.Data.Enums.BaseItemKind.Series
                },
                IsVirtualItem = false,
                Recursive = true
            });

            foreach (var item in items)
            {
                string backupPath = Path.Combine(backupDir, $"{item.Id:N}.jpg");
                string hashPath = Path.Combine(backupDir, $"{item.Id:N}.hash");

                if (File.Exists(backupPath))
                {
                    try
                    {
                        await RestoreOrKeepPosterAsync(item, backupPath, hashPath).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "BtttrTags: Failed to restore original poster for [{Name}] ({ItemId})", item.Name, item.Id.ToString("N"));
                    }
                }
            }

            return true;
        }
        finally
        {
            _posterLock.Release();
        }
    }

    private sealed record PosterSidecar(string Hash, string Tag, string Gold, string PendingHash)
    {
        public bool WasInterrupted => PendingHash.Length > 0;
    }
}
