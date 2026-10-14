using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Runic.Platform;
using Runic.Platform.Windows.Elevation;

namespace Runic.Elevation.Smoke;

/// <summary>What the smoke's actions do once <see cref="ElevatedHelper.Match"/> accepted them.</summary>
internal static class SmokeActions
{
    /// <summary>The variable the RealElevation cases set in the caller, which must not reach the helper.</summary>
    public const string Canary = "RUNIC_ELEVATION_CANARY";

    public static int Run(PrivilegedActionDeclaration action, ImmutableArray<string> arguments)
    {
        var cancellation = ElevatedHelper.WatchCancellation();
        switch (action.Verb)
        {
            case "report":
                Console.WriteLine("verb=report");
                foreach (var argument in arguments) Console.WriteLine($"arg <{argument}>");
                Console.WriteLine($"caller={Path.GetFileName(ElevatedHelper.GetCallerImagePath())}");
                Console.WriteLine($"caller-pid={ElevatedHelper.GetCallerProcessId()}");
                Console.Error.WriteLine("from stderr");
                // Tools the helper starts inherit its output.
                Child("/d /c echo from-child");
                Child("/d /c echo child-error 1>&2");
                Console.WriteLine(new string('x', 40000));
                Console.Write("no newline at the end");
                return 3;
            case "wait":
                Console.WriteLine("waiting");
                cancellation.WaitHandle.WaitOne();
                Console.WriteLine("stopped by caller");
                return 5;
            case "ignore":
                Console.WriteLine("ignoring");
                Thread.Sleep(TimeSpan.FromMinutes(2));
                return 6;
            case "flood":
                var count = int.Parse(arguments[0], System.Globalization.CultureInfo.InvariantCulture);
                for (var i = 0; i < count; i++) Console.WriteLine($"line {i}");
                return 0;
            case "environment":
                Console.WriteLine($"cwd={Environment.CurrentDirectory}");
                Console.WriteLine($"path={Environment.GetEnvironmentVariable("Path")}");
                Console.WriteLine($"temp={Environment.GetEnvironmentVariable("TEMP")}");
                Console.WriteLine($"canary={Environment.GetEnvironmentVariable(Canary) ?? "<none>"}");
                Console.WriteLine($"variables={string.Join(',', Environment.GetEnvironmentVariables().Keys.Cast<string>().Order(StringComparer.OrdinalIgnoreCase))}");
                Console.WriteLine($"nativeaot={!RuntimeFeature.IsDynamicCodeSupported}");
                return 0;
            default:
                return 2;
        }
    }

    private static void Child(string arguments)
    {
        using var child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), arguments) { UseShellExecute = false })!;
        child.WaitForExit();
    }
}
