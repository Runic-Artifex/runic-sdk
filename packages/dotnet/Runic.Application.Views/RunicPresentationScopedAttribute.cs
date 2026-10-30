namespace Runic.Application.Views;

/// <summary>
/// Scopes a command to the presentation it was issued for: the page sends it with the presentation's epoch, and .NET
/// runs it only while that epoch is current. Put it on a Bridge command property or on a CommunityToolkit
/// <c>[RelayCommand]</c> method.
/// </summary>
/// <remarks>
/// <para>
/// An epoch begins with the route's first state, so content that is presented again has a new one, and with
/// <see cref="RunicPresentation.Renew(object)"/>, which a wizard's Next or Back calls when it changes the page. A
/// second Next clicked on the page that the first Next already left does nothing: its call resolves with the current
/// state, without an error.
/// </para>
/// <para>
/// The generated client lists the command in <c>scopedCommands</c>. Pages and hosts of older releases, which do not
/// use the <c>presentation-epoch/1</c> feature, run it as an ordinary command. Adding or removing the attribute changes
/// the View's contract fingerprint.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method)]
public sealed class RunicPresentationScopedAttribute : Attribute;
