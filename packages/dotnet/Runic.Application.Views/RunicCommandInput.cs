namespace Runic.Application.Views;

/// <summary>
/// Declares the browser argument shape for a plain <see cref="System.Windows.Input.ICommand"/>.
/// The command itself remains synchronous; the bridge only uses this metadata to
/// decode the wire argument and to make the generated TypeScript surface typed.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class RunicCommandInputAttribute(Type input) : Attribute
{
    /// <summary>The CLR type of the command's browser argument.</summary>
    public Type Input { get; } = input ?? throw new ArgumentNullException(nameof(input));
}
