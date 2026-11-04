using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Runic.Navigation;

/// <summary>
/// A serializable navigation state: the regions and selections of a navigator that no entry owns, each with its entries
/// and, below each entry, the regions and selections its content owns. <see cref="RunicNavigator.GetPath"/> creates it and
/// <see cref="RunicNavigator.RestoreAsync"/> rebuilds it, for example after a restart.
/// </summary>
/// <remarks>
/// <para>
/// Regions and selections are recorded by position: the regions an owner created, in creation order, leaving out those of
/// a selection, and its selections in creation order. An entry is recorded by its route (<see cref="INavigationRouteMap"/>).
/// A path holds no entry ids, region keys or content, so it stays valid across processes as long as the application
/// creates its regions in the same order.
/// </para>
/// <para>
/// Store it where the application keeps its own state, for example as JSON next to the window placement:
/// <see cref="ToJson"/> and <see cref="FromJson"/> read and write a stable format without reflection.
/// </para>
/// </remarks>
/// <param name="Regions">The root regions, by position.</param>
/// <param name="Selections">The root selections, by position.</param>
public sealed record NavigationPath(IReadOnlyList<NavigationRegionPath> Regions, IReadOnlyList<NavigationSelectionPath> Selections)
{
    /// <summary>The version that <see cref="ToJson"/> writes.</summary>
    public const int FormatVersion = 1;

    // Bounds for untrusted JSON (FromJson): a stored file can be edited.
    internal const int MaximumDepth = 32;
    internal const int MaximumNodes = 10_000;

    /// <summary>Gets an empty path: restoring it changes nothing.</summary>
    public static NavigationPath Empty { get; } = new([], []);

    /// <summary>
    /// Writes the path as JSON:
    /// <c>{"version":1,"regions":[{"entries":[{"route":"customers","regions":[],"selections":[]}]}],"selections":[]}</c>.
    /// An entry without a route has <c>"route":null</c>.
    /// </summary>
    public string ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", FormatVersion);
            WriteChildren(writer, Regions, Selections);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Reads a path that <see cref="ToJson"/> wrote. Unknown properties are ignored.</summary>
    /// <exception cref="FormatException">
    /// The text is not such a path, has a newer <c>version</c>, nests deeper than 32 levels or has more than 10,000 entries,
    /// regions and selections.
    /// </exception>
    public static NavigationPath FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaximumDepth * 4 + 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("A navigation path is a JSON object.");
            if (root.TryGetProperty("version", out var version) && (version.ValueKind != JsonValueKind.Number || version.GetInt32() > FormatVersion))
                throw new FormatException($"The navigation path has version {version}, newer than {FormatVersion}.");
            var nodes = 0;
            var (regions, selections) = ReadChildren(root, 0, ref nodes);
            return new NavigationPath(regions, selections);
        }
        catch (JsonException error)
        {
            throw new FormatException("The text is not a navigation path.", error);
        }
        catch (InvalidOperationException error)
        {
            throw new FormatException("The text is not a navigation path.", error);
        }
    }

    internal static void WriteChildren(Utf8JsonWriter writer, IReadOnlyList<NavigationRegionPath> regions,
        IReadOnlyList<NavigationSelectionPath> selections)
    {
        writer.WriteStartArray("regions");
        foreach (var region in regions) WriteRegion(writer, region);
        writer.WriteEndArray();
        writer.WriteStartArray("selections");
        foreach (var selection in selections)
        {
            writer.WriteStartObject();
            writer.WriteNumber("selected", selection.Selected);
            writer.WriteStartArray("regions");
            foreach (var region in selection.Regions) WriteRegion(writer, region);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteRegion(Utf8JsonWriter writer, NavigationRegionPath region)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("entries");
        foreach (var entry in region.Entries)
        {
            writer.WriteStartObject();
            if (entry.Route is null) writer.WriteNull("route");
            else writer.WriteString("route", entry.Route);
            WriteChildren(writer, entry.Regions, entry.Selections);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static (NavigationRegionPath[] Regions, NavigationSelectionPath[] Selections) ReadChildren(JsonElement element, int depth, ref int nodes)
    {
        if (depth > MaximumDepth) throw new FormatException($"The navigation path nests deeper than {MaximumDepth} levels.");
        var regions = element.TryGetProperty("regions", out var regionArray) ? ReadRegions(regionArray, depth, ref nodes) : [];
        NavigationSelectionPath[] selections = [];
        if (element.TryGetProperty("selections", out var selectionArray))
        {
            RequireArray(selectionArray, "selections");
            selections = new NavigationSelectionPath[selectionArray.GetArrayLength()];
            var index = 0;
            foreach (var selection in selectionArray.EnumerateArray())
            {
                Count(ref nodes);
                if (selection.ValueKind != JsonValueKind.Object) throw new FormatException("A selection is a JSON object.");
                var selected = selection.TryGetProperty("selected", out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : -1;
                var members = selection.TryGetProperty("regions", out var memberArray) ? ReadRegions(memberArray, depth, ref nodes) : [];
                selections[index++] = new NavigationSelectionPath(members, selected);
            }
        }
        return (regions, selections);
    }

    private static NavigationRegionPath[] ReadRegions(JsonElement array, int depth, ref int nodes)
    {
        RequireArray(array, "regions");
        var regions = new NavigationRegionPath[array.GetArrayLength()];
        var index = 0;
        foreach (var region in array.EnumerateArray())
        {
            Count(ref nodes);
            if (region.ValueKind != JsonValueKind.Object) throw new FormatException("A region is a JSON object.");
            NavigationEntryPath[] entries = [];
            if (region.TryGetProperty("entries", out var entryArray))
            {
                RequireArray(entryArray, "entries");
                entries = new NavigationEntryPath[entryArray.GetArrayLength()];
                var position = 0;
                foreach (var entry in entryArray.EnumerateArray())
                {
                    Count(ref nodes);
                    if (entry.ValueKind != JsonValueKind.Object) throw new FormatException("An entry is a JSON object.");
                    string? route = null;
                    if (entry.TryGetProperty("route", out var routeValue) && routeValue.ValueKind != JsonValueKind.Null)
                        route = routeValue.ValueKind == JsonValueKind.String ? routeValue.GetString() : throw new FormatException("A route is a string.");
                    var (childRegions, childSelections) = ReadChildren(entry, depth + 1, ref nodes);
                    entries[position++] = new NavigationEntryPath(route) { Regions = childRegions, Selections = childSelections };
                }
            }
            regions[index++] = new NavigationRegionPath(entries);
        }
        return regions;
    }

    private static void RequireArray(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new FormatException($"'{name}' is a JSON array.");
    }

    private static void Count(ref int nodes)
    {
        if (++nodes > MaximumNodes) throw new FormatException($"The navigation path has more than {MaximumNodes} nodes.");
    }
}

/// <summary>A region of a <see cref="NavigationPath"/>: its entries, from the bottom of its history to the current entry.</summary>
/// <param name="Entries">The entries, bottom to top; empty for an empty region.</param>
public sealed record NavigationRegionPath(IReadOnlyList<NavigationEntryPath> Entries)
{
    /// <summary>Writes the region as JSON, as an element of <c>regions</c> in <see cref="NavigationPath.ToJson"/>.</summary>
    public string ToJson() => new NavigationPath([this], []).ToJson();

    /// <summary>Reads a region that <see cref="ToJson"/> wrote.</summary>
    /// <exception cref="FormatException">The text is not such a region.</exception>
    public static NavigationRegionPath FromJson(string json) =>
        NavigationPath.FromJson(json).Regions is [var region] ? region : throw new FormatException("The text is not one navigation region.");
}

/// <summary>An entry of a <see cref="NavigationPath"/>: its route and the regions and selections its content owns.</summary>
/// <param name="Route">The entry's route path, or <see langword="null"/> when no route describes it.</param>
public sealed record NavigationEntryPath(string? Route)
{
    /// <summary>Gets the regions the entry's content owns, by position, leaving out those of a selection.</summary>
    public IReadOnlyList<NavigationRegionPath> Regions { get; init; } = [];

    /// <summary>Gets the selections the entry's content owns, by position.</summary>
    public IReadOnlyList<NavigationSelectionPath> Selections { get; init; } = [];
}

/// <summary>
/// A selection of a <see cref="NavigationPath"/>, such as document tabs: each region of it by position, and the selected
/// one. Restoring it creates regions beyond the selection's current ones with <see cref="NavigationSelectionOptions.RestoreRegion"/>.
/// </summary>
/// <param name="Regions">The selection's regions, in presentation order.</param>
/// <param name="Selected">The index of the selected region, or -1 when the selection was empty.</param>
public sealed record NavigationSelectionPath(IReadOnlyList<NavigationRegionPath> Regions, int Selected);

/// <summary>What <see cref="RunicNavigator.RestoreAsync"/> could not restore.</summary>
/// <param name="Issues">The parts of the path that were left out, in the order they were met; empty when every part was restored.</param>
public sealed record NavigationRestoreResult(IReadOnlyList<NavigationRestoreIssue> Issues)
{
    /// <summary>Gets whether every part of the path was restored.</summary>
    public bool IsComplete => Issues.Count == 0;
}

/// <summary>A part of a <see cref="NavigationPath"/> that was not restored.</summary>
/// <param name="Kind">Why it was left out.</param>
/// <param name="Location">
/// Where it is in the path, such as <c>regions[0]/entries[2]/selections[0]/regions[1]</c>.
/// </param>
/// <param name="Route">The entry's route, for an entry.</param>
/// <param name="Outcome">The outcome of the navigation request that did not commit, for <see cref="NavigationRestoreIssueKind.NotCommitted"/>.</param>
public sealed record NavigationRestoreIssue(NavigationRestoreIssueKind Kind, string Location, string? Route = null, NavigationOutcome? Outcome = null);

/// <summary>Why a part of a <see cref="NavigationPath"/> was not restored.</summary>
/// <remarks>Later releases can add values.</remarks>
public enum NavigationRestoreIssueKind
{
    /// <summary>
    /// No route of the map matches the entry's route for the region, or the entry had no route. The entry and the regions
    /// below it are left out. By default the entries above it are left out too (<see cref="AboveUnmapped"/>); with
    /// <see cref="NavigationUnmappedEntries.Skip"/> they are restored.
    /// </summary>
    UnmappedRoute,

    /// <summary>The owner has fewer regions than the path; the region and its entries are left out.</summary>
    MissingRegion,

    /// <summary>The owner has fewer selections than the path; the selection is left out.</summary>
    MissingSelection,

    /// <summary>
    /// The selection has fewer regions than the path and no <see cref="NavigationSelectionOptions.RestoreRegion"/>, or that
    /// function returned no usable region; the region is left out.
    /// </summary>
    MissingSelectionRegion,

    /// <summary>
    /// A navigation request did not commit: a guard refused, the request failed, was superseded or the navigator closed. The
    /// region keeps what was restored before it, and the rest of the region is left out.
    /// </summary>
    NotCommitted,

    /// <summary>
    /// The entries above an <see cref="UnmappedRoute"/> entry, from the <c>Location</c>'s index on, such as
    /// <c>regions[0]/entries[3..]</c>, are left out, because a page above a missing one may depend on it
    /// (<see cref="NavigationUnmappedEntries.Stop"/>).
    /// </summary>
    AboveUnmapped,

    /// <summary>
    /// A region has more entries than <see cref="NavigationRestoreOptions.MaximumEntriesPerRegion"/>, or a selection more
    /// regions than <see cref="NavigationRestoreOptions.MaximumRegionsPerSelection"/>; those from the <c>Location</c>'s
    /// index on, such as <c>regions[0]/entries[256..]</c>, are left out.
    /// </summary>
    LimitExceeded,
}

/// <summary>What <see cref="RunicNavigator.RestoreAsync"/> does with an entry whose route no route of the map matches.</summary>
public enum NavigationUnmappedEntries
{
    /// <summary>
    /// The region stops at the entry: it keeps the entries below it, and the entries above it are left out
    /// (<see cref="NavigationRestoreIssueKind.AboveUnmapped"/>), so a missing Customer 42 doesn't leave Invoice 7 directly
    /// above Customers. The default.
    /// </summary>
    Stop,

    /// <summary>The entry is left out and the entries above it are restored.</summary>
    Skip,
}

/// <summary>Configures <see cref="RunicNavigator.RestoreAsync"/> and the region and selection restores.</summary>
/// <remarks>
/// A saved path is application data from an earlier run, possibly of another version of the application. Store the
/// application's version next to the path, and discard a path that another version wrote rather than restoring it: its
/// routes, and the order in which owners create their regions, may differ.
/// </remarks>
public sealed record NavigationRestoreOptions
{
    internal static readonly NavigationRestoreOptions Default = new();

    /// <summary>Gets the route map, or <see langword="null"/> (the default) for the <see cref="INavigationRouteMap"/> of the navigator's services.</summary>
    public INavigationRouteMap? Routes { get; init; }

    /// <summary>Gets what an entry that no route matches does; <see cref="NavigationUnmappedEntries.Stop"/> by default.</summary>
    public NavigationUnmappedEntries UnmappedEntries { get; init; }

    /// <summary>Gets how many entries of one region are restored at most; 256 by default. Later ones are left out.</summary>
    public int MaximumEntriesPerRegion { get; init; } = 256;

    /// <summary>Gets how many regions of one selection, such as tabs, are restored at most; 256 by default. Later ones are left out.</summary>
    public int MaximumRegionsPerSelection { get; init; } = 256;

    internal static NavigationRestoreOptions Check(NavigationRestoreOptions? options)
    {
        if (options is null) return Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumEntriesPerRegion, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumRegionsPerSelection, 1, nameof(options));
        if (!Enum.IsDefined(options.UnmappedEntries)) throw new ArgumentOutOfRangeException(nameof(options), options.UnmappedEntries, "Unknown value.");
        return options;
    }
}
