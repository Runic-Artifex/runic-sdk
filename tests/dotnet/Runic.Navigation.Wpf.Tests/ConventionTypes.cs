using System.Windows.Controls;

// The naming convention maps a ViewModels namespace segment to Views.
namespace Runic.Navigation.Wpf.Tests.ViewModels
{
    internal sealed class ShelfViewModel;
}

namespace Runic.Navigation.Wpf.Tests.Views
{
    internal sealed class ShelfPage : Border;
}

// ViewModels and views in unrelated namespaces, as in separate Core and WPF assemblies.
namespace Runic.Navigation.Wpf.Tests.Core.Archive
{
    internal sealed class LedgerViewModel;
}

namespace Runic.Navigation.Wpf.Tests.Screens
{
    internal sealed class LedgerView : Border;
}
