namespace Debryfin.Resolution
{
    using System;
    using System.Collections.Generic;
    using Debryfin.Providers;

    // P0-05: evita repetir peticiones al proveedor tras un fallo.
    // El reloj se inyecta (Func<DateTime>) para poder testear el backoff sin esperas reales.
    public sealed class NegativeCache
    {
        private const int TransientBaseMinutes = 2;
        private const int TransientMaxMinutes = 10;
        private const int RateLimitedMinutes = 15;
        private const int AuthFailedMinutes = 30;
        private const int UnsupportedOrPermanentMinutes = 60;
        private const int NotFoundMinutes = 60;
        private const int PendingMinutes = 2;

        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        public void RecordFailure(string key, ErrorKind error, DateTime now)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (error == ErrorKind.None) return;

            lock (_gate)
            {
                Entry entry;
                if (!_entries.TryGetValue(key, out entry))
                {
                    entry = new Entry();
                    _entries[key] = entry;
                }

                var duration = GetDuration(error, entry);
                if (error != ErrorKind.Transient)
                {
                    // La cadena de reintentos transitorios solo crece con fallos Transient
                    // consecutivos; otro tipo de fallo (o un exito con Clear) la reinicia.
                    entry.ConsecutiveTransientFailures = 0;
                }

                entry.BlockedUntilUtc = now + duration;
            }
        }

        public bool IsBlocked(string key, DateTime now)
        {
            if (string.IsNullOrEmpty(key)) return false;

            lock (_gate)
            {
                Entry entry;
                if (!_entries.TryGetValue(key, out entry)) return false;

                // La entrada se conserva aunque la ventana haya expirado: el contador de
                // fallos transitorios consecutivos debe sobrevivir a la expiracion para
                // que el backoff siga escalando (2 -> 4 -> 8 -> 10 min). Se limpia solo
                // con Clear (exito) o al registrar un fallo nuevo.
                return entry.BlockedUntilUtc >= now;
            }
        }

        public void Clear(string key)
        {
            if (string.IsNullOrEmpty(key)) return;

            lock (_gate)
            {
                _entries.Remove(key);
            }
        }

        private static TimeSpan GetDuration(ErrorKind error, Entry entry)
        {
            switch (error)
            {
                case ErrorKind.Transient:
                    // Backoff exponencial: 2 -> 4 -> 8 -> 10 (techo) minutos,
                    // duplicando por cada fallo transitorio consecutivo.
                    entry.ConsecutiveTransientFailures++;
                    var minutes = TransientBaseMinutes;
                    for (var i = 1; i < entry.ConsecutiveTransientFailures; i++)
                    {
                        minutes *= 2;
                        if (minutes >= TransientMaxMinutes) break;
                    }

                    return TimeSpan.FromMinutes(Math.Min(minutes, TransientMaxMinutes));

                case ErrorKind.RateLimited:
                    return TimeSpan.FromMinutes(RateLimitedMinutes);

                case ErrorKind.AuthFailed:
                    return TimeSpan.FromMinutes(AuthFailedMinutes);

                case ErrorKind.Unsupported:
                case ErrorKind.Permanent:
                    // El enlace no va a empezar a soportarse solo: ventana larga.
                    return TimeSpan.FromMinutes(UnsupportedOrPermanentMinutes);

                case ErrorKind.NotFound:
                    // Enlace muerto: no tiene sentido insistir a corto plazo.
                    return TimeSpan.FromMinutes(NotFoundMinutes);

                case ErrorKind.Pending:
                    // El hoster sigue procesando (p. ej. descarga de TorBox): reintentar pronto.
                    return TimeSpan.FromMinutes(PendingMinutes);

                default:
                    return TimeSpan.FromMinutes(TransientBaseMinutes);
            }
        }

        private sealed class Entry
        {
            public DateTime BlockedUntilUtc;
            public int ConsecutiveTransientFailures;
        }
    }
}
