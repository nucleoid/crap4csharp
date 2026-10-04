# Complexity rulesets

Ruleset IDs identify the complete inventory, ownership, and decision dialect. Score-affecting
changes require a new ID.

## `ordinary-methods-v1`

This is the frozen legacy dialect. It inventories concrete ordinary methods only, starts at one,
and adds one for `if`, `for`, ordinary `foreach`, `while`, `do`, `catch`, conditional expressions,
switch arms/case labels, `&&`, `||`, and `??`. Nested local functions, lambdas, and anonymous
methods are not traversed. Constructors, accessors, operators, initializers, and top-level code are
excluded. The option-only CLI defaults to this ID.

## `callables-v1`

This opt-in dialect inventories every authored callable kind listed in `callable-support.md`. Each
callable starts at one. It adds one for the legacy decisions plus catch filters, switch guards,
binary `and`/`or` patterns, and deconstruction `foreach`. `else`, `default`, `finally`, `await`,
`yield`, `??=`, null-conditional access, discard patterns, `not` patterns, and `await foreach` add
nothing by themselves.

Nested callable bodies are excluded from their parent's leaf complexity. The
`crap.nestedFamilyRisk` guard is separate: family CC is `1 + sum(member CC - 1)` and coverage is
the OR-union of distinct authoritatively owned points. It is never included in callable totals.
Any unknown child makes the family score unknown.

Both leaf and known family scores use `CC² × (1 - coverage)³ + CC`; violation comparison is
strictly greater than the configured threshold.
