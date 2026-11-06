using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Debryfin.Configuration;
using Debryfin.Providers;
using Debryfin.Resolution;
using MediaBrowser.Model.Logging;
using NSubstitute;
using Xunit;

namespace Debryfin.Tests
{
    public class DebridServiceFallbackTests
    {
        private const string SourceLink = "https://1fichier.com/?testfile123";
        private const string ResolvedUrl = "https://download.example.com/video.mkv";

        [Fact]
        public async Task UnrestrictLinkAsync_NoKeysConfigured_ReturnsNull_DoesNotCallResolver()
        {
            var logger = Substitute.For<ILogger>();
            var resolver = Substitute.For<ILinkResolver>();
            var config = new DebryfinOptions(); // all keys null / empty

            var service = new DebridService(logger, new HttpClient(), resolver, () => config);

            var result = await service.UnrestrictLinkAsync(SourceLink, CancellationToken.None);

            Assert.Null(result);
            logger.Received(1).Warn("No provider API keys configured.");
            await resolver.DidNotReceiveWithAnyArgs().ResolveAsync(null, null, null, CancellationToken.None);
        }

        [Fact]
        public async Task UnrestrictLinkAsync_SingleKeyFails_ReturnsNull_OneAttempt()
        {
            var logger = Substitute.For<ILogger>();
            var resolver = Substitute.For<ILinkResolver>();
            var config = new DebryfinOptions
            {
                RealDebridApiKey = "rd-secret-key"
            };

            resolver.ResolveAsync(
                Arg.Is<IDebridProvider>(p => p.Id == DebridProvider.RealDebrid),
                SourceLink,
                "rd-secret-key",
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LinkResolutionResult
            {
                Error = ErrorKind.Transient,
                Message = "Temporary error"
            }));

            var service = new DebridService(logger, new HttpClient(), resolver, () => config);

            var result = await service.UnrestrictLinkAsync(SourceLink, CancellationToken.None);

            Assert.Null(result);
            await resolver.Received(1).ResolveAsync(
                Arg.Is<IDebridProvider>(p => p.Id == DebridProvider.RealDebrid),
                SourceLink,
                "rd-secret-key",
                Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task UnrestrictLinkAsync_TwoKeys_RdFails_AdSucceeds_ReturnsUrl_TwoAttemptsFallback()
        {
            var logger = Substitute.For<ILogger>();
            var resolver = Substitute.For<ILinkResolver>();
            var config = new DebryfinOptions
            {
                RealDebridApiKey = "rd-secret-key",
                AllDebridApiKey = "ad-secret-key",
                EnableFallback = true
            };

            resolver.ResolveAsync(
                Arg.Is<IDebridProvider>(p => p.Id == DebridProvider.RealDebrid),
                SourceLink,
                "rd-secret-key",
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LinkResolutionResult
            {
                Error = ErrorKind.Transient,
                Message = "Host offline on RD"
            }));

            resolver.ResolveAsync(
                Arg.Is<IDebridProvider>(p => p.Id == DebridProvider.AllDebrid),
                SourceLink,
                "ad-secret-key",
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LinkResolutionResult
            {
                Url = ResolvedUrl,
                Error = ErrorKind.None
            }));

            var service = new DebridService(logger, new HttpClient(), resolver, () => config);

            var result = await service.UnrestrictLinkAsync(SourceLink, CancellationToken.None);

            Assert.Equal(ResolvedUrl, result);

            // Verify order of calls
            Received.InOrder(async () =>
            {
                await resolver.ResolveAsync(
                    Arg.Is<IDebridProvider>(p => p.Id == DebridProvider.RealDebrid),
                    SourceLink,
                    "rd-secret-key",
                    Arg.Any<CancellationToken>());

                await resolver.ResolveAsync(
                    Arg.Is<IDebridProvider>(p => p.Id == DebridProvider.AllDebrid),
                    SourceLink,
                    "ad-secret-key",
                    Arg.Any<CancellationToken>());
            });
        }

        [Fact]
        public async Task UnrestrictLinkAsync_TwoKeys_EnableFallbackFalse_RdFails_ReturnsNull_DoesNotTryAd()
        {
            var logger = Substitute.For<ILogger>();
            var resolver = Substitute.For<ILinkResolver>();
            var config = new DebryfinOptions
            {
                RealDebridApiKey = "rd-secret-key",
                AllDebridApiKey = "ad-secret-key",
                EnableFallback = false
            };

            resolver.ResolveAsync(
                Arg.Is<IDebridProvider>(p => p.Id == DebridProvider.RealDebrid),
                SourceLink,
                "rd-secret-key",
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LinkResolutionResult
            {
                Error = ErrorKind.Transient,
                Message = "Host offline on RD"
            }));

            var service = new DebridService(logger, new HttpClient(), resolver, () => config);

            var result = await service.UnrestrictLinkAsync(SourceLink, CancellationToken.None);

            Assert.Null(result);

            await resolver.Received(1).ResolveAsync(
                Arg.Is<IDebridProvider>(p => p.Id == DebridProvider.RealDebrid),
                SourceLink,
                "rd-secret-key",
                Arg.Any<CancellationToken>());

            await resolver.DidNotReceive().ResolveAsync(
                Arg.Is<IDebridProvider>(p => p.Id == DebridProvider.AllDebrid),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
        }
    }
}
