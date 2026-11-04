namespace Debryfin.Resolution
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading;
    using System.Threading.Tasks;
    using Debryfin.Providers;

    // P0-05: resuelve un enlace de hoster a traves de un IDebridProvider aplicando:
    //  - coalescing: las llamadas simultaneas para la misma clave comparten UNA sola peticion;
    //  - caché negativa: tras un fallo no se repite la peticion durante la ventana de backoff.
    // El reloj se inyecta (Func<DateTime>) para poder testear el backoff sin esperas reales.
    public sealed class LinkResolver
    {
        private readonly NegativeCache _negativeCache = new NegativeCache();
        private readonly Func<DateTime> _getUtcNow;

        // Clave -> tarea en vuelo. El valor se crea con un TaskCompletionSource para que
        // el trabajo real NO arranque dentro del factory de GetOrAdd (si arrancara, perdedores
        // de la carrera lanzarian peticiones duplicadas al proveedor).
        private readonly ConcurrentDictionary<string, Task<LinkResolutionResult>> _inFlight =
            new ConcurrentDictionary<string, Task<LinkResolutionResult>>(StringComparer.Ordinal);

        public LinkResolver(Func<DateTime> getUtcNow)
        {
            _getUtcNow = getUtcNow ?? throw new ArgumentNullException(nameof(getUtcNow));
        }

        // Visible solo para tests (InternalsVisibleTo "Debryfin.Tests"): permite verificar
        // el estado de la caché negativa sin depender del tiempo real.
        internal NegativeCache NegativeCache => _negativeCache;

        public async Task<LinkResolutionResult> ResolveAsync(
            IDebridProvider provider,
            string sourceUrl,
            string apiKey,
            CancellationToken ct)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            if (string.IsNullOrEmpty(sourceUrl)) throw new ArgumentException("La URL de origen es obligatoria.", nameof(sourceUrl));

            var key = BuildKey(provider, sourceUrl);

            // En backoff: devolvemos el fallo sin tocar al proveedor.
            if (_negativeCache.IsBlocked(key, _getUtcNow()))
            {
                return new LinkResolutionResult
                {
                    Error = ErrorKind.Transient,
                    Message = $"Resolucion de {provider.DisplayName} en espera (backoff). Se reintenta mas tarde."
                };
            }

            var myTcs = new TaskCompletionSource<LinkResolutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = _inFlight.GetOrAdd(key, _ => myTcs.Task);

            if (!ReferenceEquals(task, myTcs.Task))
            {
                // Ya hay una peticion en vuelo para esta clave: nos coalescemos con ella.
                return await task.ConfigureAwait(false);
            }

            try
            {
                var result = await ResolveCoreAsync(provider, sourceUrl, key, apiKey, ct).ConfigureAwait(false);
                myTcs.TrySetResult(result);
                return result;
            }
            catch (Exception ex)
            {
                // La tarea compartida tambien debe fallar para los que se coalescaron.
                myTcs.TrySetException(ex);
                throw;
            }
            finally
            {
                Task<LinkResolutionResult> removed;
                _inFlight.TryRemove(key, out removed);
            }
        }

        private async Task<LinkResolutionResult> ResolveCoreAsync(
            IDebridProvider provider,
            string sourceUrl,
            string key,
            string apiKey,
            CancellationToken ct)
        {
            try
            {
                var unrestrict = await provider.UnrestrictAsync(sourceUrl, apiKey, ct).ConfigureAwait(false);

                if (unrestrict != null && unrestrict.Error == ErrorKind.None && !string.IsNullOrEmpty(unrestrict.Url))
                {
                    // El exito resetea la caché negativa de esta clave.
                    _negativeCache.Clear(key);
                    return new LinkResolutionResult
                    {
                        Url = unrestrict.Url,
                        Error = ErrorKind.None
                    };
                }

                var kind = unrestrict?.Error ?? ErrorKind.Transient;
                _negativeCache.RecordFailure(key, kind, _getUtcNow());
                return new LinkResolutionResult
                {
                    Error = kind,
                    Message = unrestrict?.Message ?? "El proveedor no devolvio un enlace directo."
                };
            }
            catch (OperationCanceledException)
            {
                // Cancelacion no es un fallo del proveedor: no se penaliza con backoff.
                throw;
            }
            catch (Exception)
            {
                // Mensaje fijo (sin ex.Message) para no filtrar secretos en el resultado.
                _negativeCache.RecordFailure(key, ErrorKind.Transient, _getUtcNow());
                return new LinkResolutionResult
                {
                    Error = ErrorKind.Transient,
                    Message = "Error de comunicacion con el proveedor."
                };
            }
        }

        internal static string BuildKey(IDebridProvider provider, string sourceUrl)
        {
            return provider.Id + ":" + sourceUrl;
        }
    }
}
