using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library; // Necesario para interfaces
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

        // Tiempo de expiración del enlace generado (72 horas)
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
                // --- LÓGICA DE DETECCIÓN Y ACTUALIZACIÓN EN SEGUNDO PLANO ---
                // Detectamos todas las versiones para actualizarlas de golpe, 
                // así si cambias de versión, el enlace ya estará fresco.

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

                // Actualizamos TODAS las versiones encontradas (Multithread)
                if (filesToProcess.Count > 0)
                {
                    var tasks = filesToProcess.Distinct().Select(path => ResolveAndCacheUrlAsync(path, cancellationToken));
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Debryfin: Error en escaneo de versiones", ex);
            }

            // --- CORRECCIÓN DUPLICADOS ---
            // Solo devolvemos la MediaSource del archivo que Emby pidió (item.Path).
            // Emby se encarga de mostrar las otras versiones usando su propia lógica de agrupación.
            // Si devolvemos todas aquí, Emby las duplica.

            var finalUrl = await _fileSystem.ReadAllTextAsync(currentItemPath).ConfigureAwait(false);
            
            // Validación: Si el enlace sigue siendo de 1fichier o uptobox, no devolvemos nada (dejamos que Emby falle o reintente)
            if (string.IsNullOrWhiteSpace(finalUrl) || finalUrl.Contains("1fichier.com") || finalUrl.Contains("uptobox.com"))
            {
                return new List<MediaSourceInfo>();
            }

            // Construimos la info con ID y Nombre para evitar líneas vacías
            return BuildMediaSourceInfo(finalUrl, currentItemPath, item);
        }

        private async Task ResolveAndCacheUrlAsync(string strmPath, CancellationToken ct)
        {
            await _strmRefresher.RefreshAsync(strmPath, ct).ConfigureAwait(false);
        }

        private List<MediaSourceInfo> BuildMediaSourceInfo(string directUrl, string sourcePath, BaseItem item)
        {
            var config = Plugin.Instance.Configuration;
            
            // Generar un nombre limpio basado en el archivo (ej: "Peli [4K]" -> "4K")
            var name = Path.GetFileNameWithoutExtension(sourcePath);
            
            // Stable across process restarts (item GUID or SHA1 of path).
            var id = MediaSourceId.For(item != null ? item.Id : Guid.Empty, sourcePath);

            var info = new MediaSourceInfo
            {
                Path = directUrl.Trim(),
                Protocol = MediaProtocol.Http,
                IsRemote = true,
                Id = id,    // <--- Fix para duplicados
                Name = name // <--- Fix para "Sin nombre"
            };

            if (config.UseProxyMode)
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

        public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
            return Task.FromResult<ILiveStream>(null);
        }
    }
}