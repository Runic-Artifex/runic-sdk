using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using TUnit.Core;

namespace Runic.Navigation.Tests;

// W250-029: the navigation API is supported, so no type or member of Runic.Navigation is
// marked [Experimental].
internal sealed class NavigationStabilityTests
{
    [Test]
    public void NoNavigationTypeOrMemberIsExperimental()
    {
        const BindingFlags members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var marked = typeof(RunicNavigator).Assembly.GetTypes()
            .SelectMany(type => type.GetMembers(members).Prepend<MemberInfo>(type))
            .Where(member => member.GetCustomAttribute<ExperimentalAttribute>(inherit: false) is not null)
            .Select(member => member is Type type ? type.FullName : $"{member.DeclaringType?.FullName}.{member.Name}")
            .ToArray();
        if (marked.Length > 0)
            throw new InvalidOperationException($"Experimental navigation members: {string.Join(", ", marked)}");
    }
}
