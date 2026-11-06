namespace Debryfin.Resolution
{
    using System.Threading;
    using System.Threading.Tasks;
    using Debryfin.Providers;

    public interface ILinkResolver
    {
        Task<LinkResolutionResult> ResolveAsync(
            IDebridProvider provider,
            string sourceUrl,
            string apiKey,
            CancellationToken ct);
    }
}
