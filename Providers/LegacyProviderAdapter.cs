namespace Debryfin.Providers
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Debryfin.Configuration;

    // Strangler strategy (P0-04): wraps the existing CallRealDebrid / CallAllDebrid
    // without changing their behavior; translates null -> Transient and exception -> Transient.
    public sealed class LegacyProviderAdapter : IDebridProvider
    {
        private static readonly TimeSpan ProviderTtl = TimeSpan.FromHours(72);

        private readonly DebridService _service;
        private readonly Func<string, string, CancellationToken, Task<string>> _unrestrict;

        public LegacyProviderAdapter(DebridService service, DebridProvider id)
            : this(service, id, null)
        {
        }

        // Test seam (InternalsVisibleTo "Debryfin.Tests"): allows replacing the legacy call
        // with an injectable function, to test the result translation without network.
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
                    return UnrestrictResult.Fail(ErrorKind.Transient, "The provider did not return a direct link.");
                }

                return UnrestrictResult.Ok(url);
            }
            catch (Exception)
            {
                // Fixed message (no ex.Message) to avoid leaking secrets in the result.
                return UnrestrictResult.Fail(ErrorKind.Transient, "Communication error with the provider.");
            }
        }

        public Task<AccountInfo> GetUserInfoAsync(string apiKey, CancellationToken ct)
        {
            // Dummy until P1: real account info will be implemented when migrating each provider.
            return Task.FromResult(new AccountInfo());
        }

        public Task<ValidationResult> ValidateApiKeyAsync(string apiKey, CancellationToken ct)
        {
            // Dummy until P1-07: do not assert validity until real validation exists.
            return Task.FromResult(new ValidationResult
            {
                IsValid = false,
                Message = "API key validation not implemented (pending P1-07)."
            });
        }
    }
}
