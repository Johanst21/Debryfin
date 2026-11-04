namespace Debryfin.Providers
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Debryfin.Configuration;

    // Estrategia strangler (P0-04): envuelve CallRealDebrid / CallAllDebrid existentes
    // sin cambiar su comportamiento; traduce null -> Transient y excepcion -> Transient.
    public sealed class LegacyProviderAdapter : IDebridProvider
    {
        private static readonly TimeSpan ProviderTtl = TimeSpan.FromHours(72);

        private readonly DebridService _service;
        private readonly Func<string, string, CancellationToken, Task<string>> _unrestrict;

        public LegacyProviderAdapter(DebridService service, DebridProvider id)
            : this(service, id, null)
        {
        }

        // Seam de tests (InternalsVisibleTo "Debryfin.Tests"): permite sustituir la llamada
        // legacy por una funcion inyectable, para probar la traduccion de resultados sin red.
        internal LegacyProviderAdapter(
            DebridService service,
            DebridProvider id,
            Func<string, string, CancellationToken, Task<string>> unrestrict)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            Id = id;
            if (unrestrict != null)
            {
                _unrestrict = unrestrict;
            }
            else
            {
                _unrestrict = (link, key, ct) => id == DebridProvider.RealDebrid
                    ? _service.CallRealDebrid(link, key, ct)
                    : _service.CallAllDebrid(link, key, ct);
            }
        }

        public DebridProvider Id { get; }

        public string DisplayName => Id == DebridProvider.RealDebrid ? "Real-Debrid" : "AllDebrid";

        public TimeSpan DefaultLinkTtl => ProviderTtl;

        public async Task<UnrestrictResult> UnrestrictAsync(string sourceLink, string apiKey, CancellationToken ct)
        {
            try
            {
                var url = await _unrestrict(sourceLink, apiKey, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(url))
                {
                    return UnrestrictResult.Fail(ErrorKind.Transient, "El proveedor no devolvio un enlace directo.");
                }

                return UnrestrictResult.Ok(url);
            }
            catch (Exception)
            {
                // Mensaje fijo (sin ex.Message) para no filtrar secretos en el resultado.
                return UnrestrictResult.Fail(ErrorKind.Transient, "Error de comunicacion con el proveedor.");
            }
        }

        public Task<AccountInfo> GetUserInfoAsync(string apiKey, CancellationToken ct)
        {
            // Dummy hasta P1: la informacion de cuenta real se implementara al migrar cada proveedor.
            return Task.FromResult(new AccountInfo());
        }

        public Task<ValidationResult> ValidateApiKeyAsync(string apiKey, CancellationToken ct)
        {
            // Dummy hasta P1-07: no afirmar validez hasta que exista la validacion real.
            return Task.FromResult(new ValidationResult
            {
                IsValid = false,
                Message = "Validacion de API key no implementada (pendiente P1-07)."
            });
        }
    }
}
