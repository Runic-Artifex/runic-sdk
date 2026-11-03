using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Runic.Navigation;

/// <summary>
/// Maps route paths, such as <c>customers/42/invoices/7</c>, to navigation targets, and the entries of a region back to
/// their route paths. <see cref="RunicNavigator.GetPath"/> and <see cref="RunicNavigator.RestoreAsync"/> use it to save and
/// rebuild navigation, and <see cref="NavigationRegion.OpenRouteAsync"/> to open a deep link.
/// </summary>
/// <remarks>
/// <see cref="NavigationRouteMap"/> is the implementation that <see cref="NavigationRouteServiceCollectionExtensions.AddRunicNavigationRoutes"/>
/// registers. Route paths are untrusted input when they come from a link, a notification or an address bar: a map creates
/// only the targets it declares, from typed values.
/// </remarks>
public interface INavigationRouteMap
{
    /// <summary>
    /// Creates a target for <paramref name="route"/> whose content <paramref name="region"/> can present, or returns
    /// <see langword="null"/> when no route matches. Creating a target constructs no content.
    /// </summary>
    /// <param name="region">The region the target is for; its <see cref="NavigationRegion.ContentType"/> must accept the content.</param>
    /// <param name="route">A route path, with or without a leading <c>/</c>.</param>
    INavigationTarget<object>? CreateTarget(NavigationRegion region, string route);

    /// <summary>Gets the route path of <paramref name="entry"/>, or <see langword="null"/> when no route describes it.</summary>
    /// <param name="entry">An entry of a region of any navigator.</param>
    string? GetRoute(NavigationEntry entry);
}

/// <summary>
/// The route map that <see cref="NavigationRouteMapBuilder"/> builds: typed route templates mapped to navigation targets.
/// </summary>
/// <remarks>
/// <para>
/// A template is segments separated by <c>/</c>: literals, compared without regard to case, and parameters
/// <c>{name}</c> (a string), <c>{name:int}</c>, <c>{name:long}</c>, <c>{name:guid}</c> or <c>{name:bool}</c>. Parameter
/// values are percent-encoded in a route path, and numbers use the invariant culture.
/// </para>
/// <para>
/// A route path matches the first route, in registration order, whose segments and parameter types match and whose content a
/// region accepts. An entry's route is the route its target was created for by this map, or else the first route, in
/// registration order, that describes the entry: by its content type and the input of its target, or by the
/// <c>values</c> function of <see cref="NavigationRouteMapBuilder.Map{T}(string, Func{IServiceProvider, NavigationRouteValues, INavigationTarget{T}}, Func{T, NavigationRouteValues?})"/>.
/// </para>
/// </remarks>
public sealed class NavigationRouteMap : INavigationRouteMap
{
    private readonly NavigationRouteRegistration[] _routes;

    internal NavigationRouteMap(NavigationRouteRegistration[] routes) => _routes = routes;

    /// <summary>Gets the route templates, in registration order.</summary>
    public IReadOnlyList<string> Templates => [.. _routes.Select(static route => route.Template.Text)];

    /// <inheritdoc />
    public INavigationTarget<object>? CreateTarget(NavigationRegion region, string route)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(route);
        if (!NavigationRouteTemplate.TrySplit(route, out var segments)) return null;
        foreach (var registration in _routes)
        {
            if (!region.ContentType.IsAssignableFrom(registration.ContentType)) continue;
            if (!registration.Template.TryMatch(segments, out var values)) continue;
            var services = region.Navigator.Services ?? EmptyServiceProvider.Instance;
            var target = registration.CreateTarget(services, values);
            if (target is null) continue;
            target.Route = registration.Template.Format(values);
            target.RouteMap = this;
            return (INavigationTarget<object>)target;
        }
        return null;
    }

    /// <inheritdoc />
    public string? GetRoute(NavigationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var target = entry.Core.Target;
        if (target is { Route: { } created } && ReferenceEquals(target.RouteMap, this)) return created;
        var content = entry.Core.Content;
        if (content is null) return null;
        foreach (var registration in _routes)
            if (registration.TryGetValues(content, target) is { } values) return registration.Template.Format(values);
        return null;
    }

    /// <summary>
    /// Gets whether <paramref name="route"/> matches one of the map's routes, and its canonical form: parameter values
    /// formatted again, literals as the template writes them, without a leading or trailing <c>/</c>.
    /// </summary>
    /// <param name="route">A route path.</param>
    /// <param name="canonical">The canonical route path.</param>
    /// <param name="values">The parameter values.</param>
    public bool TryMatch(string route, [NotNullWhen(true)] out string? canonical, [NotNullWhen(true)] out NavigationRouteValues? values)
    {
        ArgumentNullException.ThrowIfNull(route);
        canonical = null;
        values = null;
        if (!NavigationRouteTemplate.TrySplit(route, out var segments)) return false;
        foreach (var registration in _routes)
        {
            if (!registration.Template.TryMatch(segments, out var matched)) continue;
            canonical = registration.Template.Format(matched);
            values = matched;
            return true;
        }
        return false;
    }
}

/// <summary>Declares the routes of a <see cref="NavigationRouteMap"/>.</summary>
/// <remarks>
/// Each <c>Map</c> method checks its template at once and throws <see cref="ArgumentException"/> for a malformed template,
/// a parameter type the method can't pass, or a parameter declared twice.
/// </remarks>
public sealed class NavigationRouteMapBuilder
{
    private readonly List<NavigationRouteRegistration> _routes = [];

    /// <summary>
    /// Maps a template without parameters to <see cref="NavigationTarget.Create{T}()"/>. An entry whose content is exactly a
    /// <typeparamref name="T"/> created without input has this route.
    /// </summary>
    /// <typeparam name="T">The content type the navigator constructs.</typeparam>
    /// <param name="template">The route template, such as <c>customers</c>.</param>
    public NavigationRouteMapBuilder Map<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(string template)
        where T : class
    {
        var parsed = Parse(template, 0);
        _routes.Add(new NavigationRouteRegistration(parsed, typeof(T),
            (_, _) => new ActivatedNavigationTarget<T>(),
            (content, target) => content.GetType() == typeof(T) && target is not { HasInput: true } ? NavigationRouteValues.Empty : null));
        return this;
    }

    /// <summary>
    /// Maps a template with one parameter to <see cref="NavigationTarget.Create{T, TInput}(TInput)"/>, passing the parameter's
    /// value as the input. An entry whose content is exactly a <typeparamref name="T"/> created with a
    /// <typeparamref name="TInput"/> input has this route.
    /// </summary>
    /// <typeparam name="T">The content type the navigator constructs.</typeparam>
    /// <typeparam name="TInput">
    /// The parameter's type: <see cref="int"/>, <see cref="long"/>, <see cref="string"/>, <see cref="Guid"/> or
    /// <see cref="bool"/>, matching the template's constraint.
    /// </typeparam>
    /// <param name="template">The route template, such as <c>customers/{id:int}</c>.</param>
    public NavigationRouteMapBuilder Map<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T, TInput>(string template)
        where T : class, INavigationInput<TInput>
    {
        var parsed = Parse(template, 1);
        var parameter = parsed.Parameters[0];
        if (parameter.Type != typeof(TInput))
            throw new ArgumentException(
                $"The parameter {{{parameter.Name}}} of '{template}' is a {parameter.TypeName}, but the input is a {typeof(TInput).Name}. Declare {{{parameter.Name}:{NavigationRouteTemplate.ConstraintOf(typeof(TInput))}}}.",
                nameof(template));
        var name = parameter.Name;
        _routes.Add(new NavigationRouteRegistration(parsed, typeof(T),
            (_, values) => new ActivatedInputNavigationTarget<T, TInput>((TInput)values[name]),
            (content, target) => content.GetType() == typeof(T) && target is InputNavigationTargetCore<TInput> input && input.Input is not null
                ? new NavigationRouteValues().SetValue(name, input.Input)
                : null));
        return this;
    }

    /// <summary>
    /// Maps a template to <see cref="NavigationTarget.Create{T, TInput}(TInput)"/> with an input read from the parameters,
    /// such as a record of a customer and an invoice number. An entry whose content is exactly a <typeparamref name="T"/>
    /// created with a <typeparamref name="TInput"/> input has this route, with the parameters <paramref name="write"/> gives.
    /// </summary>
    /// <typeparam name="T">The content type the navigator constructs.</typeparam>
    /// <typeparam name="TInput">The input type.</typeparam>
    /// <param name="template">The route template, such as <c>customers/{id:int}/invoices/{invoice:int}</c>.</param>
    /// <param name="read">Creates the input from the parameter values.</param>
    /// <param name="write">Gives the parameter values of an input, or <see langword="null"/> when the route doesn't describe it.</param>
    public NavigationRouteMapBuilder Map<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T, TInput>(string template,
        Func<NavigationRouteValues, TInput> read, Func<TInput, NavigationRouteValues?> write)
        where T : class, INavigationInput<TInput>
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);
        var parsed = Parse(template, null);
        _routes.Add(new NavigationRouteRegistration(parsed, typeof(T),
            (_, values) => new ActivatedInputNavigationTarget<T, TInput>(read(values)),
            (content, target) => content.GetType() == typeof(T) && target is InputNavigationTargetCore<TInput> input ? write(input.Input) : null));
        return this;
    }

    /// <summary>
    /// Maps a template to targets that <paramref name="target"/> creates, such as a borrowed page from the window's
    /// services or a factory with a <see cref="NavigationEntryContext"/>. An entry whose content is a <typeparamref name="T"/>
    /// has this route when <paramref name="values"/> returns its parameter values.
    /// </summary>
    /// <typeparam name="T">The content type.</typeparam>
    /// <param name="template">The route template.</param>
    /// <param name="target">
    /// Creates the target from the navigator's <see cref="RunicNavigator.Services"/> (an empty provider without them) and
    /// the parameter values; it must not construct content (use a <c>Create</c> target) unless it borrows it. Return
    /// <see langword="null"/> to let a later route match.
    /// </param>
    /// <param name="values">
    /// Gives the parameter values of an entry's content, an empty <see cref="NavigationRouteValues"/> for a template without
    /// parameters, or <see langword="null"/> when the route doesn't describe it.
    /// </param>
    public NavigationRouteMapBuilder Map<T>(string template, Func<IServiceProvider, NavigationRouteValues, INavigationTarget<T>?> target,
        Func<T, NavigationRouteValues?> values) where T : class
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(values);
        var parsed = Parse(template, null);
        _routes.Add(new NavigationRouteRegistration(parsed, typeof(T),
            (services, routeValues) => target(services, routeValues) switch
            {
                null => null,
                NavigationTargetCore core => core,
                _ => throw new InvalidOperationException("Navigation targets must be created with NavigationTarget."),
            },
            (content, _) => content is T typed ? values(typed) : null));
        return this;
    }

    /// <summary>Builds the map. The builder can be used again; later routes don't change a built map.</summary>
    public NavigationRouteMap Build() => new([.. _routes]);

    private static NavigationRouteTemplate Parse(string template, int? parameters)
    {
        ArgumentNullException.ThrowIfNull(template);
        var parsed = NavigationRouteTemplate.Parse(template);
        if (parameters is { } count && parsed.Parameters.Count != count)
            throw new ArgumentException(
                count == 0
                    ? $"The route '{template}' has parameters; map it with an input or a values function."
                    : $"The route '{template}' must have exactly {count} parameter for this Map overload.",
                nameof(template));
        return parsed;
    }
}

/// <summary>Registers a <see cref="NavigationRouteMap"/>.</summary>
public static class NavigationRouteServiceCollectionExtensions
{
    /// <summary>
    /// Registers the route map that <paramref name="configure"/> declares as the singleton <see cref="INavigationRouteMap"/>
    /// and <see cref="NavigationRouteMap"/>, which <see cref="RunicNavigator.GetPath"/>, <see cref="RunicNavigator.RestoreAsync"/>
    /// and <see cref="NavigationRegion.OpenRouteAsync"/> resolve from the navigator's services. A map registered earlier stays.
    /// </summary>
    public static IServiceCollection AddRunicNavigationRoutes(this IServiceCollection services, Action<NavigationRouteMapBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new NavigationRouteMapBuilder();
        configure(builder);
        var map = builder.Build();
        services.TryAddSingleton(map);
        services.TryAddSingleton<INavigationRouteMap>(provider => provider.GetRequiredService<NavigationRouteMap>());
        return services;
    }
}

/// <summary>
/// The parameter values of a route: <see cref="int"/>, <see cref="long"/>, <see cref="string"/>, <see cref="Guid"/> and
/// <see cref="bool"/> values by parameter name, compared without regard to case.
/// </summary>
public sealed class NavigationRouteValues
{
    internal static readonly NavigationRouteValues Empty = new();

    private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sets an <see cref="int"/> parameter and returns these values.</summary>
    public NavigationRouteValues Set(string name, int value) => SetValue(name, value);

    /// <summary>Sets a <see cref="long"/> parameter and returns these values.</summary>
    public NavigationRouteValues Set(string name, long value) => SetValue(name, value);

    /// <summary>Sets a <see cref="string"/> parameter and returns these values.</summary>
    public NavigationRouteValues Set(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SetValue(name, value);
    }

    /// <summary>Sets a <see cref="Guid"/> parameter and returns these values.</summary>
    public NavigationRouteValues Set(string name, Guid value) => SetValue(name, value);

    /// <summary>Sets a <see cref="bool"/> parameter and returns these values.</summary>
    public NavigationRouteValues Set(string name, bool value) => SetValue(name, value);

    /// <summary>Gets an <see cref="int"/> parameter.</summary>
    /// <exception cref="KeyNotFoundException">There is no such parameter.</exception>
    /// <exception cref="InvalidCastException">The parameter has another type.</exception>
    public int GetInt32(string name) => (int)this[name];

    /// <summary>Gets a <see cref="long"/> parameter.</summary>
    public long GetInt64(string name) => (long)this[name];

    /// <summary>Gets a <see cref="string"/> parameter.</summary>
    public string GetString(string name) => (string)this[name];

    /// <summary>Gets a <see cref="Guid"/> parameter.</summary>
    public Guid GetGuid(string name) => (Guid)this[name];

    /// <summary>Gets a <see cref="bool"/> parameter.</summary>
    public bool GetBoolean(string name) => (bool)this[name];

    /// <summary>Gets a parameter's value.</summary>
    /// <exception cref="KeyNotFoundException">There is no such parameter.</exception>
    public object this[string name] => _values.TryGetValue(name, out var value)
        ? value
        : throw new KeyNotFoundException($"The route has no parameter '{name}'.");

    /// <summary>Gets the parameter names.</summary>
    public IReadOnlyCollection<string> Names => _values.Keys;

    /// <summary>Gets the number of parameters.</summary>
    public int Count => _values.Count;

    /// <summary>Gets a parameter's value, or returns <see langword="false"/> when there is no such parameter.</summary>
    public bool TryGetValue(string name, [MaybeNullWhen(false)] out object value) => _values.TryGetValue(name, out value);

    internal NavigationRouteValues SetValue(string name, object value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (ReferenceEquals(this, Empty)) throw new InvalidOperationException("The shared empty values can't change.");
        _values[name] = value;
        return this;
    }
}

/// <summary>Reads route paths from deep links: URLs with the application's scheme or of its web-link hosts.</summary>
public static class NavigationRouteLinks
{
    /// <summary>
    /// Gets the route path of <paramref name="uri"/>: for a URL with one of <paramref name="schemes"/>, such as
    /// <c>myapp://invoice/7</c> or <c>myapp:invoice/7</c>, its host and path (<c>invoice/7</c>); for an <c>https</c> URL of
    /// one of <paramref name="webLinkHosts"/>, its path. The query and the fragment are dropped.
    /// </summary>
    /// <returns><see langword="false"/> for any other URI, or one with user information or an empty route.</returns>
    /// <remarks>
    /// The URI is untrusted input: the route still has to match a route of the map, and the page it opens should confirm
    /// anything destructive. Schemes and hosts are compared without regard to case.
    /// </remarks>
    public static bool TryGetRoute(Uri uri, IEnumerable<string> schemes, [NotNullWhen(true)] out string? route,
        IEnumerable<string>? webLinkHosts = null)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(schemes);
        route = null;
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length > 0) return false;
        string raw;
        if (schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            // myapp://invoice/7 has the host "invoice"; myapp:invoice/7 has none.
            var host = uri.IsDefaultPort || uri.Port < 0 ? uri.Host : $"{uri.Host}:{uri.Port}";
            var path = uri.AbsolutePath;
            raw = host.Length == 0 ? path : $"{host}/{path.TrimStart('/')}";
        }
        else if (webLinkHosts is not null && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
            && webLinkHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            raw = uri.AbsolutePath;
        else return false;
        raw = raw.Trim('/');
        if (raw.Length == 0) return false;
        route = raw;
        return true;
    }
}

internal sealed class NavigationRouteRegistration(NavigationRouteTemplate template, Type contentType,
    Func<IServiceProvider, NavigationRouteValues, NavigationTargetCore?> createTarget,
    Func<object, NavigationTargetCore?, NavigationRouteValues?> getValues)
{
    public NavigationRouteTemplate Template { get; } = template;
    public Type ContentType { get; } = contentType;

    public NavigationTargetCore? CreateTarget(IServiceProvider services, NavigationRouteValues values) => createTarget(services, values);

    // The parameter values of an entry this route describes, checked against the template, or null.
    public NavigationRouteValues? TryGetValues(object content, NavigationTargetCore? target)
    {
        var values = getValues(content, target);
        return values is not null && Template.Accepts(values) ? values : null;
    }
}

internal sealed record NavigationRouteParameter(string Name, Type Type)
{
    public string TypeName => Type == typeof(string) ? "string" : NavigationRouteTemplate.ConstraintOf(Type);
}

internal sealed class NavigationRouteTemplate
{
    private readonly (string? Literal, NavigationRouteParameter? Parameter)[] _segments;

    private NavigationRouteTemplate(string text, (string?, NavigationRouteParameter?)[] segments, NavigationRouteParameter[] parameters)
    {
        Text = text;
        _segments = segments;
        Parameters = parameters;
    }

    public string Text { get; }
    public IReadOnlyList<NavigationRouteParameter> Parameters { get; }

    public static string ConstraintOf(Type type) =>
        type == typeof(int) ? "int"
        : type == typeof(long) ? "long"
        : type == typeof(Guid) ? "guid"
        : type == typeof(bool) ? "bool"
        : type == typeof(string) ? "string"
        : throw new ArgumentException($"A route parameter can't be a {type.Name}: use int, long, string, Guid or bool.");

    public static NavigationRouteTemplate Parse(string template)
    {
        var trimmed = template.Trim('/');
        if (trimmed.Length == 0) throw new ArgumentException("A route template needs at least one segment.", nameof(template));
        var parts = trimmed.Split('/');
        var segments = new (string?, NavigationRouteParameter?)[parts.Length];
        List<NavigationRouteParameter> parameters = [];
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Length == 0) throw new ArgumentException($"The route template '{template}' has an empty segment.", nameof(template));
            if (part[0] == '{')
            {
                if (part[^1] != '}' || part.Length < 3)
                    throw new ArgumentException($"The segment '{part}' of '{template}' is not a parameter: write {{name}} or {{name:type}}.", nameof(template));
                var body = part[1..^1];
                var colon = body.IndexOf(':');
                var name = colon < 0 ? body : body[..colon];
                var constraint = colon < 0 ? "string" : body[(colon + 1)..];
                if (name.Length == 0 || !name.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                    throw new ArgumentException($"The parameter name in '{part}' of '{template}' must be letters, digits or '_'.", nameof(template));
                var type = constraint switch
                {
                    "string" => typeof(string),
                    "int" => typeof(int),
                    "long" => typeof(long),
                    "guid" => typeof(Guid),
                    "bool" => typeof(bool),
                    _ => throw new ArgumentException($"The parameter type '{constraint}' in '{template}' is not one of int, long, string, guid or bool.", nameof(template)),
                };
                if (parameters.Any(parameter => string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException($"The parameter {{{name}}} appears twice in '{template}'.", nameof(template));
                var parameter = new NavigationRouteParameter(name, type);
                parameters.Add(parameter);
                segments[index] = (null, parameter);
            }
            else
            {
                if (part.AsSpan().IndexOfAny("{}?#") >= 0)
                    throw new ArgumentException($"The literal segment '{part}' of '{template}' contains a reserved character.", nameof(template));
                segments[index] = (part, null);
            }
        }
        return new NavigationRouteTemplate(trimmed, segments, [.. parameters]);
    }

    // The route path's segments, still percent-encoded; false for an empty path or an empty segment.
    public static bool TrySplit(string route, out string[] segments)
    {
        var trimmed = route.Trim('/');
        segments = trimmed.Length == 0 ? [] : trimmed.Split('/');
        return segments.Length > 0 && Array.TrueForAll(segments, static segment => segment.Length > 0);
    }

    public bool TryMatch(string[] segments, out NavigationRouteValues values)
    {
        values = null!;
        if (segments.Length != _segments.Length) return false;
        var matched = new NavigationRouteValues();
        for (var index = 0; index < segments.Length; index++)
        {
            var (literal, parameter) = _segments[index];
            if (literal is not null)
            {
                if (!string.Equals(literal, segments[index], StringComparison.OrdinalIgnoreCase)) return false;
                continue;
            }
            string text;
            try { text = Uri.UnescapeDataString(segments[index]); }
            catch (UriFormatException) { return false; }
            if (!TryParse(text, parameter!.Type, out var value)) return false;
            matched.SetValue(parameter.Name, value);
        }
        values = matched;
        return true;
    }

    // Whether values holds every parameter, with its type.
    public bool Accepts(NavigationRouteValues values)
    {
        foreach (var parameter in Parameters)
            if (!values.TryGetValue(parameter.Name, out var value) || value.GetType() != parameter.Type
                || value is string { Length: 0 }) return false;
        return true;
    }

    public string Format(NavigationRouteValues values)
    {
        var parts = new string[_segments.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            var (literal, parameter) = _segments[index];
            parts[index] = literal ?? Uri.EscapeDataString(FormatValue(values[parameter!.Name]));
        }
        return string.Join('/', parts);
    }

    private static string FormatValue(object value) => value switch
    {
        int number => number.ToString(CultureInfo.InvariantCulture),
        long number => number.ToString(CultureInfo.InvariantCulture),
        Guid id => id.ToString("D"),
        bool flag => flag ? "true" : "false",
        string text => text,
        _ => throw new InvalidOperationException($"A route parameter can't be a {value.GetType().Name}."),
    };

    private static bool TryParse(string text, Type type, out object value)
    {
        value = null!;
        if (text.Length == 0) return false;
        if (type == typeof(string)) value = text;
        else if (type == typeof(int) && int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)) value = number;
        else if (type == typeof(long) && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var big)) value = big;
        else if (type == typeof(Guid) && Guid.TryParse(text, out var id)) value = id;
        else if (type == typeof(bool) && bool.TryParse(text, out var flag)) value = flag;
        else return false;
        return true;
    }
}
