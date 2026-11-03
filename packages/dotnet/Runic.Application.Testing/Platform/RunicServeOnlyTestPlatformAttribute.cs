namespace Runic.Application.Testing;

/// <summary>
/// Marks an application's entry assembly as a test build that may use
/// <see cref="RunicTestPlatformServiceCollectionExtensions.AddRunicServeOnlyTestPlatform"/>.
/// </summary>
/// <remarks>
/// Add it only in a test-only configuration, for example with
/// <c>&lt;AssemblyAttribute Include="Runic.Application.Testing.RunicServeOnlyTestPlatformAttribute" Condition="'$(Configuration)' == 'E2E'" /&gt;</c>.
/// Without it, the serve-only test platform refuses to start even when its environment variables are set.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class RunicServeOnlyTestPlatformAttribute : Attribute
{
}
