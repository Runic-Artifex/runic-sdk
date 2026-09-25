# Window and View architecture

A Runic Window is an explicit .NET composition boundary. Its generic ViewModel
is scoped to that logical window. Views are explicit partial types that select
content contracts and carry a typed `DataContext`; they are created within the
window's dependency-injection scope.

The generated C# attachment connects the host to those contracts, while the
build emits ordinary TypeScript clients. The frontend chooses components and
controls rendering. It reports View mount and unmount so the .NET content session
can track the logical View lifetime independently from the ViewModel scope.

Commands and property writes are typed. Property writes carry receipts and version
conflicts; callers should await pending writes before issuing a command that depends
on the updated value. Accepted operations remain owned by .NET through a browser
reload, and a reconnect receives a fresh snapshot.

See the [first Window](../../../../examples/first-window/README.md) for the smallest
composition and [Notes examples](../../../../examples/notes-view-first/README.md) for
nested content, routing, and multiple Views over a ViewModel.
