- **Generated Bridges share their codecs and getters, so each bridged ViewModel adds less to a NativeAOT executable.**
  The generator now writes each distinct wire codec once per assembly, in `RunicShared.Codecs.Bridge.g.cs` beside the
  Bridges, instead of one codec class per member, command input or result and interaction value in every Bridge. A
  DTO's codec calls the codecs of its members, so a type used in several places has one codec. Bridges name their
  codecs with `using` aliases (`using TitleValueCodec = …RunicBridgeCodecs.String;`). Each property also has one
  getter, shared by its descriptors and its data subscription. In a research fixture with a realistic ViewModel
  shape a ViewModel costs 43.1 KB instead of 56.3 KB on linux-x64 (−23 %); in the scaling suite's `aot-size` fixture
  20.8 KB instead of 25.5 KB. Only the generated C# in `obj/` changes: the generated TypeScript clients, their source
  map and the wire protocol are byte-identical, and code from an earlier generator keeps working with this runtime.
  Part of #1109.
