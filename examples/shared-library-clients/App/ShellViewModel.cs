using CommunityToolkit.Mvvm.ComponentModel;
using SharedUi;

namespace SharedLibraryApp;

/// <summary>The application's window: its own heading and the library's two panels.</summary>
public sealed partial class ShellViewModel(AboutViewModel about, SupportViewModel support) : ObservableObject
{
    /// <summary>The window's heading.</summary>
    public string Title => "Acme Workbench";

    /// <summary>The library's about panel.</summary>
    public AboutViewModel About { get; } = about;

    /// <summary>The library's support menu.</summary>
    public SupportViewModel Support { get; } = support;
}
