# Comparison notes

DevProjex and Repomix can both turn a repository into a context document, but
their selection, binary, output, secret-checking, and token-estimation semantics
are different. The reproducible measurements and exact commands are in
[Benchmarks.md](Benchmarks.md).

On the pinned Flask default corpus, the cold observations were 1,552 ms and
70.3 MiB main-process peak RSS for DevProjex versus 536 ms and 291.2 MiB for
Repomix; the tools selected 212 and 230 files respectively. On the Git-ignore
plus secret-checking Godot series, the observations were 62,445 ms and
1,037.6 MiB versus 8,269 ms and 3,478.0 MiB, with 14,261 versus 14,149 files.
Because these are different effective file sets and different output contracts,
the numbers are not an identical-workload speed ranking.

For the fixed Flask session-cookie investigation, the pack-first MCP route used
21 calls and about 79,653 response tokens after reading its stored pack. Compact
tree/search/file exploration followed by a three-file pack used 14 calls and
about 51,075 response tokens, a reduction of roughly 36% for this task. The
token count is a local `o200k_base` estimate reported as ±5%; it measures MCP
response volume, not whether a model produced a correct answer.
