- **CS-WebUI (preview) under `dotnet runic dev --browser`: an application's own absolute navigation leaves the
  session address (#1225).** The page's WebUI connection goes to the Window's own port, so `dev` cannot move a
  navigation sent over it. The host sends none, and Views routes in the URL's fragment, but
  `WebUiWindow.Navigate` with an absolute URL of the Window, or a second `Show`, takes the tab to the Window's own
  address, outside `dev`'s admission. Pass `Navigate` a relative path. See
  [What a window opens](../../../packages/dotnet/Runic.Application.Views.CsWebUi/README.md#what-a-window-opens).
