namespace Runic.Application.Views;

/// <summary>
/// Generates one TypeScript client for the ViewModels that implement this interface, so one web component can render
/// all of them: <c>{Name}State</c> and <c>{Name}Client</c> with the interface's members, and
/// <c>{Name}PageReference</c>, the page reference of any of their Views, whose <c>connect()</c> returns the shared
/// client. Each implementing ViewModel keeps its own client, and its page reference extends the shared one.
/// </summary>
/// <remarks>
/// <para>
/// <c>{Name}</c> is <see cref="Name"/>, or the interface's name without a leading <c>I</c>: <c>IResultPage</c> generates
/// <c>resultPage.ts</c> with <c>ResultPageState</c>, <c>ResultPageClient</c>, <c>ResultPagePageReference</c> and
/// <c>resultPageKinds</c>. The shared client has the interface's properties, including those of the interfaces it
/// extends: state, setters of the properties the interface declares settable, and commands. Methods and events of the
/// interface are not part of it.
/// </para>
/// <para>
/// Every ViewModel with a View that implements the interface must publish each of its properties alike: through a
/// property the ViewModel class declares and the Bridge publishes, with the interface's nullability, and with the same
/// generated TypeScript in every implementing ViewModel apart from parameter names. Otherwise the generator reports
/// <c>RUNICBRIDGE036</c>.
/// </para>
/// <para>
/// The attribute changes only the generated TypeScript types; the wire contract and the contract fingerprint stay the
/// same.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = false)]
public sealed class RunicSharedClientAttribute : Attribute
{
    /// <summary>
    /// The name of the generated module and types, a letter followed by letters and digits, such as <c>ResultPage</c>;
    /// <see langword="null"/>, the default, takes the interface's name without a leading <c>I</c>.
    /// </summary>
    public string? Name { get; set; }
}
