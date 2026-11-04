using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;

namespace Debryfin.Tests
{
    public class InMemoryStrmStore : Debryfin.Strm.IStrmStore
    {
        private readonly ConcurrentDictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _timestamps = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _readCallCounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly int _readDelayMs;
        private readonly int _writeDelayMs;

        private string? _blockedSuffix;
        private TaskCompletionSource<bool>? _readGate;
        private int _blockOnCallNumber;

        public InMemoryStrmStore(int readDelayMs = 0, int writeDelayMs = 0)
        {
            _readDelayMs = readDelayMs;
            _writeDelayMs = writeDelayMs;
        }

        public void BlockReadsOf(string pathSuffix, TaskCompletionSource<bool> gate, int onCallNumber = 2)
        {
            _blockedSuffix = pathSuffix;
            _readGate = gate;
            _blockOnCallNumber = onCallNumber;
        }

        public bool Exists(string path)
        {
            return _files.ContainsKey(path);
        }

        public async Task<string> ReadAllTextAsync(string path)
        {
            if (_readDelayMs > 0)
            {
                await Task.Delay(_readDelayMs).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(_blockedSuffix) && path.EndsWith(_blockedSuffix, StringComparison.OrdinalIgnoreCase))
            {
                var count = _readCallCounts.AddOrUpdate(path, 1, (_, c) => c + 1);
                if (count >= _blockOnCallNumber && _readGate != null)
                {
                    await _readGate.Task.ConfigureAwait(false);
                }
            }

            if (_files.TryGetValue(path, out var content))
            {
                return content;
            }

            throw new FileNotFoundException("File not found in memory store", path);
        }

        public async Task WriteAtomicAsync(string path, string content)
        {
            if (_writeDelayMs > 0)
            {
                await Task.Delay(_writeDelayMs).ConfigureAwait(false);
            }

            _files[path] = content;
            _timestamps[path] = DateTime.UtcNow;
        }

        public DateTime GetLastWriteUtc(string path)
        {
            if (_timestamps.TryGetValue(path, out var dt))
            {
                return dt;
            }

            return DateTime.MinValue;
        }
    }
}
