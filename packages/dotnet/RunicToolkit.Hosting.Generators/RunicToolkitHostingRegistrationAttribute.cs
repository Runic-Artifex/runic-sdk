using System;

namespace RunicToolkit.Hosting.Generators;

/// <summary>
/// Declares one closed hosting registration for deterministic compile-time generation.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RunicToolkitHostingRegistrationAttribute : Attribute
{
    /// <summary>Initializes one typed hosting registration.</summary>
    public RunicToolkitHostingRegistrationAttribute(
        HostingRegistrationKind kind,
        Type serviceType,
        Type implementationType)
    {
        Kind = kind;
        ServiceType = serviceType ?? throw new ArgumentNullException(nameof(serviceType));
        ImplementationType = implementationType
            ?? throw new ArgumentNullException(nameof(implementationType));
    }

    /// <summary>Gets the semantic registration kind.</summary>
    public HostingRegistrationKind Kind { get; }

    /// <summary>Gets the closed service type.</summary>
    public Type ServiceType { get; }

    /// <summary>Gets the closed implementation or serializer type.</summary>
    public Type ImplementationType { get; }

    /// <summary>Gets or sets the stable ordinal key or token.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an accessible static factory method on the implementation type.
    /// An empty value requests direct parameterless construction.
    /// </summary>
    public string FactoryMethod { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the registration would use reflection fallback.</summary>
    public bool UsesReflection { get; set; }
}
