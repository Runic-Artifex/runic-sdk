using System;

namespace WebUIToolkit.Collections.Tests;

internal static class Program
{
    public static int Main()
    {
        return TestRunner.Run(
            RangeMutationTests.All,
            UpdateToTests.All,
            ReentrancyAndSafetyTests.All,
            PropertySequenceTests.All);
    }
}
