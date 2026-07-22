namespace WebUIToolkit.MVVM.BindingCompiler.Tests;

internal static class Program
{
    public static int Main()
    {
        TestRunner runner = new();
        CompilerCliContractTests.Register(runner);
        return runner.Run();
    }
}
