using System.Text;

namespace FkYouTube.ProcessSupport;

internal static class BoundedTextReader
{
    // yt-dlp format/comment metadata can be large; keep ample headroom while
    // preventing an owned child from growing application memory without limit.
    internal const int CommandOutputLimit = 32 * 1024 * 1024;
    internal const int CommandErrorLimit = 2 * 1024 * 1024;
    internal const int FeedLineLimit = 4 * 1024 * 1024;
    internal const int MpvLineLimit = 32 * 1024 * 1024;

    internal static async Task<string> ReadToEndAsync(TextReader reader, int limit, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            if (read > limit - text.Length) throw OutputTooLarge();
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }

    internal static async Task DrainAsync(TextReader reader, CancellationToken token, Action<string>? onChunk = null)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            onChunk?.Invoke(new string(buffer, 0, read));
    }

    internal static IOException OutputTooLarge() => new("작업자 출력이 허용된 크기를 초과했습니다.");
}

/// <summary>Bound a line before allocating it, retaining read-ahead for the next response.</summary>
internal sealed class BoundedLineReader(TextReader reader, int limit)
{
    private readonly char[] _buffer = new char[4096];
    private int _offset, _available;
    private bool _skipLineFeed;

    internal async Task<string?> ReadLineAsync(CancellationToken token)
    {
        var text = new StringBuilder();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (_offset == _available)
            {
                _available = await reader.ReadAsync(_buffer.AsMemory(), token).ConfigureAwait(false);
                _offset = 0;
                if (_available == 0) return text.Length == 0 ? null : text.ToString();
            }
            if (_skipLineFeed)
            {
                _skipLineFeed = false;
                if (_buffer[_offset] == '\n') { _offset++; continue; }
            }
            var end = _buffer.AsSpan(_offset, _available - _offset).IndexOfAny('\r', '\n');
            var count = end < 0 ? _available - _offset : end;
            if (count > limit - text.Length) throw BoundedTextReader.OutputTooLarge();
            text.Append(_buffer, _offset, count);
            _offset += count;
            if (end >= 0)
            {
                _skipLineFeed = _buffer[_offset++] == '\r';
                return text.ToString();
            }
        }
    }
}
