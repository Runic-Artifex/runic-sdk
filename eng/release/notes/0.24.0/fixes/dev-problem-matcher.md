- **A new `runic-app --editor vscode` project's `dev` tasks no longer list a failure twice in Problems.** `dev` also
  prints a failure as a sentence, such as "…, so dev did not start: /path/to/index.ts(12,14): error TS2322: …", and the
  tasks' pattern, VS Code's `$msCompile` after `dev`'s label, filed a copy of the error under that sentence. The pattern's
  file now holds no `": "` and the subcategory before `error` no colon, as in Runic DevKit's matcher. The pattern in the
  `dotnet runic dev` README has the same change. An existing project's `.vscode/tasks.json` can take the three tasks'
  `regexp` from a new project.
