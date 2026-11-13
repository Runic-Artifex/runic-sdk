using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;

namespace SidePanelSample;

/// <summary>
/// A small order form with a debug panel. The panel opens beside the Window, which grows by its width, so the form keeps
/// its size and its place on the screen. Where the Window cannot grow, the page shows the same panel as an overlay.
/// </summary>
/// <remarks>
/// The panel's page (<c>#/debug</c>) is a second connection of this Window: it binds to this ViewModel like the Window's
/// own page, so both show the same state and either can run its commands.
/// </remarks>
public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>The panel's width in the Window's units (device-independent pixels, points, logical pixels).</summary>
    public const int PanelWidth = 372;

    /// <summary>The panel's route in the frontend (Frontend/src/app.ts).</summary>
    public const string PanelPath = "#/debug";

    private const int MaximumLogLength = 50;
    private readonly IRunicWindowPresentation _window;
    private IRunicSidePanel? _panel;

    public MainViewModel(IRunicWindowPresentation window) => _window = window;

    /// <summary>How many of the item the order has.</summary>
    [ObservableProperty]
    public partial int Quantity { get; private set; } = 1;

    /// <summary>What happened, newest last; the debug panel lists it.</summary>
    [ObservableProperty]
    public partial ImmutableArray<string> Log { get; private set; } = [];

    /// <summary>Whether the debug panel is open beside the Window.</summary>
    [ObservableProperty]
    public partial bool IsDebugPanelOpen { get; private set; }

    /// <summary>Whether the panel is docked on the Window's left edge; otherwise on its right edge.</summary>
    [ObservableProperty]
    public partial bool IsDebugPanelOnLeft { get; private set; }

    /// <summary>Whether the page shows the debug panel as an overlay, because the Window could not grow.</summary>
    [ObservableProperty]
    public partial bool ShowDebugOverlay { get; private set; }

    /// <summary>Why the Window did not grow or move the panel, such as <c>Maximized</c>; empty when it did.</summary>
    [ObservableProperty]
    public partial string Refusal { get; private set; } = "";

    [RelayCommand]
    private void Add()
    {
        Quantity++;
        Record($"Quantity {Quantity}");
    }

    [RelayCommand]
    private void Remove()
    {
        if (Quantity == 0) return;
        Quantity--;
        Record($"Quantity {Quantity}");
    }

    /// <summary>Shows the debug panel beside the Window, or as an overlay when the Window cannot grow; hides it when shown.</summary>
    [RelayCommand]
    private async Task ToggleDebugAsync(CancellationToken cancellationToken)
    {
        if (IsDebugPanelOpen || ShowDebugOverlay)
        {
            await CloseDebugAsync(cancellationToken);
            return;
        }
        var edge = IsDebugPanelOnLeft ? RunicSidePanelEdge.Left : RunicSidePanelEdge.Right;
        var result = await _window.OpenSidePanelAsync(new RunicSidePanelOptions { Edge = edge, Width = PanelWidth, Path = PanelPath },
            cancellationToken);
        switch (result)
        {
            case RunicSidePanelResult.Opened opened:
                Adopt(opened.Panel);
                break;
            case RunicSidePanelResult.Refused { Reason: RunicSidePanelRefusal.AlreadyOpen } when _window.SidePanel is { } open:
                // Opened elsewhere, for example by another ViewModel of this Window.
                Adopt(open);
                break;
            case RunicSidePanelResult.Refused { Reason: RunicSidePanelRefusal.Closed }:
                // The Window is closing.
                break;
            case RunicSidePanelResult.Refused { Reason: var reason }:
                // Unsupported, Maximized, FullScreen, Snapped, Minimized, NoRoom, PositionUnavailable (the left edge on
                // Wayland), LoadFailed, or a reason added later: the page shows the panel over its own content instead.
                Refusal = reason.ToString();
                ShowDebugOverlay = true;
                Record($"Debug panel shown as an overlay: the Window refused to grow ({reason})");
                break;
        }
    }

    /// <summary>Docks the panel on the other edge. An overlay moves to the other side of the page.</summary>
    [RelayCommand]
    private async Task MoveDebugAsync(CancellationToken cancellationToken)
    {
        var left = !IsDebugPanelOnLeft;
        if (_panel is not { IsOpen: true } panel)
        {
            IsDebugPanelOnLeft = left;
            return;
        }
        // Refused while the Window is maximized, full-screen or snapped, without room on the other side, or for the left
        // edge on Wayland; the panel then stays where it is.
        if (await panel.MoveToEdgeAsync(left ? RunicSidePanelEdge.Left : RunicSidePanelEdge.Right, cancellationToken) is { } refusal)
        {
            Refusal = refusal.ToString();
            Record($"Debug panel stays on the {(IsDebugPanelOnLeft ? "left" : "right")}: the Window refused to move it ({refusal})");
            return;
        }
        Refusal = "";
        IsDebugPanelOnLeft = left;
        Record($"Debug panel docked on the {(left ? "left" : "right")}");
    }

    /// <summary>Closes the panel, and the Window shrinks back; or hides the overlay.</summary>
    [RelayCommand]
    private async Task CloseDebugAsync(CancellationToken cancellationToken)
    {
        if (ShowDebugOverlay)
        {
            ShowDebugOverlay = false;
            Refusal = "";
            Record("Debug overlay hidden");
        }
        // Closed raises PanelClosed, which records the change.
        if (_panel is { } panel) await panel.CloseAsync(cancellationToken);
    }

    private void Adopt(IRunicSidePanel panel)
    {
        _panel = panel;
        panel.Closed += PanelClosed;
        IsDebugPanelOnLeft = panel.Edge == RunicSidePanelEdge.Left;
        IsDebugPanelOpen = true;
        Refusal = "";
        Record($"Debug panel opened on the {(IsDebugPanelOnLeft ? "left" : "right")}, {panel.Width} wide");
    }

    // In a turn of the Window's model context, whoever closed the panel: this ViewModel, the panel's page (window.close()),
    // a crash or the Window's close.
    private void PanelClosed(object? sender, RunicSidePanelClosedEventArgs e)
    {
        if (sender is IRunicSidePanel panel) panel.Closed -= PanelClosed;
        if (!ReferenceEquals(sender, _panel)) return;
        _panel = null;
        IsDebugPanelOpen = false;
        Record($"Debug panel closed ({e.Reason})");
    }

    private void Record(string entry)
    {
        var log = Log.Add($"{DateTime.Now:HH:mm:ss} {entry}");
        Log = log.Length > MaximumLogLength ? log.RemoveRange(0, log.Length - MaximumLogLength) : log;
    }
}

public static class SidePanelSampleServiceCollectionExtensions
{
    /// <summary>The example's ViewModel and the Window's presentation (<see cref="IRunicWindowPresentation"/>) it takes.</summary>
    public static IServiceCollection AddSidePanelSample(this IServiceCollection services) =>
        services.AddScoped<MainViewModel>().AddRunicWindowService();
}
