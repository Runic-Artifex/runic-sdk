// Renders selected runic-app template files in the browser. The template
// engine's conditional lines and name replacement are reproduced for the
// preview; `dotnet new runic-app` remains the authority for generated files.
import angularApp from '../../../tools/Runic.Application.Templates/content/runic-app/frontends/angular/src/app/app.ts?raw';
import angularIndex from '../../../tools/Runic.Application.Templates/content/runic-app/frontends/angular/src/index.html?raw';
import reactApp from '../../../tools/Runic.Application.Templates/content/runic-app/frontends/react/src/App.tsx?raw';
import reactIndex from '../../../tools/Runic.Application.Templates/content/runic-app/frontends/react/index.html?raw';
import svelteApp from '../../../tools/Runic.Application.Templates/content/runic-app/frontends/svelte/src/App.svelte?raw';
import svelteIndex from '../../../tools/Runic.Application.Templates/content/runic-app/frontends/svelte/index.html?raw';
import vueApp from '../../../tools/Runic.Application.Templates/content/runic-app/frontends/vue/src/App.vue?raw';
import vueIndex from '../../../tools/Runic.Application.Templates/content/runic-app/frontends/vue/index.html?raw';
import program from '../../../tools/Runic.Application.Templates/content/runic-app/Program.cs?raw';
import project from '../../../tools/Runic.Application.Templates/content/runic-app/RunicWindowApp.csproj?raw';
import views from '../../../tools/Runic.Application.Templates/content/runic-app/Views.cs?raw';
import viewModels from '../../../tools/Runic.Application.Templates/content/runic-app/WorkspaceViewModel.cs?raw';
import { templateSourceName, type CreatorSelection } from './creator';
import { renderTemplate } from './template-conditions';

export type PreviewFile = {
  readonly path: string;
  readonly language: string;
  readonly content: string;
};

const frontends: Readonly<
  Record<string, { app: [string, string, string]; index: [string, string] }>
> = {
  react: {
    app: ['Frontend/src/App.tsx', 'tsx', reactApp],
    index: ['Frontend/index.html', reactIndex],
  },
  vue: {
    app: ['Frontend/src/App.vue', 'vue', vueApp],
    index: ['Frontend/index.html', vueIndex],
  },
  svelte: {
    app: ['Frontend/src/App.svelte', 'svelte', svelteApp],
    index: ['Frontend/index.html', svelteIndex],
  },
  angular: {
    app: ['Frontend/src/app/app.ts', 'ts', angularApp],
    index: ['Frontend/src/index.html', angularIndex],
  },
};

export function previewFiles(
  name: string,
  version: string,
  selection: CreatorSelection,
): PreviewFile[] {
  const frontend = frontends[selection.frontend] ?? frontends.react;
  const render = (content: string) =>
    renderTemplate(content, selection)
      .replaceAll(templateSourceName, name)
      .replaceAll('__RUNIC_NUGET_VERSION__', version);
  return [
    { path: 'Program.cs', language: 'csharp', content: render(program) },
    { path: 'Views.cs', language: 'csharp', content: render(views) },
    {
      path: 'WorkspaceViewModel.cs',
      language: 'csharp',
      content: render(viewModels),
    },
    { path: `${name}.csproj`, language: 'xml', content: render(project) },
    {
      path: frontend.app[0],
      language: frontend.app[1],
      content: render(frontend.app[2]),
    },
    {
      path: frontend.index[0],
      language: 'html',
      content: render(frontend.index[1]),
    },
  ];
}
