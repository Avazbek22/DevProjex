# CLI Profiles

DevProjex profiles store selection intent, not dynamic scan counts.

## References

`--profile` accepts:

- `auto`: TUI/open only; resolves `local` when valid, otherwise `standard`;
- `standard`: deterministic built-in defaults;
- `local`: the existing per-project Desktop profile;
- `FILE`: a portable versioned JSON profile.

`analyze`, context/project export, and `profile show` default to `standard` so
scripts behave consistently on another machine. `profile export` defaults to
`local`. TUI and `open` default to `auto`.

## Precedence

Resolution order is:

1. load the referenced baseline profile;
2. replace each field explicitly supplied by the command;
3. validate selected paths and Git readiness against the current project;
4. create the canonical `ProjectContextPlan`.

An absent option inherits its profile field. An explicitly empty collection is an
empty set. In particular, `--exclude none` replaces profile Exclusions with an
empty set.

## Schema v1

```json
{
  "schemaVersion": 1,
  "kind": "devprojex-profile",
  "selection": {
    "roots": null,
    "extensions": null,
    "selectedPaths": [],
    "gitMode": "gitignore",
    "exclusions": [
      "smart-ignore",
      "hidden-folders",
      "hidden-files"
    ]
  }
}
```

Semantics:

- `roots: null` means all currently available roots;
- `extensions: null` means all currently available extensions;
- an empty `selectedPaths` means the full effective tree;
- selected file and directory paths are relative to the source root;
- a directory includes its effective subtree;
- Git mode is exactly one of `none`, `gitignore`, or `tracked`;
- Exclusions contain only ordinary exclusion tokens.

Unknown additive JSON properties are allowed for forward compatibility. A missing
or unsupported schema, unknown required Git mode, unknown exclusion token, or
invalid selected path is a validation failure.

## Commands

```shell
devprojex profile show .
devprojex profile show . --profile local
devprojex profile show . --profile ./profile.json --format json

devprojex profile export . --profile local -o ../devprojex-profile.json
devprojex profile import ./profile.json . --apply
devprojex profile validate ./profile.json
devprojex profile reset .
```

Writing a portable profile is atomic. Existing output returns exit code `4` and
requires `--force` for replacement.
`profile import` validates without modifying local state unless `--apply` is
present.

Legacy local state with both Git options enabled is normalized by the existing
security-first profile logic before conversion. The v1 portable schema cannot
represent two simultaneous Git modes.
