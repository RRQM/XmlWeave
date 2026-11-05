using System;

namespace XmlWeave;

/// <summary>包含 XML 路径、行号及列号的格式错误。</summary>
public sealed class XmlWeaveException : FormatException
{
    /// <summary>创建 XML 格式错误。</summary>
    public XmlWeaveException(string message, string path, int line, int column, Exception? inner = null)
        : base($"{message} ({path}, 行 {line}, 列 {column})", inner)
    { Path = path; Line = line; Column = column; }
    /// <summary>错误节点路径。</summary>
    public string Path { get; }
    /// <summary>行号。</summary>
    public int Line { get; }
    /// <summary>列号。</summary>
    public int Column { get; }
}
