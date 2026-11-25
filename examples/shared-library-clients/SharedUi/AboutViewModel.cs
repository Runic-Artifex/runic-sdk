using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SharedUi;

/// <summary>Which releases the update check offers.</summary>
public enum UpdateChannel { Stable, Preview }

/// <summary>The about panel that every application shows.</summary>
public sealed partial class AboutViewModel : ObservableObject
{
    /// <summary>The product's name.</summary>
    public string ProductName => "Acme Workbench";

    /// <summary>The installed version.</summary>
    public string Version => "2.4.0";

    /// <summary>Which releases <see cref="CheckForUpdatesCommand"/> looks for.</summary>
    [ObservableProperty]
    public partial UpdateChannel Channel { get; set; }

    /// <summary>The result of the last update check.</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    /// <summary>Checks the channel for a newer release.</summary>
    [RelayCommand]
    private void CheckForUpdates() =>
        Status = Channel == UpdateChannel.Preview ? "Version 2.5.0-preview.1 is available." : "You have the latest version.";
}
