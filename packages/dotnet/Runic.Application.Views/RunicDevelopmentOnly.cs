using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace Runic.Application.Views;

/// <summary>
/// Marks a ViewModel, or one of its properties or commands, as a development tool, such as a mock-control panel or a
/// diagnostics member: a trimmed or NativeAOT publish leaves it out, together with the generated code only it needs.
/// </summary>
/// <remarks>
/// <para>
/// In builds, <c>dotnet run</c>, the debugger and untrimmed publishes, everything is as without the attribute. A trimmed
/// or NativeAOT publish turns <see cref="RunicDevelopmentOnly.IsSupported"/> off (the MSBuild property
/// <c>RunicDevelopmentOnlyViews</c>, <c>auto</c> by default; <c>true</c> keeps them in every publish):
/// </para>
/// <list type="bullet">
/// <item><description>A marked ViewModel has no Bridge: presenting it, or opening a Window or View of it, throws an
/// <see cref="InvalidOperationException"/> that names it.</description></item>
/// <item><description>A marked property is absent from the state, and the page cannot set or write it. A marked command,
/// on its property or on its CommunityToolkit <c>[RelayCommand]</c> method, and its <c>can…</c> and <c>is…Executing</c>
/// state are absent too; calling it is rejected as <c>unavailable</c>.</description></item>
/// </list>
/// <para>
/// The generated client is the same in every configuration, so the page's types describe a marked member as one that
/// may be absent (an optional state member) and a marked command as one that may be unavailable. Marking a member
/// changes the View's contract fingerprint; marking a ViewModel does not.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Method, Inherited = true)]
public sealed class RunicDevelopmentOnlyAttribute : Attribute;

/// <summary>Whether development-only views and members (<see cref="RunicDevelopmentOnlyAttribute"/>) are in this application.</summary>
public static class RunicDevelopmentOnly
{
    /// <summary>
    /// The feature switch that trimmed and NativeAOT publishes turn off (the MSBuild property
    /// <c>RunicDevelopmentOnlyViews</c>).
    /// </summary>
    public const string FeatureSwitch = "Runic.Application.Views.DevelopmentOnlyViews";

    /// <summary>
    /// <see langword="true"/> unless a trimmed or NativeAOT publish turned development-only views and members off. The
    /// trimmer treats it as a constant, so application code that only development needs can test it as well.
    /// </summary>
    [FeatureSwitchDefinition(FeatureSwitch)]
    public static bool IsSupported => !AppContext.TryGetSwitch(FeatureSwitch, out var enabled) || enabled;

    /// <summary>The failure of opening or presenting a development-only ViewModel that this publish left out.</summary>
    /// <param name="viewModel">The ViewModel's type.</param>
    /// <remarks>
    /// For generated code; not part of the application API. Generated Bridges call it, including those of libraries that
    /// an earlier minor release generated, so it stays while the Bridge contract version does. Applications test
    /// <see cref="IsSupported"/> instead.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static InvalidOperationException Removed(Type viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return new InvalidOperationException(
            $"{viewModel.FullName} is development-only ([RunicDevelopmentOnly]), and this trimmed or NativeAOT publish leaves its Bridge out. "
            + "Present it only when RunicDevelopmentOnly.IsSupported, or publish with RunicDevelopmentOnlyViews=true to keep it.");
    }
}
