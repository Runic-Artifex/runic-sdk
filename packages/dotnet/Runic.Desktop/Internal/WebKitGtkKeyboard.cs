namespace Runic.Desktop.Internal;

internal enum PageZoomCommand
{
    None,
    In,
    Out,
    Reset,
}

// Browser keyboard behaviour that WebKitGTK leaves to its embedder. Shared by the
// GTK 3 host and the GTK 4 provider, which links this file.
internal static class WebKitGtkKeyboard
{
    // GdkModifierType has the same values in GTK 3 and GTK 4.
    private const uint ControlMask = 1 << 2;
    private const uint AltMask = 1 << 3;
    private const uint SuperMask = 1 << 26;
    private const uint HyperMask = 1 << 27;
    private const uint MetaMask = 1 << 28;

    // GDK key symbols.
    private const uint KeyPlus = 0x02b;
    private const uint KeyMinus = 0x02d;
    private const uint KeyZero = 0x030;
    private const uint KeyEqual = 0x03d;
    private const uint KeyUnderscore = 0x05f;
    private const uint KeyKeypadAdd = 0xffab;
    private const uint KeyKeypadSubtract = 0xffad;
    private const uint KeyKeypadZero = 0xffb0;

    internal const double DefaultZoom = 1.0;

    // Firefox's steps, from 30 % to 300 %.
    internal static readonly double[] ZoomLevels = [0.3, 0.5, 0.67, 0.8, 0.9, 1.0, 1.1, 1.2, 1.33, 1.5, 1.7, 2.0, 2.4, 3.0];

    internal static double MinimumZoom => ZoomLevels[0];

    internal static double MaximumZoom => ZoomLevels[^1];

    private const double Tolerance = 0.005;

    /// <summary>Maps a key press that the page did not handle to a zoom command.</summary>
    /// <remarks>Control must be held without Alt, Super, Hyper or Meta. Shift is allowed, because some layouts need it for + or _.</remarks>
    internal static PageZoomCommand GetZoomCommand(uint keyval, uint state)
    {
        if ((state & ControlMask) == 0 || (state & (AltMask | SuperMask | HyperMask | MetaMask)) != 0)
        {
            return PageZoomCommand.None;
        }
        return keyval switch
        {
            KeyPlus or KeyEqual or KeyKeypadAdd => PageZoomCommand.In,
            KeyMinus or KeyUnderscore or KeyKeypadSubtract => PageZoomCommand.Out,
            KeyZero or KeyKeypadZero => PageZoomCommand.Reset,
            _ => PageZoomCommand.None,
        };
    }

    /// <summary>Maps a wheel message of the page script: 1 zooms in, -1 out.</summary>
    internal static PageZoomCommand GetWheelZoomCommand(int message) => message switch
    {
        1 => PageZoomCommand.In,
        -1 => PageZoomCommand.Out,
        _ => PageZoomCommand.None,
    };

    internal static double Apply(PageZoomCommand command, double current) => command switch
    {
        PageZoomCommand.In => ZoomIn(current),
        PageZoomCommand.Out => ZoomOut(current),
        PageZoomCommand.Reset => DefaultZoom,
        _ => current,
    };

    internal static double ZoomIn(double current)
    {
        foreach (var level in ZoomLevels)
        {
            if (level > current + Tolerance)
            {
                return level;
            }
        }
        return MaximumZoom;
    }

    internal static double ZoomOut(double current)
    {
        for (var index = ZoomLevels.Length - 1; index >= 0; index--)
        {
            if (ZoomLevels[index] < current - Tolerance)
            {
                return ZoomLevels[index];
            }
        }
        return MinimumZoom;
    }

    internal const string ScriptWorld = "runic-desktop-keyboard";

    internal const string ZoomMessageHandler = "runicZoom";

    // Runs in its own script world in the top frame, from document start.
    //
    // Tab: WebKitGTK cannot give the page focus from one end. When Tab leaves the last
    // control, WebKit clears the focused element and hands focus to the GTK window,
    // which has nothing else to focus, so focus returns to the view with no focused
    // element: a silent stop for screen readers. When a Tab or Shift+Tab that the page
    // did not cancel clears the focus this way, the script focuses the first or last
    // control, as a browser window does.
    //
    // Wheel: WebKit scrolls on Control and the wheel, so the host cannot wait for the
    // page to leave the event unhandled, as it does for keys. While Control is held,
    // the script cancels a wheel event that the page did not cancel and asks the host
    // to zoom. The listener exists only while Control is held: a cancelable wheel
    // listener makes WebKit wait for the page before scrolling.
    internal const string Script = """
(() => {
  "use strict";
  let tab = null;
  addEventListener("keydown", event => {
    if (event.key === "Control") { watchWheel(true); return; }
    if (event.key !== "Tab" || event.ctrlKey || event.altKey || event.metaKey) return;
    tab = event;
    setTimeout(() => { if (tab === event) tab = null; });
  }, true);
  addEventListener("keyup", event => { if (event.key === "Control") watchWheel(false); }, true);
  addEventListener("blur", event => { if (event.target === window) watchWheel(false); }, true);
  addEventListener("focusout", event => {
    const pending = tab;
    if (!pending || pending.defaultPrevented || event.relatedTarget !== null) return;
    tab = null;
    setTimeout(() => {
      const active = document.activeElement;
      if (active && active !== document.body && active !== document.documentElement) return;
      const order = candidates();
      if (pending.shiftKey) order.reverse();
      for (const element of order) {
        const target = checkedRadio(element) ?? element;
        target.focus({ focusVisible: true });
        if (deepActive() === target) return;
      }
    });
  }, true);

  let watching = false;
  let wheel = 0;
  function watchWheel(on) {
    if (on === watching) return;
    watching = on;
    wheel = 0;
    if (on) addEventListener("wheel", zoomWheel, { passive: false });
    else removeEventListener("wheel", zoomWheel, { passive: false });
  }
  // Wheels report a whole step per notch; touchpads report small steps, which add up.
  function zoomWheel(event) {
    if (!event.ctrlKey || event.altKey || event.metaKey || event.defaultPrevented || !event.deltaY) return;
    event.preventDefault();
    const step = event.deltaMode === WheelEvent.DOM_DELTA_PIXEL ? event.deltaY / 40 : event.deltaY;
    if (step * wheel < 0) wheel = 0;
    wheel += step;
    if (Math.abs(wheel) < 1) return;
    const message = wheel < 0 ? 1 : -1;
    wheel = 0;
    window.webkit?.messageHandlers?.runicZoom?.postMessage(message);
  }

  function deepActive() {
    let active = document.activeElement;
    while (active?.shadowRoot?.activeElement) active = active.shadowRoot.activeElement;
    return active;
  }
  function visible(element) {
    if (typeof element.checkVisibility === "function")
      return element.checkVisibility({ visibilityProperty: true });
    return element.getClientRects().length > 0;
  }
  function tabbable(element) {
    if (element.tabIndex < 0 || element.disabled || element.closest("[inert]")) return false;
    if (/^(IFRAME|FRAME|OBJECT|EMBED)$/.test(element.tagName)) return false;
    return visible(element);
  }
  function collect(root, elements) {
    for (const element of root.querySelectorAll("*")) {
      if (tabbable(element)) elements.push(element);
      if (element.shadowRoot) collect(element.shadowRoot, elements);
    }
    return elements;
  }
  // Sequential order: positive tabindex values first, ascending, then document order.
  function candidates() {
    const elements = collect(document, []);
    const positive = elements.filter(element => element.tabIndex > 0);
    positive.sort((left, right) => left.tabIndex - right.tabIndex);
    return positive.concat(elements.filter(element => element.tabIndex === 0));
  }
  // Tab enters a radio group at its checked button.
  function checkedRadio(element) {
    if (!(element instanceof HTMLInputElement) || element.type !== "radio" || !element.name || element.checked) return null;
    const scope = element.form ?? element.getRootNode();
    for (const radio of scope.querySelectorAll("input[type=radio]")) {
      if (radio.name === element.name && radio.checked && (radio.form ?? null) === (element.form ?? null) && tabbable(radio)) return radio;
    }
    return null;
  }
})();
""";
}
