using System;
using System.IO;
using System.Threading.Tasks;
using MediaBrowser.Model.IO;

namespace Debryfin.Strm
{
    public class EmbyStrmStore : IStrmStore
    {
        private readonly IFileSystem _fileSystem;

        public EmbyStrmStore(IFileSystem fileSystem)
        {
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        }

        public bool Exists(string path)
        {
            return _fileSystem.FileExists(path);
        }

        public Task<string> ReadAllTextAsync(string path)
        {
            return _fileSystem.ReadAllTextAsync(path);
        }

        public DateTime GetLastWriteUtc(string path)
        {
            return _fileSystem.GetLastWriteTimeUtc(path).UtcDateTime;
        }

        public async Task WriteAtomicAsync(string path, string content)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = Path.Combine(directory ?? ".", $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

            try
            {
                using (var writer = new StreamWriter(tempPath, false, System.Text.Encoding.UTF8))
                {
                    await writer.WriteAsync(content).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                }

                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch
                    {
                        // Ignore cleanup failure for temp file
                    }
                }
            }
        }
    }
}
