using System;
using System.Collections.Generic;
using System.Linq;

namespace Runic.CommandLine;

internal static class CommandSuggestions
{
    internal static string? Commands(string token, IReadOnlyList<CommandDescriptor> commands) =>
        Find(token, commands.Where(command => !command.Help.Hidden).SelectMany(command => new[] { command.Name }.Concat(command.Aliases)));
    internal static string? Options(string token, CommandDescriptor command, string outputOptionName) =>
        Find(token.Split('=')[0], command.Options.Where(option => !option.Help.Hidden).SelectMany(option => new[] { option.Name }.Concat(option.Aliases)).Concat(new[] { "--help", "-h", "--version", outputOptionName }));

    private static string? Find(string token, IEnumerable<string> candidates)
    {
        // Bound work and never put untrusted input or values into the suggestion.
        if (token.Length is < 2 or > 64) return null;
        int best = token.Length < 5 ? 1 : 2;
        string? result = null;
        foreach (string candidate in candidates.Order(StringComparer.Ordinal))
        {
            if (candidate.Length > 64 || Math.Abs(candidate.Length - token.Length) > best) continue;
            int distance = Distance(token.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (distance <= best && (result is null || distance < best)) { best = distance; result = candidate; }
        }
        return result;
    }
    private static int Distance(string a, string b)
    {
        var table = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) table[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) table[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                table[i, j] = Math.Min(Math.Min(table[i - 1, j] + 1, table[i, j - 1] + 1), table[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) table[i, j] = Math.Min(table[i, j], table[i - 2, j - 2] + 1);
            }
        return table[a.Length, b.Length];
    }
}
