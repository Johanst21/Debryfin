using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Debryfin.Strm;
using Xunit;

namespace Debryfin.Tests
{
    public class StrmRefresherTests
    {
        [Fact]
        public async Task ConcurrentRefreshes_OnSameStrmPath_PreserveOriginalSourceAndUnrestrictOnce()
        {
            var store = new InMemoryStrmStore(readDelayMs: 25, writeDelayMs: 25);
            var strmPath = "/media/movies/TestMovie.strm";
            var originalSourceUrl = "https://1fichier.com/?abcdef123456";
            var directResolvedUrl = "https://real-debrid.com/d/xyz987direct";

            // Inicializar el .strm con la fuente original (antes de resolverse)
            await store.WriteAtomicAsync(strmPath, originalSourceUrl);

            int unrestrictCallCount = 0;
            Func<string, CancellationToken, Task<string>> unrestrict = async (url, ct) =>
            {
                Interlocked.Increment(ref unrestrictCallCount);
                await Task.Delay(30, ct); // Simular latencia de red de la API Debrid
                return directResolvedUrl;
            };

            var refresher = new StrmRefresher(store, unrestrict);

            // Lanzar 20 tareas concurrentes sobre la misma ruta
            var tasks = Enumerable.Range(0, 20)
                .Select(_ => Task.Run(() => refresher.RefreshAsync(strmPath, CancellationToken.None)))
                .ToArray();

            await Task.WhenAll(tasks);

            var originalStrmPath = strmPath + ".original";
            var savedOriginal = await store.ReadAllTextAsync(originalStrmPath);
            var finalStrm = await store.ReadAllTextAsync(strmPath);

            // Aserciones de coalescing:
            // (a) el delegado unrestrict se invoca EXACTAMENTE 1 vez
            Assert.Equal(1, unrestrictCallCount);

            // (b) .strm final == URL directa
            Assert.Equal(directResolvedUrl, finalStrm);

            // (c) .original == fuente original
            Assert.Equal(originalSourceUrl, savedOriginal);
        }

        [Fact]
        public async Task DelayedReadAfterWrite_DoesNotCorruptOriginal()
        {
            var store = new InMemoryStrmStore();
            var strmPath = "/media/movies/DelayedMovie.strm";
            var originalStrmPath = strmPath + ".original";
            var originalSourceUrl = "https://1fichier.com/?originalsource123";
            var directResolvedUrl = "https://real-debrid.com/d/directurl999";

            // Estado inicial: .strm = "source", .original no existe
            await store.WriteAtomicAsync(strmPath, originalSourceUrl);

            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Configurar: la SEGUNDA llamada a ReadAllTextAsync(.strm) debe esperar a gate antes de devolver
            store.BlockReadsOf(".strm", gate, onCallNumber: 2);

            Func<string, CancellationToken, Task<string>> unrestrict = (url, ct) => Task.FromResult(directResolvedUrl);
            var refresher = new StrmRefresher(store, unrestrict);

            // T1: ejecutar refreshAsync completo
            // T2: ejecutar refreshAsync en paralelo (se bloqueará en ReadAllTextAsync de .strm)
            var t1 = Task.Run(() => refresher.RefreshAsync(strmPath, CancellationToken.None));
            var t2 = Task.Run(() => refresher.RefreshAsync(strmPath, CancellationToken.None));

            // AWAIT hasta que T1 haya terminado (join del task de T1)
            await t1;

            // Ahora T1 ha escrito: .original = "source", .strm = "direct".
            // Signal TCS -> T2 prosigue, lee .strm = "direct".
            gate.TrySetResult(true);

            // AWAIT T2
            await t2;

            // Assert: .original DEBE ser la fuente original y NO la URL resuelta ("direct")
            var savedOriginal = await store.ReadAllTextAsync(originalStrmPath);
            Assert.Equal(originalSourceUrl, savedOriginal);
        }
    }
}
