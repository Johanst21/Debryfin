using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Debryfin.Configuration;
using Debryfin.Providers;
using Debryfin.Resolution;
using NSubstitute;
using Xunit;

namespace Debryfin.Tests
{
    public class LinkResolverTests
    {
        private static IDebridProvider CreateFailingProvider(int[] callCounter, ErrorKind kind)
        {
            var provider = Substitute.For<IDebridProvider>();
            provider.Id.Returns(DebridProvider.RealDebrid);
            provider.DisplayName.Returns("Real-Debrid");
            provider.UnrestrictAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    Interlocked.Increment(ref callCounter[0]);
                    return Task.FromResult(UnrestrictResult.Fail(kind, "fallo del proveedor"));
                });
            return provider;
        }

        [Fact]
        public async Task TransientFailure_AppliesExponentialBackoffWindow()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var clock = () => now;
            var calls = new int[1];
            var provider = CreateFailingProvider(calls, ErrorKind.Transient);

            var resolver = new LinkResolver(clock);
            const string url = "https://1fichier.com/?abc123";
            const string key = "key";

            // Llamada 1: el proveedor se invoca y falla -> ventana Transient de 2 min.
            var r1 = await resolver.ResolveAsync(provider, url, key, CancellationToken.None);
            Assert.False(r1.IsSuccess);
            Assert.Equal(ErrorKind.Transient, r1.Error);
            Assert.Equal(1, calls[0]);

            // +2 min (borde de la ventana): sigue bloqueada, NO se repite la peticion.
            now = now.AddMinutes(2);
            var r2 = await resolver.ResolveAsync(provider, url, key, CancellationToken.None);
            Assert.False(r2.IsSuccess);
            Assert.Equal(1, calls[0]);

            // +5 min: la ventana (2 min) ha expirado -> se reintenta contra el proveedor.
            now = now.AddMinutes(3);
            var r3 = await resolver.ResolveAsync(provider, url, key, CancellationToken.None);
            Assert.False(r3.IsSuccess);
            Assert.Equal(2, calls[0]);

            // El segundo fallo transitorio consecutivo duplica la ventana: 2 -> 4 min.
            Assert.True(resolver.NegativeCache.IsBlocked(LinkResolver.BuildKey(provider, url), now));
            now = now.AddMinutes(3); // t0+8: seguimos dentro de la ventana de 4 min (t0+5 .. t0+9)
            var r4 = await resolver.ResolveAsync(provider, url, key, CancellationToken.None);
            Assert.False(r4.IsSuccess);
            Assert.Equal(2, calls[0]);
            now = now.AddMinutes(2); // t0+10: fuera de la ventana de 4 min
            var r5 = await resolver.ResolveAsync(provider, url, key, CancellationToken.None);
            Assert.False(r5.IsSuccess);
            Assert.Equal(3, calls[0]);
        }

        [Fact]
        public async Task ConcurrentResolves_SameKey_CoalesceToSingleProviderCall()
        {
            var provider = Substitute.For<IDebridProvider>();
            provider.Id.Returns(DebridProvider.RealDebrid);
            var calls = new int[1];
            const string directUrl = "https://real-debrid.com/d/xyz987direct";

            provider.UnrestrictAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(ci => Task.Run(async () =>
                {
                    Interlocked.Increment(ref calls[0]);
                    await Task.Delay(50); // latencia simulada de la API del proveedor
                    return UnrestrictResult.Ok(directUrl);
                }));

            var resolver = new LinkResolver(() => DateTime.UtcNow);
            const string url = "https://1fichier.com/?coalesce123";

            // 10 llamadas simultaneas a la misma clave: solo UNA debe llegar al proveedor.
            var tasks = Enumerable.Range(0, 10)
                .Select(_ => resolver.ResolveAsync(provider, url, "key", CancellationToken.None))
                .ToArray();

            var results = await Task.WhenAll(tasks);

            Assert.Equal(1, calls[0]);
            Assert.All(results, r =>
            {
                Assert.True(r.IsSuccess);
                Assert.Equal(directUrl, r.Url);
            });
        }

        [Fact]
        public async Task Success_ReturnsUrlAndClearsNegativeCache()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var clock = () => now;
            const string url = "https://1fichier.com/?success123";
            const string directUrl = "https://real-debrid.com/d/ok456";

            // Primero falla (Permanent: ventana de 60 min) para luego tener algo que limpiar.
            var failing = CreateFailingProvider(new int[1], ErrorKind.Permanent);
            var resolver = new LinkResolver(clock);
            var blocked = await resolver.ResolveAsync(failing, url, "key", CancellationToken.None);
            Assert.False(blocked.IsSuccess);
            Assert.True(resolver.NegativeCache.IsBlocked(LinkResolver.BuildKey(failing, url), now));

            // El proveedor responde bien: la resolucion devolve la URL y limpia la caché negativa.
            var ok = Substitute.For<IDebridProvider>();
            ok.Id.Returns(DebridProvider.RealDebrid);
            ok.UnrestrictAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(ci => Task.FromResult(UnrestrictResult.Ok(directUrl)));

            now = now.AddMinutes(61); // fuera de la ventana del fallo anterior
            var result = await resolver.ResolveAsync(ok, url, "key", CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal(directUrl, result.Url);
            Assert.Equal(ErrorKind.None, result.Error);
            Assert.False(resolver.NegativeCache.IsBlocked(LinkResolver.BuildKey(ok, url), now));
        }
    }
}
