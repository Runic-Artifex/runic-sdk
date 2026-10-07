using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Runic.Application.Views;

// Two DTOs with the same C# name: generated TypeScript qualifies both with
// their namespace segment.
namespace Runic.Application.Testing.Tests.Alpha
{
    public sealed record Tag(string Name);
}

namespace Runic.Application.Testing.Tests.Beta
{
    public sealed record Tag(string Label);
}

namespace Runic.Application.Testing.Tests
{
    // Named like the state interface generated for NamingViewModel.
    public sealed record NamingState(int Count);

    // A member declared T? is nullable for every type argument.
    public sealed record GenericBox<T>(T? Value);

    // A lowercase C# type name that is a TypeScript reserved word.
#pragma warning disable CS8981, CA1716, IDE1006
    public sealed record delete(int Id);
#pragma warning restore CS8981, CA1716, IDE1006

#pragma warning disable CA1008 // The case-less enum is the shape under test.
    public enum EmptyKind { }
#pragma warning restore CA1008

    /// <summary>Exercises generated names. This comment contains */ and must not end the TSDoc block.</summary>
    public sealed partial class NamingViewModel : ObservableObject
    {
        private string _label = "";

        public Alpha.Tag First { get; } = new("a");
        public Beta.Tag Second { get; } = new("b");
        public NamingState Inner { get; } = new(1);
        public GenericBox<int[]> Box { get; } = new([1]);
        public EmptyKind? Nothing { get; }
        public delete Removed { get; } = new(1);

        // Wire names that are special on JavaScript objects.
        [RunicAlias("__proto__")] public string Proto { get; } = "proto";
        [RunicAlias("constructor")] public string Builder { get; } = "constructor";
        [RunicAlias("prototype")] public string Template { get; } = "prototype";

        public string Label { get => _label; private set => SetProperty(ref _label, value); }

        // Parameter names that would shadow a JavaScript keyword or a local of
        // the generated client get a "Value" suffix.
        [RelayCommand]
        private void Rename(string @default) => Label = @default;

        [RelayCommand]
        private void Show(string view) => Label = view;
    }

    public sealed partial class NamingWindow(NamingViewModel model) : RunicWindow<NamingViewModel>(model);
}
