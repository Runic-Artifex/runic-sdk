using System.Collections.Immutable;

namespace Runic.Platform.Windows;

// The parts of the Windows status icon that need no Win32: the menu with its command IDs, the tooltip and the
// icon pixels. The native provider turns them into an HMENU, szTip and an HICON.
internal static class WindowsStatusIconModel
{
    // NOTIFYICONDATAW.szTip holds 128 UTF-16 code units, including the terminating NUL.
    internal const int TooltipCapacity = 128;

    internal abstract record Entry;
    internal sealed record Command(uint Id, string CommandId, string Label, bool Enabled, bool? Checked) : Entry;
    internal sealed record Separator : Entry;
    internal sealed record Submenu(string Label, bool Enabled, ImmutableArray<Entry> Entries) : Entry;

    // A command keeps its Win32 ID across menu changes, so a choice made while the state changes still lands on the
    // command the user saw. The menu is resolved against the snapshot it was built from.
    internal sealed class Ids
    {
        private readonly Dictionary<string, uint> _ids = new(StringComparer.Ordinal);
        // TrackPopupMenuEx returns 0 when nothing was chosen, so IDs start at 1.
        private uint _next = 1;
        internal uint For(string commandId) => _ids.TryGetValue(commandId, out uint id) ? id : _ids[commandId] = _next++;
    }

    internal sealed record Menu(ImmutableArray<Entry> Entries, ImmutableDictionary<uint, Command> Commands)
    {
        internal static Menu Empty { get; } = new([], ImmutableDictionary<uint, Command>.Empty);
        internal bool IsEmpty => Entries.IsEmpty;
        // Only an enabled command is chosen; a disabled one cannot be clicked, and an unknown ID came from another menu.
        internal string? Choose(uint id) => Commands.TryGetValue(id, out var command) && command.Enabled ? command.CommandId : null;
    }

    internal static Menu BuildMenu(ImmutableArray<DesktopMenuItem> items, Ids ids)
    {
        var commands = ImmutableDictionary.CreateBuilder<uint, Command>();
        var entries = Build(items, ids, commands);
        return new Menu(entries, commands.ToImmutable());
    }

    private static ImmutableArray<Entry> Build(ImmutableArray<DesktopMenuItem> items, Ids ids, ImmutableDictionary<uint, Command>.Builder commands)
    {
        var entries = ImmutableArray.CreateBuilder<Entry>(items.Length);
        foreach (var item in items)
        {
            switch (item)
            {
                case DesktopMenuCommand command:
                    var entry = new Command(ids.For(command.Id), command.Id, MenuLabel(command.Label), command.IsEnabled, command.IsChecked);
                    commands[entry.Id] = entry;
                    entries.Add(entry);
                    break;
                case DesktopMenuSubmenu submenu:
                    entries.Add(new Submenu(MenuLabel(submenu.Label), submenu.IsEnabled, Build(submenu.Items, ids, commands)));
                    break;
                default:
                    entries.Add(new Separator());
                    break;
            }
        }
        return entries.MoveToImmutable();
    }

    // Win32 menus read '&' as a mnemonic prefix; the label is shown as written.
    internal static string MenuLabel(string label) => label.Replace("&", "&&", StringComparison.Ordinal);

    // The title, then the tooltip on the next line, cut to szTip without splitting a surrogate pair.
    internal static string Tooltip(string title, string? tooltip)
    {
        string text = string.IsNullOrEmpty(tooltip) ? title : title + "\n" + tooltip;
        int limit = TooltipCapacity - 1;
        if (text.Length <= limit) return text;
        if (char.IsHighSurrogate(text[limit - 1])) limit--;
        return text[..limit];
    }

    // The smallest image at least as large as the shell's small icon, or else the largest; the shell scales it.
    internal static DesktopIconImage? SelectImage(DesktopIcon icon, int size)
    {
        DesktopIconImage? best = null;
        foreach (var image in icon.Images)
        {
            int side = Math.Max(image.Width, image.Height);
            if (best is null) { best = image; continue; }
            int bestSide = Math.Max(best.Width, best.Height);
            bool fits = side >= size, bestFits = bestSide >= size;
            if (fits && (!bestFits || side < bestSide) || !fits && !bestFits && side > bestSide) best = image;
        }
        return best;
    }

    // An icon's 32-bit colour bitmap holds straight-alpha BGRA, top-down here.
    internal static void ToBgra(ImmutableArray<byte> rgba, Span<byte> bgra)
    {
        if (bgra.Length != rgba.Length) throw new ArgumentException("The destination must hold every pixel.", nameof(bgra));
        var source = rgba.AsSpan();
        for (int i = 0; i < source.Length; i += 4)
        {
            bgra[i] = source[i + 2];
            bgra[i + 1] = source[i + 1];
            bgra[i + 2] = source[i];
            bgra[i + 3] = source[i + 3];
        }
    }

    // Windows has no attention state, so the attention icon is shown in its place.
    internal static DesktopIcon ShownIcon(DesktopStatusIconState state) =>
        state.Status == DesktopStatusIconStatus.NeedsAttention ? state.AttentionIcon ?? state.Icon : state.Icon;
}
