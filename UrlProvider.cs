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

namespace Debryfin
{
    public class UrlProvider : IMediaSourceProvider
    {
        private readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;

        // Tiempo de expiración del enlace generado (72 horas)
        private static readonly TimeSpan LinkExpirationTime = TimeSpan.FromHours(72);

        public UrlProvider(ILogManager logManager, IFileSystem fileSystem)
        {
            _logger = logManager.GetLogger(GetType().Name);
            _fileSystem = fileSystem;
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
                    var movieRegex = new Regex(@"^(.*?)(?:\s\(?\d{4}\)?|\[|\()", RegexOptions.IgnoreCase);
                    var movieMatch = movieRegex.Match(Path.GetFileNameWithoutExtension(currentFileName));
                    var baseName = movieMatch.Success ? movieMatch.Groups[1].Value.Trim() : Path.GetFileNameWithoutExtension(currentFileName);

                    filesToProcess = _fileSystem.GetFiles(directory, new[] { ".strm" }, false, false)
                        .Select(f => f.FullName)
                        .Where(p => Path.GetFileNameWithoutExtension(p).StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                        .ToList();
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
            return BuildMediaSourceInfo(finalUrl, currentItemPath);
        }

        private async Task ResolveAndCacheUrlAsync(string strmPath, CancellationToken ct)
        {
            var originalStrmPath = strmPath + ".original";
            string sourceUrl = null;

            try
            {
                if (_fileSystem.FileExists(originalStrmPath))
                {
                    var lastModified = _fileSystem.GetLastWriteTimeUtc(strmPath);
                    if (DateTime.UtcNow - lastModified < LinkExpirationTime) return;

                    sourceUrl = await _fileSystem.ReadAllTextAsync(originalStrmPath).ConfigureAwait(false);
                }
                else
                {
                    var content = await _fileSystem.ReadAllTextAsync(strmPath).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(content) || !content.Contains("http")) return;

                    sourceUrl = content.Trim();
                    await _fileSystem.WriteAllTextAsync(originalStrmPath, sourceUrl).ConfigureAwait(false);
                }

                if (!string.IsNullOrEmpty(sourceUrl))
                {
                    // _logger.Info("Debryfin: Actualizando {0}", Path.GetFileName(strmPath)); // Log reducido
                    var directUrl = await Plugin.DebridServiceInstance.UnrestrictLinkAsync(sourceUrl.Trim(), ct).ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(directUrl))
                    {
                        await _fileSystem.WriteAllTextAsync(strmPath, directUrl).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Debryfin: Error actualizando {0}", ex, strmPath);
            }
        }

        private List<MediaSourceInfo> BuildMediaSourceInfo(string directUrl, string sourcePath)
        {
            var config = Plugin.Instance.Configuration;
            
            // Generar un nombre limpio basado en el archivo (ej: "Peli [4K]" -> "4K")
            var name = Path.GetFileNameWithoutExtension(sourcePath);
            
            // Generar un ID único basado en la ruta. 
            // ESTO ES CLAVE: Al darle el mismo ID que el path, Emby sabe que es el mismo archivo y no lo duplica.
            var id = sourcePath.ToLowerInvariant().GetHashCode().ToString();

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