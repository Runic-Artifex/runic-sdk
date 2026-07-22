using System;
using System.Collections.Generic;
using System.Globalization;

FixtureOutput.ApplyCulture();
return PortableParser.Run(args);

internal static class PortableParser
{
    internal static int Run(string[] args)
    {
        if (args.Length == 0)
            return Error("MissingCommand");
        if (args[0] is "--help" or "-h")
            return Help("root");
        if (args[0] == "--version")
        {
            Console.Out.WriteLine("1.0.0-evaluation");
            return 0;
        }

        return args[0] switch
        {
            "export" or "x" => Export(args),
            "cache" => Cache(args),
            "run" => RunMany(args),
            "echo" => Echo(args),
            _ => Error("UnknownCommand")
        };
    }

    private static int Export(string[] args)
    {
        string? input = null;
        var seenScalars = new HashSet<string>(StringComparer.Ordinal);
        bool options = true;
        for (int i = 1; i < args.Length; i++)
        {
            string token = args[i];
            if (options && token == "--") { options = false; continue; }
            if (options && TrySplitLong(token, out string name, out string? attached))
            {
                if (name == "--verbose")
                {
                    if (attached is not null) return Error("AttachedValueForFlag");
                    if (!seenScalars.Add(name)) return Error("DuplicateScalar");
                    continue;
                }
                if (name is "--format" or "--ratio" or "--timeout")
                {
                    if (!seenScalars.Add(name)) return Error("DuplicateScalar");
                    if (!ReadValue(args, ref i, attached, out string value)) return Error("MissingOptionValue");
                    if (name == "--format" && value.ToLowerInvariant() is not ("json" or "text")) return Error("InvalidValue");
                    if (name == "--ratio" && !decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _)) return Error("InvalidValue");
                    if (name == "--timeout" && !TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out _)) return Error("InvalidValue");
                    continue;
                }
                if (name == "--tag")
                {
                    if (!ReadValue(args, ref i, attached, out _)) return Error("MissingOptionValue");
                    continue;
                }
                return Error("UnknownOption");
            }
            if (options && token is "-v")
            {
                if (!seenScalars.Add("--verbose")) return Error("DuplicateScalar");
                continue;
            }
            if (options && token is "-f" or "-t")
            {
                string canonical = token == "-f" ? "--format" : "--tag";
                if (canonical == "--format" && !seenScalars.Add(canonical)) return Error("DuplicateScalar");
                if (!ReadValue(args, ref i, null, out string value)) return Error("MissingOptionValue");
                if (canonical == "--format" && value.ToLowerInvariant() is not ("json" or "text")) return Error("InvalidValue");
                continue;
            }
            if (options && token.StartsWith("-", StringComparison.Ordinal)) return Error("UnknownOption");
            if (input is not null) return Error("UnexpectedArgument");
            input = token;
        }
        return input is null ? Error("MissingArgument") : FixtureOutput.Invoke("export");
    }

    private static int Cache(string[] args)
    {
        if (args.Length < 2 || args[1] is not ("clear" or "purge")) return Error("UnknownCommand");
        if (args.Length == 3 && args[2] == "--help") return Help("cache/clear");
        bool options = true;
        bool targetSeen = false;
        for (int i = 2; i < args.Length; i++)
        {
            string token = args[i];
            if (options && token == "--") { options = false; continue; }
            if (options && token is "--quiet" or "-q" or "/quiet") continue;
            if (options && token.StartsWith("-", StringComparison.Ordinal)) return Error("UnknownOption");
            if (targetSeen) return Error("UnexpectedArgument");
            targetSeen = true;
        }
        return FixtureOutput.Invoke("cache/clear");
    }

    private static int RunMany(string[] args)
    {
        int start = args.Length > 1 && args[1] == "--" ? 2 : 1;
        for (int i = start; i < args.Length; i++)
            if (i == 1 && args[i].StartsWith("-", StringComparison.Ordinal)) return Error("UnknownOption");
        return FixtureOutput.Invoke("run");
    }

    private static int Echo(string[] args) => args.Length switch
    {
        2 => FixtureOutput.Invoke("echo"),
        < 2 => Error("MissingArgument"),
        _ => Error("UnexpectedArgument")
    };

    private static bool TrySplitLong(string token, out string name, out string? value)
    {
        int equals = token.IndexOf('=', StringComparison.Ordinal);
        name = equals < 0 ? token : token[..equals];
        value = equals < 0 ? null : token[(equals + 1)..];
        return name.StartsWith("--", StringComparison.Ordinal);
    }

    private static bool ReadValue(string[] args, ref int index, string? attached, out string value)
    {
        if (attached is not null) { value = attached; return true; }
        if (++index < args.Length) { value = args[index]; return true; }
        value = "";
        return false;
    }

    private static int Help(string path)
    {
        Console.Out.WriteLine($"HELP {path}");
        return 0;
    }

    private static int Error(string kind)
    {
        Console.Error.WriteLine(kind);
        return 2;
    }
}
