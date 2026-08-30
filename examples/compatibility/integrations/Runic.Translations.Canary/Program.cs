using System;
using System.Threading.Tasks;
using RunicArtifex.Examples.Translations;

try
{
    return await HotSwapScenario.RunAsync();
}
catch (Exception failure)
{
    Console.Error.WriteLine(failure.Message);
    return 1;
}
