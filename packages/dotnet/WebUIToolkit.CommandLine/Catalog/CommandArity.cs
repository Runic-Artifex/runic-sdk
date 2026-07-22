using System;

namespace WebUIToolkit.CommandLine;

/// <summary>Defines the minimum and maximum number of values accepted by a command parameter.</summary>
public readonly record struct CommandArity
{
    /// <summary>Initializes a command arity.</summary>
    /// <param name="minimum">The inclusive minimum value count.</param>
    /// <param name="maximum">The inclusive maximum value count, or null when unbounded.</param>
    public CommandArity(int minimum, int? maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimum);
        if (maximum is int finiteMaximum && finiteMaximum < minimum)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }

        Minimum = minimum;
        Maximum = maximum;
    }

    /// <summary>Gets the inclusive minimum value count.</summary>
    public int Minimum { get; }

    /// <summary>Gets the inclusive maximum value count, or null when unbounded.</summary>
    public int? Maximum { get; }

    /// <summary>Gets an arity accepting no values.</summary>
    public static CommandArity Zero { get; } = new(0, 0);

    /// <summary>Gets an arity accepting zero or one value.</summary>
    public static CommandArity ZeroOrOne { get; } = new(0, 1);

    /// <summary>Gets an arity accepting exactly one value.</summary>
    public static CommandArity ExactlyOne { get; } = new(1, 1);

    /// <summary>Gets an arity accepting one or more values.</summary>
    public static CommandArity OneOrMore { get; } = new(1, null);

    /// <summary>Gets an arity accepting zero or more values.</summary>
    public static CommandArity ZeroOrMore { get; } = new(0, null);

    /// <summary>Determines whether a value count is accepted.</summary>
    public bool Accepts(int count) => count >= Minimum && (Maximum is null || count <= Maximum.Value);
}
