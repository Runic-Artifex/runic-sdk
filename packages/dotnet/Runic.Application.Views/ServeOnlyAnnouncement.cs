namespace Runic.Application.Views;

// The lines a host writes to standard output when a Window serves in serve-only mode (Runic.Application.Views.Desktop
// README, "Automation"): RUNIC_APPLICATION_URL=<url>, which every release writes, then
// RUNIC_APPLICATION_WINDOW=<url> <type>, whose fields a parser splits at spaces. Runic Desktop and CS-WebUI share it.
internal static class ServeOnlyAnnouncement
{
    internal const string UrlPrefix = "RUNIC_APPLICATION_URL=";
    internal const string WindowPrefix = "RUNIC_APPLICATION_WINDOW=";

    // Both lines in one string, so a host writes them in one write and another window's lines cannot come between.
    // Concatenation, not formatting: TextWriter's formatting reads the current culture, which loads ICU.
    internal static string Lines(string url, Type? windowType) =>
        UrlPrefix + url + Environment.NewLine
        + (windowType is null ? "" : WindowPrefix + url + " " + WindowTypeName(windowType) + Environment.NewLine);

    // The Window type without spaces: its full name, or for a closed generic type the generic definition's full name
    // with the arguments' names without namespaces, such as MyApp.DocumentWindow`1[Report]. Type.FullName of a closed
    // generic type lists assembly-qualified arguments, which contain spaces.
    internal static string WindowTypeName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!type.IsConstructedGenericType) return Spaceless(type.FullName ?? type.Name);
        return Spaceless(type.GetGenericTypeDefinition().FullName ?? type.Name) + Arguments(type);
    }

    private static string SimpleName(Type type) =>
        type.IsConstructedGenericType ? type.GetGenericTypeDefinition().Name + Arguments(type) : type.Name;

    private static string Arguments(Type type) =>
        "[" + string.Join(",", type.GenericTypeArguments.Select(SimpleName)) + "]";

    // C# names have no spaces; another language's might.
    private static string Spaceless(string name) => name.Replace(" ", "%20", StringComparison.Ordinal);
}
