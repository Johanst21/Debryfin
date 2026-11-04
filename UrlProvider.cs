using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library; // Required for interfaces
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using Debryfin.Strm;

namespace Debryfin
{
    public class UrlProvider : IMediaSourceProvider
    {
        private readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;
        private readonly StrmRefresher _strmRefresher;

        // Expiration time of the generated link (72 hours)
        private static readonly TimeSpan LinkExpirationTime = TimeSpan.FromHours(72);

        public UrlProvider(ILogManager logManager, IFileSystem fileSystem)
        {
            _logger = logManager.GetLogger(GetType().Name);
            _fileSystem = fileSystem;
            var strmStore = new EmbyStrmStore(fileSystem);
            _strmRefresher = new StrmRefresher(
                strmStore,
                (url, ct) => Plugin.DebridServiceInstance.UnrestrictLinkAsync(url, ct),
                (msg, ex, path) => _logger.ErrorException(msg, ex, path));
        }

        public async Task<List<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
        {
            if (item.Path == null || !item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            {
                return new List<MediaSourceInfo>();
            }

            var currentItemPath = item.Path;
            var directory = Path.GetDirectoryName(currentItemPath);
            var currentFileName = Path.GetFileName(currentItemPath);
            List<string> filesToProcess = new List<string>();

            try
            {
                // --- BACKGROUND DETECTION AND REFRESH LOGIC ---
                // We detect all versions to refresh them in one go,
                // so if you switch versions, the link will already be fresh.

                var episodeRegex = new Regex(@"[sS](\d{2})[eE](\d{2})", RegexOptions.IgnoreCase);
                var episodeMatch = episodeRegex.Match(currentFileName);

                if (episodeMatch.Success)
                {
                    var identifier = episodeMatch.Value;
                    filesToProcess = _fileSystem.GetFiles(directory, new[] { ".strm" }, false, false)
                        .Select(f => f.FullName)
                        .Where(p => Path.GetFileName(p).IndexOf(identifier, StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();
                }
                else
                {
                    var strmCandidates = _fileSystem.GetFiles(directory, new[] { ".strm" }, false, false)
                        .Select(f => f.FullName);

                    filesToProcess = MovieSiblingSelector.SelectFilesToProcess(currentItemPath, strmCandidates).ToList();
                }

                // Refresh ALL the versions found (Multithread)
                if (filesToProcess.Count > 0)
                {
                    var tasks = filesToProcess.Distinct().Select(path => ResolveAndCacheUrlAsync(path, cancellationToken));
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Debryfin: Error scanning versions", ex);
            }

            // Only return the MediaSource of the file Emby asked for (item.Path).
            // Emby takes care of showing the other versions using its own grouping logic.
            // If we returned all of them here, Emby would duplicate them.

            var finalUrl = await _fileSystem.ReadAllTextAsync(currentItemPath).ConfigureAwait(false);

            // Validation: if the link is still from 1fichier or uptobox, we return nothing (let Emby fail or retry)
            if (string.IsNullOrWhiteSpace(finalUrl) || finalUrl.Contains("1fichier.com") || finalUrl.Contains("uptobox.com"))
            {
                return new List<MediaSourceInfo>();
            }

            // Build the info with ID and Name to avoid empty lines
            return BuildMediaSourceInfo(finalUrl, currentItemPath, item);
        }

        private async Task ResolveAndCacheUrlAsync(string strmPath, CancellationToken ct)
        {
            await _strmRefresher.RefreshAsync(strmPath, ct).ConfigureAwait(false);
        }

        private List<MediaSourceInfo> BuildMediaSourceInfo(string directUrl, string sourcePath, BaseItem item)
        {
            var config = Plugin.Instance?.SafeGetOptions();

            // Generate a clean name based on the file (e.g. "Movie [4K]" -> "4K")
            var name = Path.GetFileNameWithoutExtension(sourcePath);

            // Stable across process restarts (item GUID or SHA1 of path).
            var id = MediaSourceId.For(item != null ? item.Id : Guid.Empty, sourcePath);

            var info = new MediaSourceInfo
            {
                Path = directUrl.Trim(),
                Protocol = MediaProtocol.Http,
                IsRemote = true,
                Id = id,    // <--- Fix for duplicates
                Name = name // <--- Fix for "No name"
            };

            if (config?.UseProxyMode ?? false)
            {
                info.SupportsDirectPlay = false;
                info.SupportsDirectStream = true;
                info.SupportsTranscoding = true;
            }
            else
            {
                info.SupportsDirectPlay = true;
                info.SupportsDirectStream = true;
            }

            return new List<MediaSourceInfo> { info };
        }

        public bool Supports(BaseItem item)
        {
            return item is Video && item.Path != null && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);
        }

        // 4.10 signature: OpenMediaSource gained 'consumerId' as 2nd parameter.
        // Debryfin has no live streams to open, so this is a no-op stub.
        public Task<ILiveStream> OpenMediaSource(string openToken, string consumerId,
            List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
            => Task.FromResult<ILiveStream>(null);
    }
}