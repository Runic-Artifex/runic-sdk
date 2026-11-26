- **Every Views contract string changes once: each View has a fingerprint of its own.** A View's contract fingerprint is
  now `runic.views.fingerprint/1`, the SHA-256 of the canonical form of the View's own contract IR
  ([contract IR, Fingerprint](../../../specs/application/contract-ir.md#fingerprint)). Until 0.23 it was one hash over
  every View of the assembly (`runic-bridge-contract-v6`), so a change to one View changed every View's contract. The
  operation contract `{ViewModel}:{fingerprint}` and the interaction and view request contracts
  `{fingerprint}:interaction:{Name}` and `{fingerprint}:request:{Name}` derive from it. So every `bridgeContract`,
  mock `contract`, `runicClientContracts` entry and interaction contract in committed generated clients changes once:
  rebuild and commit the generated clients, and publish a client library's package with them. An operation that a
  0.23 page started and recovers after the upgrade answers `unknown`, as it did after the 0.13 and 0.14 fingerprint
  changes. A Rust or C++ producer can compute the same fingerprint from its own descriptions. Hot Reload still
  recomputes a reflection hash of the assembly's Views, which the generated Bridge now carries as `ContractShape`, so
  an edit that changes a contract still asks for a restart (event 1100).
