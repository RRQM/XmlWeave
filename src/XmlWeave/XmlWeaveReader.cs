using System;
using System.Buffers;
using System.Text;

namespace XmlWeave;

/// <summary>The tokens exposed by <see cref="XmlWeaveReader"/>.</summary>
public enum XmlWeaveToken
{
    /// <summary>The reader has not advanced.</summary>
    None,
    /// <summary>An opening element, before its attributes.</summary>
    StartElement,
    /// <summary>An attribute, including namespace declarations.</summary>
    Attribute,
    /// <summary>Text or CDATA content.</summary>
    Text,
    /// <summary>A closing element, including synthetic empty-tag closings.</summary>
    EndElement,
    /// <summary>The document has ended.</summary>
    EndDocument
}

/// <summary>
/// A forward-only, UTF8 XML 1.0 reader. Namespace declarations are exposed as Attribute
/// tokens. Dispose the reader to return its pooled structural storage; input is never owned.
/// </summary>
public ref struct XmlWeaveReader
{
    private readonly ReadOnlySpan<byte> _input;
    private readonly int _maxDepth;
    private int _position, _tokenPosition, _nameStart, _nameLength, _valueStart, _valueLength;
    private int _depth, _bindingCount, _attributeCount, _nextAttribute;
    private Frame[]? _frames;
    private Binding[]? _bindings;
    private AttributeInfo[]? _attributes;
    private bool _empty, _popPending, _rootSeen, _rootClosed, _cdata, _disposed;

    private struct Frame { public int Name, Length, BindingBase; }
    private struct Binding { public int Prefix, PrefixLength, Value, ValueLength; }
    private struct AttributeInfo { public int Name, Length, Value, ValueLength; }
    private const string NullNamespace = "urn:xmlweave:null";
    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";
    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

    /// <summary>Creates a reader over a caller-owned complete UTF8 document.</summary>
    public XmlWeaveReader(ReadOnlySpan<byte> utf8, XmlWeaveOptions? options = null)
    {
        this = default;
        options?.Validate();
        _input = utf8;
        _maxDepth = options?.MaxDepth ?? 64;
        long limit = options?.MaxCharacters ?? 10_000_000;
        long characters = 0;
        for (int i = 0; i < utf8.Length;)
        {
            _tokenPosition = i;
            if (Rune.DecodeFromUtf8(utf8.Slice(i), out Rune rune, out int consumed) != OperationStatus.Done)
                throw Error("Invalid UTF8.");
            if (!IsXmlCharacter(rune.Value)) throw Error("Invalid XML character.");
            characters += rune.Utf16SequenceLength;
            if (characters > limit) throw Error("Maximum XML character count exceeded.");
            i += consumed;
        }
        _position = utf8.StartsWith("\uFEFF"u8) ? 3 : 0;
        _tokenPosition = _position;
    }

    /// <summary>The current token.</summary>
    public XmlWeaveToken Token { readonly get; private set; }

    /// <summary>Whether the current attribute declares a namespace.</summary>
    public bool IsNamespaceDeclaration => Token == XmlWeaveToken.Attribute && IsDeclaration(_nameStart, _nameLength);

    /// <summary>Whether the current attribute is nil in urn:xmlweave:null.</summary>
    public bool IsNilAttribute
    {
        get
        {
            if (Token != XmlWeaveToken.Attribute || IsNamespaceDeclaration) return false;
            int colon = Colon(_nameStart, _nameLength);
            return colon > 0 && BytesEqual(_nameStart + colon + 1, _nameLength - colon - 1, "nil")
                && NamespaceEquals(Resolve(_nameStart, colon), NullNamespace);
        }
    }

    /// <summary>Compares an unqualified name without allocating. Namespaced names do not match.</summary>
    public bool NameEquals(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (Token is not (XmlWeaveToken.StartElement or XmlWeaveToken.EndElement or XmlWeaveToken.Attribute)) return false;
        if (Colon(_nameStart, _nameLength) >= 0) return false;
        if (Token != XmlWeaveToken.Attribute && !NamespaceEquals(Resolve(0, 0), "")) return false;
        return BytesEqual(_nameStart, _nameLength, name);
    }

    /// <summary>Advances, ignoring comments and processing instructions, but preserving all inner text.</summary>
    public bool Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(XmlWeaveReader));
        if (Token == XmlWeaveToken.EndDocument) return false;
        if (_popPending)
        {
            _bindingCount = _frames![_depth - 1].BindingBase;
            _depth--;
            _popPending = false;
            if (_depth == 0) _rootClosed = true;
        }
        if (_nextAttribute < _attributeCount)
        {
            AttributeInfo attribute = _attributes![_nextAttribute++];
            _nameStart = attribute.Name; _nameLength = attribute.Length;
            _valueStart = attribute.Value; _valueLength = attribute.ValueLength;
            _tokenPosition = attribute.Name;
            Token = XmlWeaveToken.Attribute;
            return true;
        }
        if (_empty)
        {
            _empty = false;
            _tokenPosition = _frames![_depth - 1].Name - 1;
            SetEnd();
            return true;
        }
        while (_position < _input.Length)
        {
            _tokenPosition = _position;
            if (_input[_position] != (byte)'<')
            {
                int start = _position;
                while (_position < _input.Length && _input[_position] != (byte)'<') _position++;
                _valueStart = start; _valueLength = _position - start; _cdata = false;
                if (_input.Slice(start, _valueLength).IndexOf("]]>"u8) >= 0) throw Error("The sequence ]]> is not permitted in text.");
                Decode(start, _valueLength, false, true, default, false);
                if (_depth == 0)
                {
                    for (int i = start; i < _position; i++) if (!IsSpace(_input[i])) throw Error("Text is not permitted outside the root element.");
                    continue;
                }
                Token = XmlWeaveToken.Text;
                return true;
            }
            if (At("<!--"u8)) { SkipComment(); continue; }
            if (At("<?"u8)) { SkipInstruction(); continue; }
            if (At("<![CDATA["u8))
            {
                if (_depth == 0) throw Error("CDATA is not permitted outside the root element.");
                int start = _position + 9;
                int length = _input.Slice(start).IndexOf("]]>"u8);
                if (length < 0) throw Error("Unterminated CDATA.");
                _valueStart = start; _valueLength = length; _cdata = true;
                _position = start + length + 3;
                Token = XmlWeaveToken.Text;
                return true;
            }
            if (At("</"u8))
            {
                if (_depth == 0) throw Error("Unexpected closing element.");
                _position += 2;
                int start = _position;
                int length = ReadName();
                SkipSpace(); Expect((byte)'>');
                Frame frame = _frames![_depth - 1];
                if (!_input.Slice(start, length).SequenceEqual(_input.Slice(frame.Name, frame.Length))) throw Error("Mismatched closing element.");
                SetEnd();
                return true;
            }
            if (At("<!"u8)) throw Error("DTD and markup declarations are prohibited.");
            OpenElement();
            return true;
        }
        _tokenPosition = _position;
        if (_depth != 0) throw Error("Unclosed element.");
        if (!_rootSeen) throw Error("Missing root element.");
        Token = XmlWeaveToken.EndDocument;
        return false;
    }

    /// <summary>Decodes the current text or attribute, applying XML newline normalization.</summary>
    public string GetString()
    {
        CheckValue();
        bool attribute = Token == XmlWeaveToken.Attribute;
        int length = Decode(_valueStart, _valueLength, attribute, !_cdata || attribute, default, false);
        if (length == 0) return string.Empty;
        char[] rented = ArrayPool<char>.Shared.Rent(length);
        try
        {
            Decode(_valueStart, _valueLength, attribute, !_cdata || attribute, rented.AsSpan(0, length), true);
            return new string(rented, 0, length);
        }
        finally { ArrayPool<char>.Shared.Return(rented); }
    }

    /// <summary>Decodes into caller storage; throws FormatException when it is too small.</summary>
    public readonly ReadOnlySpan<char> GetValue(Span<char> destination)
    {
        CheckValue();
        int length = Decode(_valueStart, _valueLength, Token == XmlWeaveToken.Attribute,
            Token == XmlWeaveToken.Attribute || !_cdata, destination, true);
        return destination.Slice(0, length);
    }

    /// <summary>
    /// Collects scalar content, starting at its first Text or EndElement token, and leaves
    /// the reader on EndElement. Values exceeding destination throw FormatException;
    /// use ReadElementString for arbitrarily sized string values.
    /// </summary>
    public readonly ReadOnlySpan<char> ReadElementValue(Span<char> destination)
    {
            // The scoped core cannot retain destination. A readonly facade prevents C# from
            // treating caller stack storage as a potential assignment to this ref struct.
            int length = System.Runtime.CompilerServices.Unsafe.AsRef(in this).ReadElementValueCore(destination);
            return destination.Slice(0, length);
        }

        private int ReadElementValueCore(scoped Span<char> destination)
        {
            int length = 0;
        while (Token == XmlWeaveToken.Text)
        {
            length += GetValue(destination.Slice(length)).Length;
            Read();
        }
        if (Token != XmlWeaveToken.EndElement) throw Error("Expected scalar element content.");
        return length;
    }

    /// <summary>Tests decoded XML text for whitespace without requiring a scalar-sized buffer.</summary>
    public readonly bool IsWhitespace()
    {
        if (Token != XmlWeaveToken.Text) return false;
        int end = _valueStart + _valueLength;
        Span<char> character = stackalloc char[2];
        for (int i = _valueStart; i < end;)
        {
            if (_input[i] == (byte)'&' && !_cdata)
            {
                int length = _input.Slice(i, end - i).IndexOf((byte)';');
                if (length < 0) throw ValueError(i, "Unterminated entity reference.");
                int count = Decode(i, length + 1, false, true, character, true);
                for (int j = 0; j < count; j++) if (character[j] is not (' ' or '\t' or '\r' or '\n')) return false;
                i += length + 1;
            }
            else
            {
                if (!IsSpace(_input[i])) return false;
                i++;
            }
        }
        return true;
    }

    /// <summary>Collects scalar content without allocating intermediate strings.</summary>
    public string ReadElementString()
    {
        if (Token == XmlWeaveToken.EndElement) return string.Empty;
        char[] buffer = ArrayPool<char>.Shared.Rent(256);
        int length = 0;
        try
        {
            while (Token == XmlWeaveToken.Text)
            {
                int count = Decode(_valueStart, _valueLength, false, !_cdata, default, false);
                if (count > int.MaxValue - length) throw Error("Scalar value is too large.");
                if (length + count > buffer.Length)
                {
                    char[] larger = ArrayPool<char>.Shared.Rent(length + count);
                    buffer.AsSpan(0, length).CopyTo(larger);
                    ArrayPool<char>.Shared.Return(buffer); buffer = larger;
                }
                length += GetValue(buffer.AsSpan(length)).Length;
                Read();
            }
            if (Token != XmlWeaveToken.EndElement) throw Error("Expected scalar element content.");
            return length == 0 ? string.Empty : new string(buffer, 0, length);
        }
        finally { ArrayPool<char>.Shared.Return(buffer); }
    }

    /// <summary>Requires the current root end, followed only by XML trailing miscellany.</summary>
    public void Finish()
    {
        if (Token == XmlWeaveToken.EndDocument) return;
        if (Token != XmlWeaveToken.EndElement || _depth != 1) throw Error("Expected the root closing element.");
        if (Read()) throw Error("Unexpected trailing content.");
    }

    /// <summary>Creates an error with the current token's path and XML line/column.</summary>
    public readonly XmlWeaveException Error(string message, Exception? inner = null) => ErrorAt(_tokenPosition, message, inner);

        private readonly XmlWeaveException ErrorAt(int position, string message, Exception? inner = null)
        {
            int line = 1, column = 1;
        for (int i = _input.StartsWith("\uFEFF"u8) ? 3 : 0; i < position && i < _input.Length;)
        {
            byte b = _input[i];
            if (b == 13)
            {
                        i++; if (i < position && _input[i] == 10) i++;
                line++; column = 1;
            }
            else if (b == 10) { i++; line++; column = 1; }
            else if (Rune.DecodeFromUtf8(_input.Slice(i), out Rune rune, out int used) == OperationStatus.Done)
            { i += used; column += rune.Utf16SequenceLength; }
            else { i++; column++; }
        }
        var path = new StringBuilder();
        for (int i = 0; i < _depth; i++)
        {
            Frame frame = _frames![i];
            path.Append('/'); path.Append(Encoding.UTF8.GetString(_input.Slice(frame.Name, frame.Length)));
        }
        if (path.Length == 0) path.Append('/');
        return new XmlWeaveException(message, path.ToString(), line, column, inner);
    }

    /// <summary>Returns all pooled storage. Safe to call more than once on this instance.</summary>
    public void Dispose()
    {
        if (_frames is not null) ArrayPool<Frame>.Shared.Return(_frames);
        if (_bindings is not null) ArrayPool<Binding>.Shared.Return(_bindings);
        if (_attributes is not null) ArrayPool<AttributeInfo>.Shared.Return(_attributes);
        _frames = null; _bindings = null; _attributes = null; _disposed = true;
    }

    private void OpenElement()
    {
        if (_rootClosed) throw Error("Multiple root elements.");
        if (_depth >= _maxDepth) throw Error("Maximum XML depth exceeded.");
        _position++;
        int name = _position, length = ReadName();
        Ensure(ref _frames, _depth + 1);
        _frames![_depth++] = new Frame { Name = name, Length = length, BindingBase = _bindingCount };
        _rootSeen = true;
        _attributeCount = 0; _nextAttribute = 0;
        while (true)
        {
            bool space = SkipSpace();
            if (_position >= _input.Length) throw Error("Unterminated opening element.");
            if (_input[_position] == (byte)'>') { _position++; break; }
            if (_input[_position] == (byte)'/') { _position++; Expect((byte)'>'); _empty = true; break; }
            if (!space) throw Error("Attributes must be separated by whitespace.");
            int start = _position, size = ReadName();
            SkipSpace(); Expect((byte)'='); SkipSpace();
            if (_position >= _input.Length || (_input[_position] != (byte)'\'' && _input[_position] != (byte)'"')) throw Error("Expected quoted attribute value.");
            byte quote = _input[_position++];
            int value = _position;
            while (_position < _input.Length && _input[_position] != quote)
            {
                if (_input[_position] == (byte)'<') throw Error("Literal < is not permitted in attributes.");
                _position++;
            }
            if (_position == _input.Length) throw Error("Unterminated attribute.");
            int valueLength = _position++ - value;
            _tokenPosition = start;
            Decode(value, valueLength, true, true, default, false);
            for (int i = 0; i < _attributeCount; i++)
            {
                AttributeInfo old = _attributes![i];
                if (_input.Slice(old.Name, old.Length).SequenceEqual(_input.Slice(start, size))) throw Error("Duplicate attribute.");
            }
            Ensure(ref _attributes, _attributeCount + 1);
            _attributes![_attributeCount++] = new AttributeInfo { Name = start, Length = size, Value = value, ValueLength = valueLength };
            if (IsDeclaration(start, size)) AddBinding(start, size, value, valueLength);
        }
        ValidateNamespace(name, length, false);
        for (int i = 0; i < _attributeCount; i++)
        {
            AttributeInfo a = _attributes![i];
            if (IsDeclaration(a.Name, a.Length)) continue;
            _tokenPosition = a.Name;
            ValidateNamespace(a.Name, a.Length, true);
            int colon = Colon(a.Name, a.Length), local = colon < 0 ? a.Name : a.Name + colon + 1;
            int localLength = a.Length - (local - a.Name);
            int ns = colon < 0 ? -1 : Resolve(a.Name, colon);
            for (int j = 0; j < i; j++)
            {
                AttributeInfo b = _attributes[j];
                if (IsDeclaration(b.Name, b.Length)) continue;
                int bc = Colon(b.Name, b.Length), bl = bc < 0 ? b.Name : b.Name + bc + 1;
                if (_input.Slice(local, localLength).SequenceEqual(_input.Slice(bl, b.Length - (bl - b.Name)))
                    && NamespacesEqual(ns, bc < 0 ? -1 : Resolve(b.Name, bc))) throw Error("Duplicate expanded attribute name.");
            }
        }
        _nameStart = name; _nameLength = length; _tokenPosition = name - 1;
        Token = XmlWeaveToken.StartElement;
    }

    private void SetEnd()
    {
        Frame frame = _frames![_depth - 1];
        _nameStart = frame.Name; _nameLength = frame.Length;
        _popPending = true; Token = XmlWeaveToken.EndElement;
    }

    private void AddBinding(int name, int length, int value, int valueLength)
    {
        int prefixLength = length == 5 ? 0 : length - 6;
        int prefix = prefixLength == 0 ? 0 : name + 6;
        bool xml = BytesEqual(prefix, prefixLength, "xml");
        bool xmlUri = ValueEquals(value, valueLength, XmlNamespace);
        if (BytesEqual(prefix, prefixLength, "xmlns") || ValueEquals(value, valueLength, XmlnsNamespace)
            || xml != xmlUri || (prefixLength != 0 && ValueEquals(value, valueLength, "")))
            throw Error("Invalid reserved namespace binding.");
        Ensure(ref _bindings, _bindingCount + 1);
        _bindings![_bindingCount++] = new Binding { Prefix = prefix, PrefixLength = prefixLength, Value = value, ValueLength = valueLength };
    }

    private void ValidateNamespace(int name, int length, bool attribute)
    {
        int colon = Colon(name, length);
        if (colon < 0) return;
        if (BytesEqual(name, colon, "xmlns")) throw Error("The xmlns prefix is reserved.");
        if (Resolve(name, colon) == -1) throw Error(attribute ? "Undeclared attribute prefix." : "Undeclared element prefix.");
    }

    // -1 is the empty namespace; -2 is the implicitly bound xml namespace.
    private int Resolve(int prefix, int length)
    {
        if (BytesEqual(prefix, length, "xml")) return -2;
        for (int i = _bindingCount - 1; i >= 0; i--)
        {
            Binding binding = _bindings![i];
            if (length == binding.PrefixLength && _input.Slice(prefix, length).SequenceEqual(_input.Slice(binding.Prefix, length))) return i;
        }
        return -1;
    }

    private bool NamespaceEquals(int index, string value) => index == -1 ? value.Length == 0
        : index == -2 ? value == XmlNamespace : ValueEquals(_bindings![index].Value, _bindings[index].ValueLength, value);

    private bool NamespacesEqual(int left, int right)
    {
        if (left == right) return true;
        if (left < 0) return NamespaceEquals(right, left == -1 ? "" : XmlNamespace);
        if (right < 0) return NamespaceEquals(left, right == -1 ? "" : XmlNamespace);
        Binding a = _bindings![left], b = _bindings[right];
        int count = Decode(a.Value, a.ValueLength, true, true, default, false);
        if (count != Decode(b.Value, b.ValueLength, true, true, default, false)) return false;
        char[] first = ArrayPool<char>.Shared.Rent(Math.Max(1, count));
        char[] second = ArrayPool<char>.Shared.Rent(Math.Max(1, count));
        try
        {
            Decode(a.Value, a.ValueLength, true, true, first.AsSpan(0, count), true);
            Decode(b.Value, b.ValueLength, true, true, second.AsSpan(0, count), true);
            return first.AsSpan(0, count).SequenceEqual(second.AsSpan(0, count));
        }
        finally { ArrayPool<char>.Shared.Return(first); ArrayPool<char>.Shared.Return(second); }
    }

    private bool ValueEquals(int start, int length, string expected)
    {
        int count = Decode(start, length, true, true, default, false);
        if (count != expected.Length) return false;
        char[]? rented = null;
        Span<char> chars = count <= 256 ? stackalloc char[count] : (rented = ArrayPool<char>.Shared.Rent(count)).AsSpan(0, count);
        try { Decode(start, length, true, true, chars, true); return chars.SequenceEqual(expected.AsSpan()); }
        finally { if (rented is not null) ArrayPool<char>.Shared.Return(rented); }
    }

    private readonly int Decode(int start, int length, bool attribute, bool entities, scoped Span<char> destination, bool write)
    {
        int end = start + length, count = 0;
        for (int i = start; i < end;)
        {
            int scalar;
            byte b = _input[i];
            if (b == (byte)'&' && entities)
            {
                int entityStart = ++i;
                while (i < end && _input[i] != (byte)';') i++;
                if (i == end) throw ValueError(entityStart - 1, "Unterminated entity reference.");
                ReadOnlySpan<byte> entity = _input.Slice(entityStart, i++ - entityStart);
                if (entity.SequenceEqual("amp"u8)) scalar = '&';
                else if (entity.SequenceEqual("lt"u8)) scalar = '<';
                else if (entity.SequenceEqual("gt"u8)) scalar = '>';
                else if (entity.SequenceEqual("apos"u8)) scalar = '\'';
                else if (entity.SequenceEqual("quot"u8)) scalar = '"';
                else if (entity.Length > 1 && entity[0] == (byte)'#')
                {
                    int radix = 10, digitStart = 1;
                    if (entity[1] == (byte)'x') { radix = 16; digitStart = 2; }
                    scalar = 0;
                    if (digitStart == entity.Length) throw ValueError(entityStart, "Empty character reference.");
                    for (int j = digitStart; j < entity.Length; j++)
                    {
                        byte c = entity[j];
                        int digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
                        if (digit < 0 || digit >= radix || scalar > (0x10ffff - digit) / radix) throw ValueError(entityStart, "Invalid character reference.");
                        scalar = scalar * radix + digit;
                    }
                    if (!IsXmlCharacter(scalar)) throw ValueError(entityStart, "Invalid XML character reference.");
                }
                else throw ValueError(entityStart, "Unknown entity reference.");
            }
            else if (b == 13)
            {
                i++; if (i < end && _input[i] == 10) i++;
                scalar = attribute ? ' ' : '\n';
            }
            else if (attribute && (b == 10 || b == 9)) { i++; scalar = ' '; }
            else
            {
                if (Rune.DecodeFromUtf8(_input.Slice(i, end - i), out Rune rune, out int used) != OperationStatus.Done) throw ValueError(i, "Invalid UTF8.");
                scalar = rune.Value; i += used;
            }
            int needed = scalar > 0xffff ? 2 : 1;
            if (write)
            {
                if (needed > destination.Length - count) throw new FormatException("The XML scalar exceeds the destination buffer.");
                if (needed == 1) destination[count] = (char)scalar;
                else { int n = scalar - 0x10000; destination[count] = (char)(0xd800 + (n >> 10)); destination[count + 1] = (char)(0xdc00 + (n & 1023)); }
            }
            count += needed;
        }
        return count;
    }

    private readonly XmlWeaveException ValueError(int position, string message) => ErrorAt(position, message);
        private readonly void CheckValue()
    {
        if (Token is not (XmlWeaveToken.Text or XmlWeaveToken.Attribute)) throw Error("The current token has no scalar value.");
    }
    private bool At(ReadOnlySpan<byte> text) => _input.Slice(_position).StartsWith(text);
    private bool IsDeclaration(int start, int length) => BytesEqual(start, length, "xmlns") || (length > 6 && _input.Slice(start, 6).SequenceEqual("xmlns:"u8));
    private int Colon(int start, int length) => _input.Slice(start, length).IndexOf((byte)':');
    private bool BytesEqual(int start, int length, string value)
    {
        ReadOnlySpan<byte> bytes = _input.Slice(start, length);
        int offset = 0;
        for (int i = 0; i < bytes.Length;)
        {
            if (Rune.DecodeFromUtf8(bytes.Slice(i), out Rune rune, out int used) != OperationStatus.Done) return false;
            if (rune.Utf16SequenceLength > value.Length - offset) return false;
            if (rune.Value <= 0xffff) { if (value[offset++] != (char)rune.Value) return false; }
            else
            {
                int n = rune.Value - 0x10000;
                if (value[offset++] != (char)(0xd800 + (n >> 10)) || value[offset++] != (char)(0xdc00 + (n & 1023))) return false;
            }
            i += used;
        }
        return offset == value.Length;
    }
    private bool SkipSpace()
    {
        int start = _position;
        while (_position < _input.Length && IsSpace(_input[_position])) _position++;
        return start != _position;
    }
    private void Expect(byte value)
    {
        if (_position >= _input.Length || _input[_position] != value) throw ValueError(_position, "Unexpected XML syntax.");
        _position++;
    }
    private int ReadName(bool qualified = true)
    {
        int start = _position;
        bool first = true, colon = false;
        while (_position < _input.Length)
        {
            Rune.DecodeFromUtf8(_input.Slice(_position), out Rune rune, out int used);
            int c = rune.Value;
            if (c == ':' && !qualified) { first = false; _position += used; continue; }
            if (c == ':')
            {
                if (first || colon) throw ValueError(_position, "Invalid qualified XML name.");
                colon = true; first = true; _position += used; continue;
            }
            if (!(first ? IsNameStart(c) : IsNameStart(c) || c == '-' || c == '.' || c >= '0' && c <= '9' || c == 0xb7 || c >= 0x300 && c <= 0x36f || c >= 0x203f && c <= 0x2040)) break;
            first = false; _position += used;
        }
        if (first) throw ValueError(_position, "Invalid XML name.");
        return _position - start;
    }
    private void SkipComment()
    {
        int start = _position + 4;
        int length = _input.Slice(start).IndexOf("-->"u8);
        if (length < 0) throw Error("Unterminated XML comment.");
        ReadOnlySpan<byte> content = _input.Slice(start, length);
        if (content.IndexOf("--"u8) >= 0 || (length > 0 && content[length - 1] == (byte)'-')) throw Error("Invalid XML comment.");
        _position = start + length + 3;
    }
    private void SkipInstruction()
    {
        int opening = _position;
        _position += 2;
        int target = _position, length = ReadName(false);
        bool declaration = BytesEqual(target, length, "xml");
        if (!declaration && length == 3 && (_input[target] | 32) == 'x' && (_input[target + 1] | 32) == 'm' && (_input[target + 2] | 32) == 'l') throw Error("The XML processing instruction target is reserved.");
        if (declaration)
        {
            int documentStart = _input.StartsWith("\uFEFF"u8) ? 3 : 0;
            if (opening != documentStart) throw Error("The XML declaration must appear first.");
            ReadDeclaration();
            return;
        }
        if (At("?>"u8)) { _position += 2; return; }
        if (!SkipSpace()) throw Error("Expected processing instruction whitespace.");
        int tail = _input.Slice(_position).IndexOf("?>"u8);
        if (tail < 0) throw Error("Unterminated processing instruction.");
        _position += tail + 2;
    }
    private void ReadDeclaration()
    {
        if (!SkipSpace()) throw Error("Invalid XML declaration.");
        ReadDeclarationField("version", "1.0", null);
        bool space = SkipSpace();
        if (At("encoding"u8))
        {
            if (!space) throw Error("Invalid XML declaration.");
            ReadDeclarationField("encoding", "UTF-8", "utf-8"); space = SkipSpace();
        }
        if (At("standalone"u8))
        {
            if (!space) throw Error("Invalid XML declaration.");
            ReadDeclarationField("standalone", "yes", "no"); SkipSpace();
        }
        Expect((byte)'?'); Expect((byte)'>');
    }
    private void ReadDeclarationField(string name, string expected, string? alternative)
    {
        int start = _position, length = ReadName();
        if (!BytesEqual(start, length, name)) throw Error("Invalid XML declaration field.");
        SkipSpace(); Expect((byte)'='); SkipSpace();
        if (_position >= _input.Length || (_input[_position] != '\'' && _input[_position] != '"')) throw Error("Invalid XML declaration value.");
        byte quote = _input[_position++]; start = _position;
        while (_position < _input.Length && _input[_position] != quote) _position++;
        length = _position - start;
        bool matches = BytesEqual(start, length, expected) || (alternative is not null && BytesEqual(start, length, alternative));
        if (name == "encoding" && length == 5)
        {
            ReadOnlySpan<byte> encoding = _input.Slice(start, length);
            matches = (encoding[0] | 32) == 'u' && (encoding[1] | 32) == 't' && (encoding[2] | 32) == 'f' && encoding[3] == '-' && encoding[4] == '8';
        }
        if (!matches) throw Error("Unsupported XML declaration value.");
        Expect(quote);
    }
    private static bool IsSpace(byte c) => c is 9 or 10 or 13 or 32;
    private static bool IsXmlCharacter(int c) => c is 9 or 10 or 13 || c >= 0x20 && c <= 0xd7ff || c >= 0xe000 && c <= 0xfffd || c >= 0x10000 && c <= 0x10ffff;
    private static bool IsNameStart(int c) => c == '_' || c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z'
        || c >= 0xc0 && c <= 0xd6 || c >= 0xd8 && c <= 0xf6 || c >= 0xf8 && c <= 0x2ff
        || c >= 0x370 && c <= 0x37d || c >= 0x37f && c <= 0x1fff || c >= 0x200c && c <= 0x200d
        || c >= 0x2070 && c <= 0x218f || c >= 0x2c00 && c <= 0x2fef || c >= 0x3001 && c <= 0xd7ff
        || c >= 0xf900 && c <= 0xfdcf || c >= 0xfdf0 && c <= 0xfffd || c >= 0x10000 && c <= 0xeffff;
    private static void Ensure<T>(ref T[]? array, int count)
    {
        if (array is not null && array.Length >= count) return;
        T[] replacement = ArrayPool<T>.Shared.Rent(array is null ? Math.Max(16, count) : Math.Max(count, array.Length * 2));
        if (array is not null) { array.AsSpan().CopyTo(replacement); ArrayPool<T>.Shared.Return(array); }
        array = replacement;
    }
}
