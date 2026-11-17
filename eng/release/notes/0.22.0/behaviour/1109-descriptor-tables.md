- **Generated Bridges describe their members as data, so a bridged ViewModel costs about a third of 0.21's.** A
  Bridge used to create its descriptors (about 30 objects and 65 delegates for a typical ViewModel) in its constructor,
  for every Bridge instance, with a lambda per member and operation. Now each Bridge holds a table with one record per
  member and answers for its members in one `switch` per operation. A reader that the generator writes once per
  assembly (`RunicShared.Tables.Bridge.g.cs`) reads the table on the first Bridge of a ViewModel type and creates the
  same descriptors, which every later Bridge of the type shares. In the research fixture of #1109 a ViewModel costs
  16.8 KB instead of 32.5 KB on linux-x64 (56.3 KB in 0.21); in the scaling suite's `aot-size` fixture 8.6 KiB instead
  of 18.2 KB (24.4 KiB in 0.21), and its Bridge has 19 methods instead of 80. The reader and the command runners add
  about 25 KB once per bridged assembly. The first Bridge of a type is created faster (0.74 ms instead of 2.1 ms in a
  JIT benchmark, compilation included), and each further one allocates 15.3 KB instead of 17.8 KB. Descriptor order,
  names and wire names, the generated TypeScript and the wire protocol are unchanged, and Bridges an earlier generator
  wrote keep working. The Runic generator no longer calls `ToolkitCommandInspector.DescriptorFor`; the method keeps its
  signature and the descriptor it writes still compiles. Part of #1109.
