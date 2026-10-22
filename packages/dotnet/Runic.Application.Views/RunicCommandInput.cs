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

/// <summary>
/// Resolves a command's parameter from a wire key: the generated client sends the key, and the Bridge calls the
/// named ViewModel method to get the parameter. <see langword="null"/> means "no such object": the call is
/// rejected with the reason <c>unresolved-key</c> and the command does not run.
/// </summary>
/// <remarks>
/// <para>
/// Put it on the command property or on a CommunityToolkit <c>[RelayCommand]</c> method. <see cref="Resolver"/>
/// names one method of the ViewModel that is not overloaded, instance or static, <see langword="public"/> or
/// <see langword="internal"/>, with one of these shapes, where <c>TKey</c> is any supported command input type and
/// <c>P</c> the command's parameter type:
/// </para>
/// <code>
/// P? Find(TKey key)
/// ValueTask&lt;P?&gt; FindAsync(TKey key)                                       // also Task&lt;P?&gt;
/// ValueTask&lt;P?&gt; FindAsync(TKey key, CancellationToken cancellationToken)  // also Task&lt;P?&gt;
/// </code>
/// <para>
/// A synchronous resolver runs in the Bridge call's model turn. An asynchronous one starts in that turn; the command's
/// availability and execution then run in a new turn with the resolved object. <c>CanExecute</c> sees the resolved
/// object. A resolver must be free of side effects: it can run more than once for one request, for example for
/// <c>can…</c> and then the command, or for a repeated operation start. A plain <see cref="System.Windows.Input.ICommand"/>
/// takes this attribute instead of <see cref="RunicCommandInputAttribute"/>.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RunicCommandKeyAttribute(string resolver) : Attribute
{
    /// <summary>The name of the ViewModel method that resolves the key, such as <c>nameof(FindItem)</c>.</summary>
    public string Resolver { get; } = resolver ?? throw new ArgumentNullException(nameof(resolver));
}
