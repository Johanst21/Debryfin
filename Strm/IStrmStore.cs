using System;
using System.Threading.Tasks;

namespace Debryfin.Strm
{
    public interface IStrmStore
    {
        bool Exists(string path);
        Task<string> ReadAllTextAsync(string path);
        Task WriteAtomicAsync(string path, string content);
        DateTime GetLastWriteUtc(string path);
    }
}
