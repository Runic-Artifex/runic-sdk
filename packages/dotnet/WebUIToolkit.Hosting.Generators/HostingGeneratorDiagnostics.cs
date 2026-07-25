using System;
using System.Collections.Generic;

namespace WebUIToolkit.Hosting.Generators;

/// <summary>
/// Identifies the tool-independent severity of a hosting generator diagnostic.
/// </summary>
public enum HostingGeneratorDiagnosticSeverity
{
    /// <summary>
    /// The diagnostic warns about a supported but unsafe or discouraged configuration.
    /// </summary>
    Warning = 0,

    /// <summary>
    /// The diagnostic prevents generation of a valid hosting registration set.
    /// </summary>
    Error = 1,
}

/// <summary>
/// Describes stable hosting generator diagnostic metadata without Roslyn dependencies.
/// </summary>
public sealed class HostingGeneratorDiagnosticDescriptor
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HostingGeneratorDiagnosticDescriptor"/> class.
    /// </summary>
    /// <param name="id">The stable <c>WUTHOST</c> identity.</param>
    /// <param name="severity">The diagnostic severity.</param>
    /// <param name="title">The short diagnostic title.</param>
    /// <param name="messageFormat">The invariant composite message format.</param>
    /// <param name="remediation">The actionable remediation guidance.</param>
    /// <exception cref="ArgumentNullException">A string argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A string argument is empty or consists only of white-space.</exception>
    public HostingGeneratorDiagnosticDescriptor(
        string id,
        HostingGeneratorDiagnosticSeverity severity,
        string title,
        string messageFormat,
        string remediation)
    {
        Id = RequireText(id, nameof(id));
        Severity = severity;
        Title = RequireText(title, nameof(title));
        MessageFormat = RequireText(messageFormat, nameof(messageFormat));
        Remediation = RequireText(remediation, nameof(remediation));
    }

    /// <summary>
    /// Gets the stable <c>WUTHOST</c> identity.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the tool-independent severity.
    /// </summary>
    public HostingGeneratorDiagnosticSeverity Severity { get; }

    /// <summary>
    /// Gets the short diagnostic title.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the invariant composite message format.
    /// </summary>
    public string MessageFormat { get; }

    /// <summary>
    /// Gets the actionable remediation guidance.
    /// </summary>
    public string Remediation { get; }

    private static string RequireText(string value, string parameterName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value;
    }
}

/// <summary>
/// Provides the allocated hosting generator diagnostic catalog.
/// </summary>
public static class HostingGeneratorDiagnostics
{
    /// <summary>
    /// Gets the diagnostic for missing or ambiguous WebUi runtime adapters.
    /// </summary>
    public static HostingGeneratorDiagnosticDescriptor WUTHOST0001 { get; } = new(
        "WUTHOST0001",
        HostingGeneratorDiagnosticSeverity.Error,
        "WebUi runtime adapter registration is invalid",
        "Exactly one WebUi runtime adapter must be registered; found '{0}'.",
        "Register exactly one WebUi runtime adapter for UI mode.");

    /// <summary>
    /// Gets the diagnostic for a missing UI root view or session.
    /// </summary>
    public static HostingGeneratorDiagnosticDescriptor WUTHOST0002 { get; } = new(
        "WUTHOST0002",
        HostingGeneratorDiagnosticSeverity.Error,
        "UI root registration is missing",
        "UI mode requires one root view and one session registration.",
        "Register a typed root view and session before enabling UI mode.");

    /// <summary>
    /// Gets the diagnostic for a duplicate command or launch token.
    /// </summary>
    public static HostingGeneratorDiagnosticDescriptor WUTHOST0003 { get; } = new(
        "WUTHOST0003",
        HostingGeneratorDiagnosticSeverity.Error,
        "Command or launch token is duplicated",
        "The command or launch token '{0}' is registered more than once.",
        "Assign a unique ordinal key to every command and launch token.");

    /// <summary>
    /// Gets the diagnostic for an inaccessible generated factory target.
    /// </summary>
    public static HostingGeneratorDiagnosticDescriptor WUTHOST0004 { get; } = new(
        "WUTHOST0004",
        HostingGeneratorDiagnosticSeverity.Error,
        "Generated factory target is inaccessible",
        "A generated factory cannot access constructor or dependency '{0}'.",
        "Expose an accessible constructor and dependency graph or provide an explicit factory.");

    /// <summary>
    /// Gets the diagnostic for reflection fallback in an AOT-enabled application.
    /// </summary>
    public static HostingGeneratorDiagnosticDescriptor WUTHOST0005 { get; } = new(
        "WUTHOST0005",
        HostingGeneratorDiagnosticSeverity.Warning,
        "Reflection fallback is unsafe for AOT",
        "Registration '{0}' uses reflection fallback while AOT is enabled.",
        "Replace reflection fallback with a typed generated or explicit factory.");

    /// <summary>
    /// Gets the diagnostic for missing or ambiguous frontend entry points.
    /// </summary>
    public static HostingGeneratorDiagnosticDescriptor WUTHOST0006 { get; } = new(
        "WUTHOST0006",
        HostingGeneratorDiagnosticSeverity.Error,
        "Frontend entry point registration is invalid",
        "Exactly one frontend entry point must be selected; found '{0}'.",
        "Mark exactly one frontend asset as the application entry point.");

    /// <summary>
    /// Gets the diagnostic for an asynchronous lifecycle callback without cancellation.
    /// </summary>
    public static HostingGeneratorDiagnosticDescriptor WUTHOST0007 { get; } = new(
        "WUTHOST0007",
        HostingGeneratorDiagnosticSeverity.Warning,
        "Lifecycle callback cannot observe cancellation",
        "Asynchronous lifecycle callback '{0}' has no cancellation-token parameter.",
        "Accept and observe a CancellationToken in the asynchronous lifecycle callback.");

    /// <summary>
    /// Gets every allocated generator diagnostic in identity order.
    /// </summary>
    public static IReadOnlyList<HostingGeneratorDiagnosticDescriptor> All { get; } =
        Array.AsReadOnly(
        [
            WUTHOST0001,
            WUTHOST0002,
            WUTHOST0003,
            WUTHOST0004,
            WUTHOST0005,
            WUTHOST0006,
            WUTHOST0007,
        ]);

    /// <summary>
    /// Finds an allocated diagnostic by its ordinal identity.
    /// </summary>
    /// <param name="id">The diagnostic identity.</param>
    /// <returns>The descriptor, or <see langword="null"/> when the identity is not allocated.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    public static HostingGeneratorDiagnosticDescriptor? Find(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        for (var index = 0; index < All.Count; index++)
        {
            var descriptor = All[index];
            if (string.Equals(descriptor.Id, id, StringComparison.Ordinal))
            {
                return descriptor;
            }
        }

        return null;
    }
}
