## Summary

Describe the user-visible problem and the chosen solution.

## Scope

- Related issue:
- Public CLI/TUI contract changed: yes / no
- Source-project read-only guarantee affected: yes / no
- New localization keys: yes / no

If the public contract changed, update `Docs/CLI-V1-Contract.md`, command help,
examples, completion, and public-boundary tests in the same pull request.

## Validation

List exact commands and results. Do not report interrupted or skipped runs as
passed.

```text
dotnet restore DevProjex.sln
dotnet build DevProjex.sln -c Release --no-restore
dotnet test DevProjex.sln -c Release --no-build
```

## Checklist

- [ ] The change fixes a reproduced problem and includes a regression test.
- [ ] CLI stdout, stderr, exit codes, and filesystem effects were checked.
- [ ] TUI keyboard access and terminal restoration were checked when relevant.
- [ ] Machine JSON/XML remains parseable and free of ANSI output.
- [ ] New user-facing strings use the localization catalog.
- [ ] Documentation and executable examples match the production command tree.
- [ ] Snapshots were reviewed visually; they were not updated only to make CI pass.
- [ ] `git diff --check` passes and no generated files are included.
