using System.Text;

namespace Ra3Trainer.Core.Runtime;

/// <summary>Incremental, bounded diagnostic reader. Only an observed session marker grants attribution.</summary>
public sealed class OverlayLogReader
{
    public const int ReadBudget = 256 * 1024;
    private const int RetainedCharacters = 256 * 1024;
    private readonly Queue<string> _lines = new();
    private readonly StringBuilder _partial = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private long _position;
    private long? _creation;
    // 上次读到的末尾若干字节：用来识破"被截断后又写到同样长度"（只看长度判不出来）。
    private readonly byte[] _tail = new byte[8];
    private int _tailCount;
    private int _characters;
    private bool _currentSession, _droppingLine;

    public string Read(Stream stream, long creationTime)
    {
        var reset = _creation != creationTime || stream.Length < _position;
        if (!reset && _tailCount > 0 && _position >= _tailCount)
        {
            // 文件被截断再写到同样长度时 stream.Length < _position 不成立：我们会带着旧游标去读新内容，
            // 结果是读到半行、或把上一段内容混进来。比对上次读到的末尾字节，对不上就当复位。
            var probe = new byte[_tailCount];
            stream.Position = _position - _tailCount;
            reset = stream.Read(probe, 0, probe.Length) != probe.Length ||
                !probe.AsSpan(0, _tailCount).SequenceEqual(_tail.AsSpan(0, _tailCount));
        }

        if (reset)
        {
            _creation = creationTime; _position = 0; _characters = 0; _tailCount = 0;
            _currentSession = _droppingLine = false;
            _lines.Clear(); _partial.Clear(); _decoder.Reset();
        }
        stream.Position = _position;
        var marker = $"osd-session {creationTime:X16}";
        var bytes = new byte[8192];
        var characters = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var budget = ReadBudget;
        while (budget > 0)
        {
            var count = stream.Read(bytes, 0, Math.Min(bytes.Length, budget));
            if (count == 0) break;
            budget -= count; _position += count;
            var tailStart = count > _tail.Length ? count - _tail.Length : 0;
            for (var i = 0; i < count - tailStart && i < _tail.Length; i++)
            {
                _tail[i] = bytes[tailStart + i];
            }

            _tailCount = Math.Min(count, _tail.Length);
            var decoded = _decoder.GetChars(bytes, 0, count, characters, 0, flush: false);
            for (var i = 0; i < decoded; i++)
            {
                var value = characters[i];
                if (value != '\n')
                {
                    if (_partial.Length < 4096) _partial.Append(value);
                    else _droppingLine = true;
                    continue;
                }
                var line = _partial.ToString().TrimEnd('\r');
                _partial.Clear();
                if (_droppingLine) { _droppingLine = false; continue; }
                if (line.StartsWith("osd-session ", StringComparison.Ordinal))
                {
                    _currentSession = line == marker;
                    _lines.Clear(); _characters = 0;
                }
                else if (_currentSession)
                {
                    _lines.Enqueue(line); _characters += line.Length + 1;
                    while (_characters > RetainedCharacters && _lines.Count > 0)
                        _characters -= _lines.Dequeue().Length + 1;
                }
            }
        }
        return _currentSession ? marker + "\n" + string.Join('\n', _lines) + "\n" : string.Empty;
    }
}
