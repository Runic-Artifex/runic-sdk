# Desktop wire compatibility boundary

Runic Desktop retains its internal `webui-compat/52f9e75` implementation as
protocol and differential evidence for installed-browser and embedded-WebView
hosting. The implementation is private to `Runic.Desktop`; it is not a public
application protocol or a frontend SDK contract.

The compatibility code keeps the optional `/webui.js` and `webui` surface for
existing WebUI behavior. Runic Desktop does not publish the old TypeScript
Desktop transport package, and current Runic Application Views clients do not
use this compatibility layer as their application contract. Views select
explicit .NET Window and View types and generate ordinary TypeScript clients;
the current first-party host adapter connects them to CS-WebUI.

Changes to this private compatibility code must remain isolated from the public
Desktop API and must not add a dependency from the Desktop core to Runic
Application Views. A future Views-to-Desktop adapter would need its own explicit
integration package and compatibility contract.
