# Attachments

A profile window that shows binary content from .NET and takes files from the
page, on Runic Desktop with a CommunityToolkit.Mvvm ViewModel (Views feature
`blobs/1`, see
[Blobs and uploads](../../packages/dotnet/Runic.Application.Views/README.md#blobs-and-uploads)).

- [`ProfileViewModel`](ProfileViewModel.cs) draws its avatar as a
  `RunicBlob`. The page shows it with `<img src={avatar.url}>`; Redraw makes a
  new blob, so the page loads a new URL and the old one stops working.
- The page uploads a file with `profile.upload(file, { onProgress })`, which
  streams it to the window's surface over HTTP, and passes the resulting
  reference to `attach`, whose parameter is a `RunicUpload`. The command stores
  it with `SaveAsAsync` and lists it as an `Attachment` whose `File` is a
  download blob (`RunicBlobDisposition.Attachment`).
- [`Frontend/src/app.ts`](Frontend/src/app.ts) uses the URLs as they are:
  `<img src>` and `<a href download>`.

## Run it from this repository

```sh
bun run bootstrap
dotnet run --project examples/attachments/Attachments.csproj
```

`node examples/attachments/browser-smoke.mjs` runs it without a window and
drives it from headless Chromium, after a Release build.
