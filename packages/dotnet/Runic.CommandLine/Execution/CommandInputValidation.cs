using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Runic.CommandLine;

internal static class CommandInputValidation
{
    internal static CommandFault? Validate(ParsedInvocation invocation)
    {
        var present = new HashSet<string>(invocation.Options.Select(binding => binding.Id), StringComparer.Ordinal);
        foreach (CommandOptionDescriptor option in invocation.Command.Options)
        {
            if (!present.Contains(option.Id)) continue;
            foreach (string required in option.Help.Requires)
                if (!present.Contains(required)) return Fault($"{option.Name} requires {Name(required)}.");
            foreach (string conflict in option.Help.ConflictsWith)
                if (present.Contains(conflict)) return Fault($"{option.Name} cannot be combined with {Name(conflict)}.");
            CommandFault? fault = Values(GeneratedCommandBinding.Options(invocation, option.Id), option.Help, option.Name);
            if (fault is not null) return fault;
        }
        foreach (CommandArgumentDescriptor argument in invocation.Command.Arguments)
        {
            CommandFault? fault = Values(GeneratedCommandBinding.Arguments(invocation, argument.Id), argument.Help, argument.Name);
            if (fault is not null) return fault;
        }
        return null;
        string Name(string id) => invocation.Command.Options.First(option => option.Id == id).Name;
    }
    private static CommandFault? Values(IReadOnlyList<string> values, CommandHelp help, string name)
    {
        foreach (string value in values)
        {
            if (help.Minimum is not null || help.Maximum is not null)
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number) ||
                    (help.Minimum is { } min && number < min) || (help.Maximum is { } max && number > max))
                    return Fault($"{name} requires a number" + (help.Minimum is { } lower ? $" >= {lower.ToString(CultureInfo.InvariantCulture)}" : "") +
                        (help.Maximum is { } upper ? $" <= {upper.ToString(CultureInfo.InvariantCulture)}" : "") + ".");
            }
            if (help.PathKind != CommandPathKind.None)
            {
                try { _ = Path.GetFullPath(value); }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                { return Fault($"{name} requires a valid {help.PathKind.ToString().ToLowerInvariant()} path."); }
                if (help.MustExist && !(help.PathKind == CommandPathKind.File ? File.Exists(value) : Directory.Exists(value)))
                    return Fault($"{name} requires an existing {help.PathKind.ToString().ToLowerInvariant()}; check the path and permissions.");
            }
        }
        return null;
    }
    private static CommandFault Fault(string message) => new("RCLI2002", message);
}
