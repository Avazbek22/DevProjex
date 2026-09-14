# Dependency live validation

This tool records dependency-fact coverage and checked related-file projections for repositories at
fixed commits. It runs the in-process dependency engine for aggregate facts, then compares `related`
CLI output with the real MCP `related_files` surface for every declared sample. Checked source
relations combine explicit entries in `registry.json` with uniquely resolvable repository-local
`#include` directives discovered by the runner. The scanner records whether each expected relation
resolved, remained explicit but unresolved, fell inside a reported partial-parse range, or vanished
silently. An unexpected resolved edge makes the run fail. Pinned source fragments keep macro-before-
declaration, conditional-in-generated-header, and generated-header cases in the live sample set.

The same run scans the ten previously checked non-native language repositories. Their file, fact,
navigation, and edge counters must exactly match the baseline product SHA in `registry.json`; the
command names every changed counter and fails instead of silently accepting drift.

Run the complete sequential protocol with:

```sh
node tools/DependencyLiveValidation/run.mjs --output dependency-live-validation.json
```

The default command clones every repository into a temporary workspace, publishes Release builds of
the scanner and TerminalHost, verifies repository and product identities, writes the result, and
removes the temporary workspace. No repository build scripts or source generators are executed.

While initially recording the checked relation lists, `--allow-unchecked` retains unexpected resolved
edges in the result instead of failing. Committed protocols must run without that switch.
