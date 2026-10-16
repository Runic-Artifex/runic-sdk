Console.WriteLine(Fixture.Greeting.For("Nix"));

namespace Fixture
{
    public static class Greeting
    {
        public static string For(string name) => $"Hello, {name}";
    }
}
