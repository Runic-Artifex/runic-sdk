# Frontend contract generator

`generate-contracts.mjs` treats the frontend contract JSON as the single
declaration of the wire contract, TypeScript handles, and the closed
CommunityToolkit adapter.

Each contract declares:

```json
{
  "name": "example.contract",
  "client": "Example",
  "csharp": {
    "modelType": "global::Example.ExampleViewModel"
  }
}
```

`csharp.modelType` must be a fully qualified, non-generic C# type name beginning
with `global::`. Every member then supplies its exact generated ViewModel member
and adapter operation:

```json
{
  "id": 1,
  "name": "title",
  "kind": "property",
  "type": "string",
  "access": "readwrite",
  "validation": true,
  "csharp": {
    "sourceMember": "Title",
    "binding": "property",
    "jsonTypeInfo": "global::Example.ExampleJsonContext.Default.String"
  }
}
```

The supported `csharp.binding` values and required metadata are:

| Protocol member | C# binding | JSON metadata |
| --- | --- | --- |
| read/write property | `property` | required |
| read-only property | `readOnlyProperty` | required |
| collection | `collection` | required for the item type |
| synchronous command | `command` | required only with an argument |
| asynchronous command | `asyncCommand` | required only with an argument |

`validation: true` emits `includeValidation: true` for a property or collection.
The generator rejects binding/access mismatches, missing or surplus JSON
metadata, invalid identifiers, duplicate contract clients, duplicate contract
names, and duplicate member names or IDs before writing either output.

The same normalized metadata maps directly to binding language version 1:

```text
protocol webuitoolkit.mvvm/1;
contract "example.contract" model Example.ExampleViewModel {
  property 1 title: System.String => Title readwrite;
  command 2 save: none -> none => SaveCommand;
}
```

The current frontend schema carries TypeScript type spellings and concrete
`JsonTypeInfo` expressions, while `.wutmvvm` requires CLR type spellings.
Consequently, automatic `.wutmvvm` emission should add explicit CLR value,
collection-item, and command-parameter types rather than infer them from
TypeScript. Once those fields are present, the mapping is mechanical and can be
added as a third deterministic generator output.
