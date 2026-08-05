# cwhtml + HTMX frontend assets

This Vite workspace is shared by the compiled cwhtml SimpleTodo and
AdvancedTodo applications. npm owns Bootstrap 5.3, Font Awesome, and HTMX;
Vite bundles those dependencies with the Runic Markup native HTMX bridge and
the two sample stylesheets.

Development builds emit readable assets and source maps. Release builds
minify and content-hash the compiled graph. Stable `cwhtml.css` and
`cwhtml.js` entrypoints point at the current graph so compiled views do not
hard-code a content hash.

Run `npm run dev --workspace @runic-artifex/sample-todo-cwhtml` for the bounded
Vite build watcher, or use `dotnet runic-toolkit dev` from either cwhtml sample
to coordinate it with contract/template compilation and the native host.
