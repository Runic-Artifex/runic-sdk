# RunicAssets.CsWebUi

This integration is owned by Runic Assets and maps an `IAssetSource` to
CsWebUi's immutable in-memory virtual file system. CsWebUi remains independent
and does not depend on Runic Assets.

```csharp
WebUiVirtualFileSystem files = await assets.ToWebUiVirtualFileSystemAsync();
window.SetVirtualFileSystem(files);
```
