using System.ComponentModel;
using Runic.Application.Views;

// These types only need to compile. The View partial for CollisionWindow and
// the bridge for CollisionWindowViewModel, and the two ItemViews, previously
// wrote the same generated file and overwrote each other.
namespace Runic.Application.Testing.Tests
{
    public sealed partial class CollisionWindow(CollisionWindowViewModel model) : RunicWindow<CollisionWindowViewModel>(model);

    public sealed class CollisionWindowViewModel : INotifyPropertyChanged
    {
        public string Title { get; } = "collision";
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }
}

namespace Runic.Application.Testing.Tests.FirstItems
{
    public sealed partial class ItemView : RunicView<FirstItemViewModel>;

    public sealed class FirstItemViewModel : INotifyPropertyChanged
    {
        public string Name { get; } = "first";
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }
}

namespace Runic.Application.Testing.Tests.SecondItems
{
    public sealed partial class ItemView : RunicView<SecondItemViewModel>;

    public sealed class SecondItemViewModel : INotifyPropertyChanged
    {
        public string Name { get; } = "second";
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }
}
