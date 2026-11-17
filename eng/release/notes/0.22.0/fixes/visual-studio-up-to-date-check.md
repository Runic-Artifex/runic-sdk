- Visual Studio's F5 now builds after a frontend-only edit and after
  `dotnet runic dev` stops. Its fast up-to-date check knew only the C# inputs,
  so F5 ran the old page. After `dev` stopped, F5 also kept the development page
  that `dev` had written into `www/`, which points at the stopped server, so the
  Window stayed blank until an explicit Build. The Runic Views build targets now
  give the check the frontend's files, without `node_modules`, the build output
  and the generated client. They also give it `obj/runic/dev-stopped`, which
  `dev` touches when it stops. Only Visual Studio evaluates these items.
