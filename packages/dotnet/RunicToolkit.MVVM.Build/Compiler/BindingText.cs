namespace RunicToolkit.MVVM.Build.Compiler;

/// <summary>Identifies a zero-based UTF-16 position in a binding source file.</summary>
public readonly record struct BindingSourcePosition
{
    /// <summary>Creates a source position.</summary>
    public BindingSourcePosition(int offset, int line, int column)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(line);
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        Offset = offset;
        Line = line;
        Column = column;
    }

    /// <summary>Gets the zero-based UTF-16 offset.</summary>
    public int Offset { get; }

    /// <summary>Gets the zero-based line.</summary>
    public int Line { get; }

    /// <summary>Gets the zero-based UTF-16 column.</summary>
    public int Column { get; }
}

/// <summary>Identifies an end-exclusive range in one logical binding source file.</summary>
public readonly record struct BindingSourceSpan
{
    /// <summary>Creates a source span.</summary>
    public BindingSourceSpan(string logicalPath, BindingSourcePosition start, BindingSourcePosition end)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalPath);
        if (end.Offset < start.Offset)
        {
            throw new ArgumentException("A source span cannot end before it starts.", nameof(end));
        }

        LogicalPath = logicalPath;
        Start = start;
        End = end;
    }

    /// <summary>Gets the deterministic logical path supplied by the caller.</summary>
    public string LogicalPath { get; }

    /// <summary>Gets the inclusive start.</summary>
    public BindingSourcePosition Start { get; }

    /// <summary>Gets the exclusive end.</summary>
    public BindingSourcePosition End { get; }

    /// <summary>Gets the UTF-16 length.</summary>
    public int Length => End.Offset - Start.Offset;
}
