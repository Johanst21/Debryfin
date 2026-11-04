using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Debryfin.Strm
{
    public class StrmRefresher
    {
        private readonly IStrmStore _store;
        private readonly Func<string, CancellationToken, Task<string>> _unrestrict;
        private readonly Action<string, Exception, string> _loggerError;
        private static readonly TimeSpan LinkExpirationTime = TimeSpan.FromHours(72);
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = 
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        public StrmRefresher(
            IStrmStore store,
            Func<string, CancellationToken, Task<string>> unrestrict,
            Action<string, Exception, string> loggerError = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _unrestrict = unrestrict ?? throw new ArgumentNullException(nameof(unrestrict));
            _loggerError = loggerError;
        }

        public async Task RefreshAsync(string strmPath, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(strmPath)) return;

            var semaphore = _locks.GetOrAdd(strmPath, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                var originalStrmPath = strmPath + ".original";
                string sourceUrl = null;

                // Releer estado DENTRO del lock
                if (_store.Exists(originalStrmPath))
                {
                    var lastModified = _store.GetLastWriteUtc(strmPath);
                    if (DateTime.UtcNow - lastModified < LinkExpirationTime)
                    {
                        return;
                    }

                    sourceUrl = await _store.ReadAllTextAsync(originalStrmPath).ConfigureAwait(false);
                }
                else
                {
                    var content = await _store.ReadAllTextAsync(strmPath).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(content) || !content.Contains("http"))
                    {
                        return;
                    }

                    sourceUrl = content.Trim();
                    // .original SOLO se escribe si no existe
                    if (!_store.Exists(originalStrmPath))
                    {
                        await _store.WriteAtomicAsync(originalStrmPath, sourceUrl).ConfigureAwait(false);
                    }
                }

                if (!string.IsNullOrEmpty(sourceUrl))
                {
                    var directUrl = await _unrestrict(sourceUrl.Trim(), ct).ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(directUrl))
                    {
                        await _store.WriteAtomicAsync(strmPath, directUrl).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _loggerError?.Invoke("Debryfin: Error actualizando {0}", ex, strmPath);
            }
            finally
            {
                semaphore.Release();
            }
        }
    }
}
