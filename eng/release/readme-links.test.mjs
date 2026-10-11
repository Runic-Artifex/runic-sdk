import { test, expect } from 'bun:test';
import { assertReleaseLinks, releaseLinks } from './readme-links.mjs';

const links = { repository: 'Runic-Artifex/runic-sdk', tag: 'v0.7.0-preview.7', directory: 'packages/dotnet/Runic.Desktop' };

test('packed README links name the release tag', () => {
  const source = [
    '[guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation/README.md#setup)',
    '[examples](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples)',
    '<https://github.com/Runic-Artifex/runic-sdk/tree/main>',
    '[GTK 4](../Runic.Desktop.Gtk4/README.md) and [inventory](docs/inventory.md#windows)',
    '[section](#install), [portal](https://docs.runic-artifex.eu/packages/), [mail](mailto:a@b.c)',
    '[a branch named mainline](https://github.com/Runic-Artifex/runic-sdk/tree/mainline)',
  ].join('\n');
  expect(releaseLinks(source, links)).toBe([
    '[guide](https://github.com/Runic-Artifex/runic-sdk/blob/v0.7.0-preview.7/packages/dotnet/Runic.Navigation/README.md#setup)',
    '[examples](https://github.com/Runic-Artifex/runic-sdk/tree/v0.7.0-preview.7/examples)',
    '<https://github.com/Runic-Artifex/runic-sdk/tree/v0.7.0-preview.7>',
    '[GTK 4](https://github.com/Runic-Artifex/runic-sdk/blob/v0.7.0-preview.7/packages/dotnet/Runic.Desktop/../Runic.Desktop.Gtk4/README.md) and [inventory](https://github.com/Runic-Artifex/runic-sdk/blob/v0.7.0-preview.7/packages/dotnet/Runic.Desktop/docs/inventory.md#windows)',
    '[section](#install), [portal](https://docs.runic-artifex.eu/packages/), [mail](mailto:a@b.c)',
    '[a branch named mainline](https://github.com/Runic-Artifex/runic-sdk/tree/mainline)',
  ].join('\n'));
});

test('a remaining main-branch link fails the pack, including other repositories', () => {
  const other = '[Svelte](https://github.com/Runic-Artifex/runic-translations-sdk/tree/main/packages/web/translations-svelte).';
  expect(releaseLinks(other, links)).toBe(other);
  expect(() => assertReleaseLinks(other, 'svelte README')).toThrow(
    'svelte README links to a main branch (github.com/Runic-Artifex/runic-translations-sdk/tree/main/packages/web/translations-svelte).');
  expect(() => assertReleaseLinks(releaseLinks('[x](https://github.com/Runic-Artifex/runic-sdk/blob/main/LICENSE)', links), 'x')).not.toThrow();
});
