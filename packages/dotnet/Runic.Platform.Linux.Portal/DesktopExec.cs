using System.Collections.Immutable;
using System.Text;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Portal;

// The Exec key: a command line, split without a shell, whose field codes expand into whole arguments.
// https://specifications.freedesktop.org/desktop-entry/latest/exec-variables.html
internal static class DesktopExec
{
    /// <summary>Splits an Exec value, after the string escapes, into arguments, undoing the quoting rules. Null when a
    /// quote is not closed.</summary>
    /// <remarks>Inside double quotes a backslash escapes <c>"</c>, <c>`</c>, <c>$</c> and <c>\</c>. Outside quotes a
    /// backslash escapes the next character and single quotes quote literally, as GLib's parser accepts.</remarks>
    internal static ImmutableArray<string>? Split(string exec)
    {
        var arguments = ImmutableArray.CreateBuilder<string>();
        var current = new StringBuilder();
        bool inArgument = false;
        for (int i = 0; i < exec.Length;)
        {
            char c = exec[i];
            if (c is ' ' or '\t' or '\n' or '\r')
            {
                if (inArgument) { arguments.Add(current.ToString()); current.Clear(); inArgument = false; }
                i++;
                continue;
            }
            inArgument = true;
            if (c == '"')
            {
                for (i++; ; i++)
                {
                    if (i >= exec.Length) return null;
                    if (exec[i] == '"') { i++; break; }
                    if (exec[i] == '\\' && i + 1 < exec.Length && exec[i + 1] is '"' or '`' or '$' or '\\') i++;
                    current.Append(exec[i]);
                }
            }
            else if (c == '\'')
            {
                int end = exec.IndexOf('\'', i + 1);
                if (end < 0) return null;
                current.Append(exec, i + 1, end - i - 1);
                i = end + 1;
            }
            else if (c == '\\' && i + 1 < exec.Length) { current.Append(exec[i + 1]); i += 2; }
            else { current.Append(c); i++; }
        }
        if (inArgument) arguments.Add(current.ToString());
        return arguments.ToImmutable();
    }

    /// <summary>The argument vectors to start, one per process.</summary>
    /// <remarks>
    /// <c>%f</c> and <c>%u</c> take one item, so several items start one process each; <c>%F</c> and <c>%U</c> take every
    /// item as separate arguments. <c>%f</c> and <c>%F</c> pass local paths and accept <c>file:</c> URIs as paths;
    /// <c>%u</c> and <c>%U</c> pass URIs and turn paths into <c>file:</c> URIs. <c>%i</c> becomes <c>--icon</c> and the
    /// Icon key, <c>%c</c> the translated name, <c>%k</c> the entry's path and <c>%%</c> a percent sign. Deprecated and
    /// unknown codes are removed. Without a file code, items are passed as <c>%f</c> would pass them, as GLib does.
    /// Returns null when an item cannot be passed: a URI other than a local file: URI for <c>%f</c> or <c>%F</c>.
    /// </remarks>
    internal static List<ImmutableArray<string>>? Expand(ImmutableArray<string> exec, DesktopEntry entry, ImmutableArray<string> items)
    {
        bool single = false, multiple = false, local = false;
        foreach (var argument in exec)
            for (int i = 0; i + 1 < argument.Length; i++)
            {
                if (argument[i] != '%') continue;
                switch (argument[++i])
                {
                    case 'f': single = true; local = true; break;
                    case 'u': single = true; break;
                    case 'F': multiple = true; local = true; break;
                    case 'U': multiple = true; break;
                }
            }
        if (!single && !multiple && !items.IsEmpty) { exec = [.. exec, "%f"]; single = true; local = true; }
        var converted = ImmutableArray.CreateBuilder<(string Path, string Uri)>(items.Length);
        foreach (var item in items)
        {
            string? path = ToPath(item);
            if (path is null && local) return null;
            converted.Add((path ?? "", ToUri(item)));
        }
        var groups = single && converted.Count > 1 ? converted.Select(static item => ImmutableArray.Create(item)).ToList() : [converted.ToImmutable()];
        return [.. groups.Select(group => ExpandOnce(exec, entry, group))];
    }

    private static ImmutableArray<string> ExpandOnce(ImmutableArray<string> exec, DesktopEntry entry, ImmutableArray<(string Path, string Uri)> items)
    {
        var result = ImmutableArray.CreateBuilder<string>();
        foreach (var argument in exec)
        {
            switch (argument)
            {
                case "%F": result.AddRange(items.Select(static item => item.Path)); continue;
                case "%U": result.AddRange(items.Select(static item => item.Uri)); continue;
                case "%f" or "%u": if (!items.IsEmpty) result.Add(argument == "%f" ? items[0].Path : items[0].Uri); continue;
                case "%i": if (entry.Icon is { } icon) { result.Add("--icon"); result.Add(icon); } continue;
            }
            var text = new StringBuilder();
            bool onlyCodes = argument.Length > 0;
            for (int i = 0; i < argument.Length; i++)
            {
                if (argument[i] != '%' || i + 1 == argument.Length) { text.Append(argument[i]); onlyCodes = false; continue; }
                char code = argument[++i];
                switch (code)
                {
                    case '%': text.Append('%'); onlyCodes = false; break;
                    case 'f' or 'F': if (!items.IsEmpty) text.Append(items[0].Path); break;
                    case 'u' or 'U': if (!items.IsEmpty) text.Append(items[0].Uri); break;
                    case 'c': text.Append(entry.Name); break;
                    case 'k': text.Append(entry.FilePath); break;
                    default: break; // %i inside a word, deprecated %d %D %n %N %v %m and unknown codes are removed.
                }
            }
            // An argument that was only field codes with nothing to insert disappears; a quoted empty argument stays.
            if (text.Length > 0 || !onlyCodes) result.Add(text.ToString());
        }
        return result.ToImmutable();
    }

    /// <summary>The local path of a path or a local file: URI, or null.</summary>
    internal static string? ToPath(string item)
    {
        if (DesktopApplicationNames.IsPath(item)) return item;
        if (!item.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            string uri = item.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) || item.StartsWith("file://localhost/", StringComparison.OrdinalIgnoreCase)
                ? item : item.StartsWith("file:/", StringComparison.OrdinalIgnoreCase) && !item.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                    ? "file://" + item[5..] : item;
            return PortalFilePicker.ReadExactLocalPath(uri);
        }
        catch (IOException) { return null; }
    }

    /// <summary>A URI for an item: a path becomes a file: URI with every byte outside the unreserved set escaped.</summary>
    internal static string ToUri(string item)
    {
        if (!DesktopApplicationNames.IsPath(item)) return item;
        var uri = new StringBuilder("file://");
        foreach (byte b in Encoding.UTF8.GetBytes(item))
        {
            if (b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~' or (byte)'/')
                uri.Append((char)b);
            else uri.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return uri.ToString();
    }
}
