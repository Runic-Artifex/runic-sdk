- The documentation of generated clients names no .NET type or language, so that it fits a producer in any language:
  a setter `set{Name}` says ``Calls the producer's setter of `title`.`` instead of
  ``Calls the public C# setter of `EditorViewModel.Title`.``, a checked field's `write{Name}` names the member's wire name
  (``Writes `title` …``) instead of the .NET type and member, and a shared client's documentation lists the implementing
  Views by their public names (`ImportResult`) instead of their .NET type names, and calls its setters the producer's.
  The code is unchanged.
