namespace Debryfin.Resolution
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading;
    using System.Threading.Tasks;
    using Debryfin.Providers;

    // P0-05: resolves a hoster link through an IDebridProvider applying:
    //  - coalescing: simultaneous calls for the same key share a single request;
    //  - negative cache: after a failure the request is not repeated during the backoff window.
    // The clock is injected (Func<DateTime>) to test the backoff without real waits.
    public sealed class LinkResolver
    {
        private readonly NegativeCache _negativeCache = new NegativeCache();
        private readonly Func<DateTime> _getUtcNow;

        // Key -> in-flight task. The value is created with a TaskCompletionSource so that
        // the real work does NOT start inside the GetOrAdd factory (if it did, the losers
        // of the race would fire duplicate requests to the provider).
        private readonly ConcurrentDictionary<string, Task<LinkResolutionResult>> _inFlight =
            new ConcurrentDictionary<string, Task<LinkResolutionResult>>(StringComparer.Ordinal);

        public LinkResolver(Func<DateTime> getUtcNow)
        {
            _getUtcNow = getUtcNow ?? throw new ArgumentNullException(nameof(getUtcNow));
        }

        // Visible to tests only (InternalsVisibleTo "Debryfin.Tests"): allows verifying
        // the state of the negative cache without depending on real time.
        internal NegativeCache NegativeCache => _negativeCache;

        public async Task<LinkResolutionResult> ResolveAsync(
            IDebridProvider provider,
            string sourceUrl,
            string apiKey,
            CancellationToken ct)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            if (string.IsNullOrEmpty(sourceUrl)) throw new ArgumentException("The source URL is required.", nameof(sourceUrl));

            var key = BuildKey(provider, sourceUrl);

            // In backoff: we return the failure without touching the provider.
            if (_negativeCache.IsBlocked(key, _getUtcNow()))
            {
                return new LinkResolutionResult
                {
                    Error = ErrorKind.Transient,
                    Message = $"{provider.DisplayName} resolution waiting (backoff). It will be retried later."
                };
            }

            var myTcs = new TaskCompletionSource<LinkResolutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = _inFlight.GetOrAdd(key, _ => myTcs.Task);

            if (!ReferenceEquals(task, myTcs.Task))
            {
                // There is already a request in flight for this key: we coalesce with it.
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
                // The shared task must also fail for those who coalesced.
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
                    // Success resets the negative cache for this key.
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
                    Message = unrestrict?.Message ?? "The provider did not return a direct link."
                };
            }
            catch (OperationCanceledException)
            {
                // Cancellation is not a provider failure: it is not penalized with backoff.
                throw;
            }
            catch (Exception)
            {
                // Fixed message (no ex.Message) to avoid leaking secrets in the result.
                _negativeCache.RecordFailure(key, ErrorKind.Transient, _getUtcNow());
                return new LinkResolutionResult
                {
                    Error = ErrorKind.Transient,
                    Message = "Communication error with the provider."
                };
            }
        }

        internal static string BuildKey(IDebridProvider provider, string sourceUrl)
        {
            return provider.Id + ":" + sourceUrl;
        }
    }
}
