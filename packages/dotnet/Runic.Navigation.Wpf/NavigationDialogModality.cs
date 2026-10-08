using System.Diagnostics.CodeAnalysis;

namespace Runic.Navigation.Wpf;

/// <summary>What a <see cref="NavigationDialogHost"/> disables while its region has dialog windows.</summary>
[Experimental(RunicNavigator.DiagnosticId)]
public enum NavigationDialogModality
{
    /// <summary>Every other visible, enabled top-level window of the UI thread, like <c>ShowDialog</c>: app-modal dialogs.</summary>
    Application,

    /// <summary>Only the owner window, its owners and the lower dialog windows: window-modal dialogs.</summary>
    Owner,
}
