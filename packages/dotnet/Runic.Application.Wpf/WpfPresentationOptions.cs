using Runic.Desktop;

namespace Runic.Application.Views.Wpf;

internal static class WpfPresentationOptions
{
    internal static IReadOnlyList<DesktopDiagnostic> Validate(DesktopWindowHostOptions options)
    {
        List<DesktopDiagnostic> diagnostics = [];
        Add(options.Width != 800, nameof(DesktopWindowOptions.Width));
        Add(options.Height != 600, nameof(DesktopWindowOptions.Height));
        Add(options.MinimumWidth is not null, nameof(DesktopWindowOptions.MinimumWidth));
        Add(options.MinimumHeight is not null, nameof(DesktopWindowOptions.MinimumHeight));
        Add(options.X is not null, nameof(DesktopWindowOptions.X));
        Add(options.Y is not null, nameof(DesktopWindowOptions.Y));
        Add(options.Centered, nameof(DesktopWindowOptions.Centered));
        Add(!options.Resizable, nameof(DesktopWindowOptions.Resizable));
        Add(options.Frameless, nameof(DesktopWindowOptions.Frameless));
        Add(options.Transparent, nameof(DesktopWindowOptions.Transparent));
        Add(options.Hidden, nameof(DesktopWindowOptions.Hidden));
        Add(options.Kiosk, nameof(DesktopWindowOptions.Kiosk));
        Add(options.IconFile is not null, nameof(DesktopWindowOptions.IconFile));
        return diagnostics;

        void Add(bool configured, string option)
        {
            if (configured) diagnostics.Add(new DesktopDiagnostic(DesktopErrorCategory.CapabilityDenied,
                "wpf-window-option-owned-by-shell", $"WPF owns {option}; the child WebView ignores this window option.",
                Retryable: false, Remediation: "Configure the WPF control or containing window instead.")
            { Option = option, Severity = DesktopDiagnosticSeverity.Warning });
        }
    }
}
