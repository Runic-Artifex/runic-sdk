using System.ComponentModel;
using System.Reflection;

namespace Runic.Application.Views.Generated;

/// <summary>
/// Checks that generated Bridge code matches the generated-code support API of this runtime (RUNICBRIDGE038).
/// </summary>
/// <remarks>
/// <para>
/// The generated <c>AddRunicBridges()</c> calls <see cref="RequireContract"/> before it registers any Bridge, with the
/// contract version its generator wrote. The version changes only when the support API breaks binary compatibility,
/// so a library that an earlier minor release of the same major generated keeps loading. A library whose Bridges
/// another major release generated fails there with <see cref="RunicBridgeContractException"/>, before a Bridge type
/// is loaded. The method keeps this signature in every release, so the call itself never fails to bind.
/// </para>
/// <para>
/// Bridges from before 0.19 make no such call. Host adapters wrap their root Bridge factory with
/// <see cref="StaleBridge"/>, which turns the <see cref="TypeLoadException"/> or <see cref="MissingMemberException"/>
/// of such a Bridge into the same exception when the first Window or View attaches it.
/// </para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class BridgeGeneratedCode
{
    // The generated-code contract this runtime implements. 1 was the generic Bridge runtime up to 0.18, which wrote no
    // version; 2 is the non-generic runtime of 0.19 (#901). Raise it, and MinimumContractVersion with it, only for a
    // break that code an earlier generator wrote cannot survive; the generator writes ContractVersion.
    internal const int ContractVersion = 2;

    // The oldest contract this runtime still runs.
    internal const int MinimumContractVersion = 2;

    /// <summary>
    /// Throws <see cref="RunicBridgeContractException"/> when <paramref name="contractVersion"/> is not a generated-code
    /// contract this runtime runs. The generated <c>AddRunicBridges()</c> calls it first.
    /// </summary>
    /// <param name="contractVersion">The contract version the generator wrote.</param>
    /// <param name="generatedAssembly">The assembly that contains the generated Bridges.</param>
    public static void RequireContract(int contractVersion, Assembly generatedAssembly)
    {
        if (contractVersion is >= MinimumContractVersion and <= ContractVersion) return;
        ArgumentNullException.ThrowIfNull(generatedAssembly);
        throw RunicBridgeContractException.ForContract(NameOf(generatedAssembly), contractVersion);
    }

    /// <summary>
    /// Returns a <see cref="RunicBridgeContractException"/> for <paramref name="error"/> when a generated Bridge factory
    /// failed because its Bridge was generated against another Runic release, otherwise <see langword="null"/>.
    /// </summary>
    /// <param name="error">The exception the factory threw.</param>
    /// <param name="factory">The generated Bridge factory, which names the assembly to rebuild.</param>
    /// <param name="viewModel">The ViewModel type the factory attaches.</param>
    /// <remarks>Use it as an exception filter around the factory call: <c>catch (Exception e) when (BridgeGeneratedCode.StaleBridge(e, factory, typeof(T)) is { } stale) { throw stale; }</c>.</remarks>
    public static RunicBridgeContractException? StaleBridge(Exception error, Delegate factory, Type viewModel)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(viewModel);
        if (!IsSupportApiMismatch(error)) return null;
        return RunicBridgeContractException.ForStaleBridge(FactoryAssembly(factory) ?? NameOf(viewModel.Assembly),
            viewModel.FullName ?? viewModel.Name, error);
    }

    // The support API of this runtime lacks a type or member that the generated code was compiled against. JIT reports
    // it as TypeLoadException naming the missing type (its base ViewModelBridge`1, for a 0.18 Bridge), or as
    // MissingMethodException/MissingFieldException naming the member; both name a type of this assembly.
    private static bool IsSupportApiMismatch(Exception error) => error switch
    {
        TypeLoadException load => NamesThisAssembly(load.TypeName) || NamesThisAssembly(load.Message),
        MissingMemberException missing => NamesThisAssembly(missing.Message),
        _ => false,
    };

    private static bool NamesThisAssembly(string? text) =>
        text is not null && text.Contains("Runic.Application.Views", StringComparison.Ordinal);

    // The generated factory is a lambda in the composition class of the assembly that holds the Bridges. Only the
    // error path reads its method; where the runtime has no metadata for it, the ViewModel's assembly is named instead.
    private static string? FactoryAssembly(Delegate factory)
    {
        try
        {
            return factory.Method.Module.Assembly is { } assembly ? NameOf(assembly) : null;
        }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException or MemberAccessException)
        {
            return null;
        }
    }

    private static string NameOf(Assembly assembly) => assembly.GetName().Name ?? assembly.FullName ?? "an assembly";
}
