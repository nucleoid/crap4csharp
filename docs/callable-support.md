# Callable inventory and coverage support

Syntax inventory and numeric coverage are separate capabilities. An inventory entry never proves
that a matching IL method exists.

| Authored kind | Inventory | Coverage in `callables-v1` |
|---|---:|---|
| Ordinary method, explicit constructor, destructor, accessor, operator/conversion | yes | semantic identity + exclusive owned source points |
| Primary constructor | yes | semantic identity when report points are exclusively attributable |
| Async/iterator/async-iterator method | yes | optional validated PE + portable-PDB state-machine link; otherwise unsupported |
| Local function, lambda, anonymous method, top-level root | yes | unsupported generated mapping in v1 |
| Non-const field/event/property initializer and primary-constructor base arguments | yes | unsupported lowering in v1 |
| Const field initializer | no executable region | not inventoried |
| Abstract/extern/bodyless member | explicit not-applicable | no synthetic score |

Coverlet collector 6.0.4 was exercised with .NET SDK 10.0.103 in Debug and Release using OpenCover
and Cobertura. Ordinary semantic members are supported when the report supplies a discriminating
identity. OpenCover's `1..2` line-only column sentinel is treated as columnless evidence. Generated
`MoveNext`, local-function, and lambda names are never decoded or matched heuristically. Current
Coverlet OpenCover output does not provide a usable metadata token for those generated methods, so
they remain explicit unsupported observations even when a separate portable PDB proves a kickoff
relationship.

Coverlet line rows can aggregate visits from multiple methods on one physical source line. A
columnless point is therefore rejected as `coverage.ambiguousCallableOwnership` whenever that line
also intersects any other inventoried callable region, including bodyless or unsupported regions.
Only report columns that establish exclusive span ownership can disambiguate such same-line code.

The portable-PDB mapper accepts captured bytes only, is bounded to 128 MiB per artifact, performs no
filesystem/process/network/OS lookup, never loads the assembly, and requires exact logical document
identity and checksum. Windows native PDB, SourceLink fetching, embedded-source recovery, private
Roslyn EnC decoding, fuzzy names, basename matching, and line proximity are unsupported.

Repeated anonymous bodies under the same outer entity deliberately share an entity fingerprint but
have distinct observation IDs and are marked ambiguous. They cannot receive a numeric baseline or
exact exemption.

Syntax-only analysis has no authoritative TFM/configuration identity. It labels both `unknown` and
accepts at most one distinct coverage document (byte-identical repeats are deduplicated), preventing
Debug/Release or cross-TFM point unions. Project-aware multi-context orchestration remains #10 work.
