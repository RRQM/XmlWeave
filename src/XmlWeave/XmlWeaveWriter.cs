using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Xml;

namespace XmlWeave;

/// <summary>Writes UTF-8 XML directly to a caller-owned buffer writer without document buffering.</summary>
/// <remarks>
/// Names must be trusted, prevalidated XML names (as supplied by generated serializers).
/// Callers must balance element names, Enter/Exit calls, and dispose this value exactly once;
/// do not copy it after Enter has rented storage. The output is not disposed or flushed.
/// Limits count serialized UTF-16 characters, including markup and character references.
/// </remarks>
public ref struct XmlWeaveWriter
{
    private readonly IBufferWriter<byte> _output;
    private readonly long _maxCharacters;
    private readonly int _maxDepth;
    private readonly bool _newLineOnElements;
    private readonly bool _newLineOnAttributes;
    private readonly bool _indent;
    private readonly string _indentChars;
    private readonly string _newLineChars;
    private byte[]? _content;
    private long _characters;
    private int _depth;
    private bool _startOpen;
    private bool _disposed;
    private object[]? _active;
    private int _activeCount;

    /// <summary>Creates a writer using caller-owned output and optional resource limits.</summary>
    public XmlWeaveWriter(IBufferWriter<byte> output, XmlWeaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        options?.Validate();
        _output = output;
        _maxCharacters = options?.MaxCharacters ?? 10_000_000;
        _maxDepth = options?.MaxDepth ?? 64;
        _newLineOnElements = options?.NewLineOnElements ?? false;
        _newLineOnAttributes = options?.NewLineOnAttributes ?? false;
        _indent = options?.Indent ?? false;
        _indentChars = options?.IndentChars ?? "  ";
        _newLineChars = options?.NewLineChars ?? "\n";
        _content = null;
        _characters = 0;
        _depth = 0;
        _startOpen = false;
        _disposed = false;
        _active = null;
        _activeCount = 0;
    }

    /// <summary>Writes an opening tag and increments the XML nesting depth.</summary>
    public void StartElement(string name)
    {
        CheckDisposed();
        ArgumentNullException.ThrowIfNull(name);
        if (_depth >= _maxDepth) throw Error("Maximum XML depth exceeded.");
        if (_newLineOnElements) EnsureContentCapacity();
        CloseStart();
        if (_newLineOnElements && _depth > 0)
        {
            if ((_content![_depth - 1] & 1) == 0) WriteLine(_depth);
            _content[_depth - 1] |= 2;
        }
        Raw("<");
        Raw(name);
        if (_newLineOnElements) _content![_depth] = 0;
        _depth++;
        _startOpen = true;
    }

    /// <summary>Closes the currently open start tag.</summary>
    public void EndStartElement()
    {
        CheckDisposed();
        if (!_startOpen) throw Error("No open start element.");
        CloseStart();
    }

    /// <summary>Writes an explicit closing tag and decrements the XML nesting depth.</summary>
    public void EndElement(string name)
    {
        CheckDisposed();
        ArgumentNullException.ThrowIfNull(name);
        if (_depth == 0) throw Error("No element to close.");
        CloseStart();
        if (_newLineOnElements && _content![_depth - 1] == 2) WriteLine(_depth - 1);
        Raw("</");
        Raw(name);
        Raw(">");
        _depth--;
    }

    /// <summary>Writes an escaped attribute inside the currently open start tag.</summary>
    public void Attribute(string name, scoped ReadOnlySpan<char> value)
    {
        CheckDisposed();
        ArgumentNullException.ThrowIfNull(name);
        if (!_startOpen) throw Error("Attributes require an open start element.");
        if (_newLineOnAttributes) WriteLine(_depth);
        else Raw(" ");
        Raw(name);
        Raw("=\"");
        Escaped(value, true);
        Raw("\"");
    }

    /// <summary>Writes escaped XML text, preserving carriage returns with character references.</summary>
    public void Text(scoped ReadOnlySpan<char> value)
    {
        CheckDisposed();
        if (_depth == 0) throw Error("Text requires an element.");
        CloseStart();
        if (_newLineOnElements) _content![_depth - 1] |= 1;
        Escaped(value, false);
    }

    /// <summary>Writes an empty element carrying the XmlWeave null marker.</summary>
    public void NullElement(string name)
    {
        StartElement(name);
        Attribute("xmlns:p", "urn:xmlweave:null");
        Attribute("p:nil", "true");
        EndElement(name);
    }

    /// <summary>Tracks the active reference path, allowing shared references outside that path.</summary>
    public void Enter(object value)
    {
        CheckDisposed();
        ArgumentNullException.ThrowIfNull(value);
        if (_activeCount >= _maxDepth) throw Error("Maximum object depth exceeded.");
        for (int i = 0; i < _activeCount; i++)
            if (ReferenceEquals(_active![i], value)) throw Error("An object reference cycle was detected.");
        if (_active is null)
            _active = ArrayPool<object>.Shared.Rent(Math.Min(_maxDepth, 64));
        else if (_activeCount == _active.Length)
        {
            int capacity = (int)Math.Min(_maxDepth, (long)_active.Length * 2);
            object[] next = ArrayPool<object>.Shared.Rent(capacity);
            Array.Copy(_active, next, _activeCount);
            ArrayPool<object>.Shared.Return(_active, clearArray: true);
            _active = next;
        }
        _active[_activeCount++] = value;
    }

    /// <summary>Removes the most recently entered reference from the active path.</summary>
    public void Exit()
    {
        CheckDisposed();
        if (_activeCount == 0) throw Error("No active object to exit.");
        _active![--_activeCount] = null!;
    }

    /// <summary>Returns pooled storage without closing the caller-owned output.</summary>
    public void Dispose()
    {
        if (_active is not null)
        {
            ArrayPool<object>.Shared.Return(_active, clearArray: true);
            _active = null;
        }
        if (_content is not null)
        {
            ArrayPool<byte>.Shared.Return(_content);
            _content = null;
        }
        _activeCount = 0;
        _disposed = true;
    }

    private void EnsureContentCapacity()
    {
        if (_content is null) _content = ArrayPool<byte>.Shared.Rent(Math.Min(_maxDepth, 64));
        else if (_depth == _content.Length)
        {
            byte[] next = ArrayPool<byte>.Shared.Rent((int)Math.Min(_maxDepth, (long)_content.Length * 2));
            _content.AsSpan(0, _depth).CopyTo(next);
            ArrayPool<byte>.Shared.Return(_content);
            _content = next;
        }
    }

    private void WriteLine(int depth)
    {
        Raw(_newLineChars);
        if (_indent)
            for (int i = 0; i < depth; i++) Raw(_indentChars);
    }

    private void CloseStart()
    {
        if (!_startOpen) return;
        Raw(">");
        _startOpen = false;
    }

    private void CheckDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(XmlWeaveWriter));
    }

    private static XmlWeaveException Error(string message) => new(message, "/", 0, 0);

    private void Count(int count)
    {
        if (count > _maxCharacters - _characters) throw Error("Maximum XML character count exceeded.");
        _characters += count;
    }

    private void Escaped(scoped ReadOnlySpan<char> value, bool attribute)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '&': Raw("&amp;"); break;
                case '<': Raw("&lt;"); break;
                case '>': Raw("&gt;"); break;
                case '"' when attribute: Raw("&quot;"); break;
                case '\r': Raw("&#xD;"); break;
                case '\n' when attribute: Raw("&#xA;"); break;
                case '\t' when attribute: Raw("&#x9;"); break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    {
                        Raw(value.Slice(i, 2));
                        i++;
                    }
                    else if (char.IsSurrogate(c) || (c < ' ' && c != '\t' && c != '\n') || c >= '\uFFFE')
                        throw Error("Invalid XML 1.0 character.");
                    else
                        Raw(value.Slice(i, 1));
                    break;
            }
        }
    }

    private void Raw(scoped ReadOnlySpan<char> value)
    {
        Count(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            uint c = value[i];
            if (c <= 0x7F)
            {
                // Keep size hints bounded even for very large generated names or values.
                int length = 1;
                while (length < 128 && i + length < value.Length && value[i + length] <= 0x7F) length++;
                Span<byte> bytes = _output.GetSpan(length);
                for (int j = 0; j < length; j++) bytes[j] = (byte)value[i + j];
                _output.Advance(length);
                i += length - 1;
            }
            else if (c <= 0x7FF)
            {
                Span<byte> bytes = _output.GetSpan(2);
                bytes[0] = (byte)(0xC0 | (c >> 6));
                bytes[1] = (byte)(0x80 | (c & 0x3F));
                _output.Advance(2);
            }
            else if (char.IsHighSurrogate((char)c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                c = (uint)char.ConvertToUtf32((char)c, value[++i]);
                Span<byte> bytes = _output.GetSpan(4);
                bytes[0] = (byte)(0xF0 | (c >> 18));
                bytes[1] = (byte)(0x80 | ((c >> 12) & 0x3F));
                bytes[2] = (byte)(0x80 | ((c >> 6) & 0x3F));
                bytes[3] = (byte)(0x80 | (c & 0x3F));
                _output.Advance(4);
            }
            else
            {
                if (char.IsSurrogate((char)c)) throw Error("Invalid XML 1.0 character.");
                Span<byte> bytes = _output.GetSpan(3);
                bytes[0] = (byte)(0xE0 | (c >> 12));
                bytes[1] = (byte)(0x80 | ((c >> 6) & 0x3F));
                bytes[2] = (byte)(0x80 | (c & 0x3F));
                _output.Advance(3);
            }
        }
    }
}

/// <summary>Allocation-free, invariant XML scalar lexical conversions for generated serializers.</summary>
public static class XmlWeaveScalar
{
    /// <summary>Formats a boolean using the XML true/false lexical representation.</summary>
    public static ReadOnlySpan<char> Format(bool value, Span<char> destination)
    {
        ReadOnlySpan<char> text = value ? "true" : "false";
        if (!text.TryCopyTo(destination)) throw new ArgumentException("Scalar destination is too small.", nameof(destination));
        return destination[..text.Length];
    }

    /// <summary>Formats a supported scalar into caller-owned storage using XML lexical conventions.</summary>
    public static ReadOnlySpan<char> Format<T>(T value, Span<char> destination) where T : ISpanFormattable
    {
        if (typeof(T) == typeof(bool)) return Format(Unsafe.As<T, bool>(ref value), destination);
        if (typeof(T) == typeof(TimeSpan)) return FormatDuration(Unsafe.As<T, TimeSpan>(ref value), destination);
        ReadOnlySpan<char> special = default;
        if (typeof(T) == typeof(double))
        {
            double number = Unsafe.As<T, double>(ref value);
            special = double.IsPositiveInfinity(number) ? "INF" : double.IsNegativeInfinity(number) ? "-INF" : double.IsNaN(number) ? "NaN" : default;
        }
        else if (typeof(T) == typeof(float))
        {
            float number = Unsafe.As<T, float>(ref value);
            special = float.IsPositiveInfinity(number) ? "INF" : float.IsNegativeInfinity(number) ? "-INF" : float.IsNaN(number) ? "NaN" : default;
        }
        if (!special.IsEmpty)
        {
            if (!special.TryCopyTo(destination)) throw new ArgumentException("Scalar destination is too small.", nameof(destination));
            return destination[..special.Length];
        }
        ReadOnlySpan<char> format = typeof(T) == typeof(DateTime) ? "yyyy-MM-ddTHH:mm:ss.FFFFFFFK"
            : typeof(T) == typeof(DateTimeOffset) ? (Unsafe.As<T, DateTimeOffset>(ref value).Offset == TimeSpan.Zero
                ? "yyyy-MM-ddTHH:mm:ss.FFFFFFF'Z'" : "yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz")
            : typeof(T) == typeof(DateOnly) || typeof(T) == typeof(TimeOnly) ? "O"
            : typeof(T) == typeof(float) || typeof(T) == typeof(double) ? "R" : default;
        if (!value.TryFormat(destination, out int written, format, CultureInfo.InvariantCulture))
            throw new ArgumentException("Scalar destination is too small.", nameof(destination));
        return destination[..written];
    }

    /// <summary>Parses an invariant XML scalar, including XML floating-point values and durations.</summary>
    public static T Parse<T>(ReadOnlySpan<char> value) where T : ISpanParsable<T>
    {
        value = TrimXmlWhitespace(value);
        if (typeof(T) == typeof(bool))
        {
            bool result = value.SequenceEqual("true") || value.SequenceEqual("1") ? true
                : value.SequenceEqual("false") || value.SequenceEqual("0") ? false
                : throw new FormatException("Invalid XML boolean.");
            return Unsafe.As<bool, T>(ref result);
        }
        if (typeof(T) == typeof(TimeSpan))
        {
            TimeSpan result = ParseDuration(value);
            return Unsafe.As<TimeSpan, T>(ref result);
        }
        if (typeof(T) == typeof(double))
        {
            double result = value.SequenceEqual("INF") ? double.PositiveInfinity
                : value.SequenceEqual("-INF") ? double.NegativeInfinity
                : double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
            return Unsafe.As<double, T>(ref result);
        }
        if (typeof(T) == typeof(float))
        {
            float result = value.SequenceEqual("INF") ? float.PositiveInfinity
                : value.SequenceEqual("-INF") ? float.NegativeInfinity
                : float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
            return Unsafe.As<float, T>(ref result);
        }
        if (typeof(T) == typeof(DateTime))
        {
            DateTime result = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            return Unsafe.As<DateTime, T>(ref result);
        }
        if (typeof(T) == typeof(DateTimeOffset))
        {
            DateTimeOffset result = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.None);
            return Unsafe.As<DateTimeOffset, T>(ref result);
        }
        return T.Parse(value, CultureInfo.InvariantCulture);
    }

    private static ReadOnlySpan<char> TrimXmlWhitespace(ReadOnlySpan<char> value)
    {
        int start = 0, end = value.Length;
        while (start < end && value[start] is ' ' or '\t' or '\r' or '\n') start++;
        while (end > start && value[end - 1] is ' ' or '\t' or '\r' or '\n') end--;
        return value[start..end];
    }

    private static ReadOnlySpan<char> FormatDuration(TimeSpan value, Span<char> destination)
    {
        // Work unsigned so TimeSpan.MinValue can be formatted without negation overflow.
        long signedTicks = value.Ticks;
        ulong ticks = signedTicks < 0 ? (ulong)(-(signedTicks + 1)) + 1 : (ulong)signedTicks;
        int position = 0;
        if (signedTicks < 0) Append("-", destination, ref position);
        Append("P", destination, ref position);
        ulong days = ticks / TimeSpan.TicksPerDay;
        ticks %= TimeSpan.TicksPerDay;
        if (days != 0) AppendNumber(days, "D", destination, ref position);
        if (ticks != 0 || days == 0)
        {
            Append("T", destination, ref position);
            ulong hours = ticks / TimeSpan.TicksPerHour;
            ticks %= TimeSpan.TicksPerHour;
            ulong minutes = ticks / TimeSpan.TicksPerMinute;
            ticks %= TimeSpan.TicksPerMinute;
            if (hours != 0) AppendNumber(hours, "H", destination, ref position);
            if (minutes != 0) AppendNumber(minutes, "M", destination, ref position);
            if (ticks != 0 || (hours == 0 && minutes == 0))
            {
                AppendNumber(ticks / TimeSpan.TicksPerSecond, "", destination, ref position);
                ulong fraction = ticks % TimeSpan.TicksPerSecond;
                if (fraction != 0)
                {
                    Span<char> digits = stackalloc char[7];
                    fraction.TryFormat(digits, out _, "D7", CultureInfo.InvariantCulture);
                    int length = 7;
                    while (digits[length - 1] == '0') length--;
                    Append(".", destination, ref position);
                    Append(digits[..length], destination, ref position);
                }
                Append("S", destination, ref position);
            }
        }
        return destination[..position];
    }

    private static void Append(ReadOnlySpan<char> text, Span<char> destination, ref int position)
    {
        if (!text.TryCopyTo(destination[position..])) throw new ArgumentException("Scalar destination is too small.", nameof(destination));
        position += text.Length;
    }

    private static void AppendNumber(ulong number, ReadOnlySpan<char> suffix, Span<char> destination, ref int position)
    {
        if (!number.TryFormat(destination[position..], out int written, default, CultureInfo.InvariantCulture))
            throw new ArgumentException("Scalar destination is too small.", nameof(destination));
        position += written;
        Append(suffix, destination, ref position);
    }

    private static TimeSpan ParseDuration(ReadOnlySpan<char> value)
    {
        int index = 0;
        bool negative = index < value.Length && value[index] == '-';
        if (negative) index++;
        if (index == value.Length || value[index++] != 'P') throw new FormatException("Invalid XML duration.");
        bool time = false, any = false, timeComponent = false;
        int previous = 0;
        ulong years = 0, months = 0, days = 0, hours = 0, minutes = 0, seconds = 0, fraction = 0;
        checked
        {
            while (index < value.Length)
            {
                if (value[index] == 'T' && !time)
                {
                    time = true;
                    index++;
                    if (index == value.Length) throw new FormatException("Invalid XML duration.");
                }
                int start = index;
                ulong number = 0;
                while (index < value.Length && value[index] is >= '0' and <= '9')
                    number = number * 10 + (uint)(value[index++] - '0');
                bool digits = index != start;
                bool fractional = index < value.Length && value[index] == '.';
                if (fractional)
                {
                    index++;
                    int count = 0;
                    while (index < value.Length && value[index] is >= '0' and <= '9')
                    {
                        if (count < 7) fraction = fraction * 10 + (uint)(value[index] - '0');
                        count++;
                        index++;
                    }
                    if (!digits && count == 0) throw new FormatException("Invalid XML duration.");
                    for (; count < 7; count++) fraction *= 10;
                }
                else if (!digits) throw new FormatException("Invalid XML duration.");
                if (index == value.Length) throw new FormatException("Invalid XML duration.");
                char unit = value[index++];
                int order = !time ? unit switch { 'Y' => 1, 'M' => 2, 'D' => 3, _ => 0 }
                    : unit switch { 'H' => 4, 'M' => 5, 'S' => 6, _ => 0 };
                if (order <= previous || (fractional && order != 6)) throw new FormatException("Invalid XML duration.");
                previous = order;
                any = true;
                timeComponent |= time;
                switch (order)
                {
                    case 1: years = number; break;
                    case 2: months = number; break;
                    case 3: days = number; break;
                    case 4: hours = number; break;
                    case 5: minutes = number; break;
                    case 6: seconds = number; break;
                }
            }
            if (!any || (time && !timeComponent)) throw new FormatException("Invalid XML duration.");
            ulong totalDays = (years + months / 12) * 365 + months % 12 * 30 + days;
            ulong ticks = (((totalDays * 24 + hours) * 60 + minutes) * 60 + seconds) * TimeSpan.TicksPerSecond + fraction;
            ulong maximum = negative ? (ulong)long.MaxValue + 1 : (ulong)long.MaxValue;
            if (ticks > maximum) throw new OverflowException("XML duration exceeds TimeSpan range.");
            long result = negative ? (ticks == maximum ? long.MinValue : -(long)ticks) : (long)ticks;
            return new TimeSpan(result);
        }
    }
}

/// <summary>Streams each Advance synchronously, leaving the caller's stream open.</summary>
public sealed class XmlWeaveStreamBufferWriter : IBufferWriter<byte>, IDisposable
{
    private readonly Stream _stream;
    private byte[]? _buffer;

    /// <summary>Creates a pooled staging adapter for a caller-owned stream.</summary>
    public XmlWeaveStreamBufferWriter(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _buffer = ArrayPool<byte>.Shared.Rent(4096);
    }

    /// <summary>Writes the specified number of staged bytes synchronously to the stream.</summary>
    public void Advance(int count)
    {
        byte[] buffer = Buffer;
        if ((uint)count > (uint)buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
        _stream.Write(buffer.AsSpan(0, count));
    }

    /// <summary>Returns staging memory containing at least the requested number of bytes.</summary>
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return Buffer;
    }

    /// <summary>Returns a staging span containing at least the requested number of bytes.</summary>
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return Buffer;
    }

    /// <summary>Returns pooled storage without closing the caller-owned output.</summary>
    public void Dispose()
    {
        byte[]? buffer = _buffer;
        _buffer = null;
        if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
    }

    private byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(XmlWeaveStreamBufferWriter));

    private void EnsureCapacity(int sizeHint)
    {
        if (sizeHint < 0) throw new ArgumentOutOfRangeException(nameof(sizeHint));
        byte[] buffer = Buffer;
        if (sizeHint <= buffer.Length) return;
        byte[] next = ArrayPool<byte>.Shared.Rent(sizeHint);
        _buffer = next;
        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
    }
}
