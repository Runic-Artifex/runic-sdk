## Desktop page services and the development host profile

Two new parts of the Desktop contract describe what a desktop host other than Runic.Desktop has to provide.
[Desktop page services](../../../specs/desktop/page-services.md) states what an embedded window gives its page besides
`__runicBridge`: drag regions, navigation and external links, announcements through live regions, high contrast and
region install. It also gives the rule for a page channel over a webview's own messages: top frame of the surface
origin only, with a secret only the host's script holds. The
[development host profile](../../../specs/desktop/development-host-profile.md) lists the environment variables a host
reads under `dotnet runic dev` and the lines it writes on standard output, each with its current `RUNIC_*` name and the
`RAX_*` name it gets in 0.25.
