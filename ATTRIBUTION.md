# Attribution and clean-room note

Crap4CSharp is an independently authored implementation informed by black-box/behavioural reference to:

- Repository: <https://github.com/unclebob/crap4java>
- Pinned revision: `69b561209f130ece728f19b0001e90df5a117c3a`
- The upstream README credits `crap4clj` as an influence.

The referenced repository contains no license grant. Its Java source, tests, and documentation were therefore not copied, translated, or adapted. Its behaviour served only as inspiration for the general tool concept and command-line expectations. The MIT license in this repository covers only this independently written implementation.

## Deliberate semantic differences

Crap4CSharp derives coverage from visited versus total **method sequence points** in OpenCover or Cobertura/Coverlet XML. It does not infer coverage from aggregate class/package percentages. A match must be confident by source file, declaring type, method name, compatible arity, and a unique source span; ambiguous overloads and conflicting module/assembly identities are reported as unknown. Multiple reports merge distinct sequence points by visited-point union rather than summing duplicate denominators. These rules are C#- and Coverlet-oriented and are not claims about the upstream implementation's coverage semantics.
