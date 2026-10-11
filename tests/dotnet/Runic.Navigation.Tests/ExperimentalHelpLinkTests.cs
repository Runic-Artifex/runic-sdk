using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Runic.Navigation.Tests;

// W250-015: RUNICNAV001 links to its docs/experimental.md entry at the release tag the assembly
// ships in, never at main.
internal static class ExperimentalHelpLinkTests
{
    public static void Run()
    {
        var assembly = typeof(RunicNavigator).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        var expected = $"https://github.com/Runic-Artifex/runic-sdk/blob/v{version}/docs/experimental.md#{{0}}";
        const BindingFlags members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var marked = assembly.GetTypes()
            .SelectMany(type => type.GetMembers(members).Prepend<MemberInfo>(type))
            .Select(member => (Member: member, Attribute: member.GetCustomAttribute<ExperimentalAttribute>(inherit: false)))
            .Where(entry => entry.Attribute is not null)
            .ToArray();
        Require(marked.Any(entry => entry.Member == typeof(RunicNavigator)), "RunicNavigator is not marked experimental.");
        Require(marked.Any(entry => entry.Member.Name == nameof(RunicModelContext.Closed)), "RunicModelContext.Closed is not marked experimental.");
        foreach (var (member, attribute) in marked)
        {
            Require(attribute!.DiagnosticId == RunicNavigator.DiagnosticId, $"{member} uses {attribute.DiagnosticId}.");
            Require(attribute.UrlFormat == expected, $"{member} links to '{attribute.UrlFormat}', not '{expected}'.");
        }
        Require(string.Format(System.Globalization.CultureInfo.InvariantCulture, expected, RunicNavigator.DiagnosticId).EndsWith("/docs/experimental.md#RUNICNAV001", StringComparison.Ordinal),
            "The help link does not name the RUNICNAV001 anchor.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
