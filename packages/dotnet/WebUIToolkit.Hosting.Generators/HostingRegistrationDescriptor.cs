using System;
using System.Collections.Generic;

namespace WebUIToolkit.Hosting.Generators;

/// <summary>
/// Identifies a registration that a future hosting generator can emit.
/// </summary>
public enum HostingRegistrationKind
{
    /// <summary>
    /// No registration kind has been selected.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// A WebUi runtime adapter registration.
    /// </summary>
    WebUiRuntimeAdapter = 1,

    /// <summary>
    /// A root view registration.
    /// </summary>
    RootView = 2,

    /// <summary>
    /// A UI session registration.
    /// </summary>
    Session = 3,

    /// <summary>
    /// A command registration.
    /// </summary>
    Command = 4,

    /// <summary>
    /// A launch token registration.
    /// </summary>
    LaunchToken = 5,

    /// <summary>
    /// A startup participant registration.
    /// </summary>
    StartupParticipant = 6,

    /// <summary>
    /// An asynchronous lifecycle callback registration.
    /// </summary>
    LifecycleCallback = 7,

    /// <summary>
    /// A generated JSON serializer context registration.
    /// </summary>
    SerializerContext = 8,

    /// <summary>
    /// An application mode runner registration.
    /// </summary>
    ModeRunner = 9,
}

/// <summary>
/// Describes one validated, dependency-neutral hosting registration.
/// </summary>
/// <remarks>
/// Metadata names use compiler-style fully qualified names. Empty keys identify
/// unkeyed registrations. Empty factory method names request direct construction.
/// The descriptor deliberately carries no source path, runtime <see cref="Type"/>,
/// Roslyn symbol, or dependency-injection object.
/// </remarks>
public sealed class HostingRegistrationDescriptor
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HostingRegistrationDescriptor"/> class.
    /// </summary>
    /// <param name="kind">The semantic registration kind.</param>
    /// <param name="registrationKey">The canonical key, or an empty string for an unkeyed registration.</param>
    /// <param name="serviceTypeMetadataName">The fully qualified service type metadata name.</param>
    /// <param name="implementationTypeMetadataName">The fully qualified implementation type metadata name.</param>
    /// <param name="factoryMethodMetadataName">The fully qualified factory method metadata name, or an empty string.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a supported kind.</exception>
    /// <exception cref="ArgumentNullException">A string argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A required metadata name is empty, or canonical text contains surrounding whitespace or control characters.
    /// </exception>
    public HostingRegistrationDescriptor(
        HostingRegistrationKind kind,
        string registrationKey,
        string serviceTypeMetadataName,
        string implementationTypeMetadataName,
        string factoryMethodMetadataName)
    {
        if (!IsSupportedKind(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A supported registration kind is required.");
        }

        if (registrationKey is null)
        {
            throw new ArgumentNullException(nameof(registrationKey));
        }

        if (serviceTypeMetadataName is null)
        {
            throw new ArgumentNullException(nameof(serviceTypeMetadataName));
        }

        if (implementationTypeMetadataName is null)
        {
            throw new ArgumentNullException(nameof(implementationTypeMetadataName));
        }

        if (factoryMethodMetadataName is null)
        {
            throw new ArgumentNullException(nameof(factoryMethodMetadataName));
        }

        ValidateOptionalCanonicalText(registrationKey, nameof(registrationKey));
        ValidateRequiredCanonicalText(serviceTypeMetadataName, nameof(serviceTypeMetadataName));
        ValidateRequiredCanonicalText(implementationTypeMetadataName, nameof(implementationTypeMetadataName));
        ValidateOptionalCanonicalText(factoryMethodMetadataName, nameof(factoryMethodMetadataName));

        Kind = kind;
        RegistrationKey = registrationKey;
        ServiceTypeMetadataName = serviceTypeMetadataName;
        ImplementationTypeMetadataName = implementationTypeMetadataName;
        FactoryMethodMetadataName = factoryMethodMetadataName;
    }

    /// <summary>
    /// Gets the semantic registration kind.
    /// </summary>
    public HostingRegistrationKind Kind { get; }

    /// <summary>
    /// Gets the canonical registration key, or an empty string for an unkeyed registration.
    /// </summary>
    public string RegistrationKey { get; }

    /// <summary>
    /// Gets the fully qualified service type metadata name.
    /// </summary>
    public string ServiceTypeMetadataName { get; }

    /// <summary>
    /// Gets the fully qualified implementation type metadata name.
    /// </summary>
    public string ImplementationTypeMetadataName { get; }

    /// <summary>
    /// Gets the fully qualified factory method metadata name, or an empty string for direct construction.
    /// </summary>
    public string FactoryMethodMetadataName { get; }

    private static bool IsSupportedKind(HostingRegistrationKind kind) =>
        kind >= HostingRegistrationKind.WebUiRuntimeAdapter && kind <= HostingRegistrationKind.ModeRunner;

    private static void ValidateRequiredCanonicalText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty canonical metadata name is required.", parameterName);
        }

        ValidateOptionalCanonicalText(value, parameterName);
    }

    private static void ValidateOptionalCanonicalText(string value, string parameterName)
    {
        if (value.Length == 0)
        {
            return;
        }

        if (!StringComparer.Ordinal.Equals(value, value.Trim()))
        {
            throw new ArgumentException("Canonical text cannot contain leading or trailing whitespace.", parameterName);
        }

        foreach (char character in value)
        {
            if (char.IsControl(character))
            {
                throw new ArgumentException("Canonical text cannot contain control characters.", parameterName);
            }
        }
    }
}

/// <summary>
/// Orders registration descriptors independently of discovery or input order.
/// </summary>
public sealed class HostingRegistrationDescriptorComparer : IComparer<HostingRegistrationDescriptor>
{
    /// <summary>
    /// Gets the shared deterministic comparer.
    /// </summary>
    public static HostingRegistrationDescriptorComparer Instance { get; } = new();

    private HostingRegistrationDescriptorComparer()
    {
    }

    /// <inheritdoc/>
    public int Compare(HostingRegistrationDescriptor? x, HostingRegistrationDescriptor? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var result = x.Kind.CompareTo(y.Kind);
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(x.RegistrationKey, y.RegistrationKey);
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(x.ServiceTypeMetadataName, y.ServiceTypeMetadataName);
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(x.ImplementationTypeMetadataName, y.ImplementationTypeMetadataName);
        return result != 0
            ? result
            : StringComparer.Ordinal.Compare(x.FactoryMethodMetadataName, y.FactoryMethodMetadataName);
    }
}
