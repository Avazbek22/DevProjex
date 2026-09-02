# CLI Profiles

DevProjex profiles store selection intent, not dynamic scan counts.

## References

`--profile` accepts:

- `auto`: TUI/open only; resolves `local` when valid, otherwise `standard`;
- `standard`: deterministic built-in defaults;
- `local`: the existing per-project Desktop profile;
- `FILE`: a portable versioned JSON profile.

`analyze`, `tree`, context/project export, `profile show`, and `profile save`
default to `standard` so scripts behave consistently on another machine.
`profile export` defaults to `local`. TUI and `open` default to `auto`.

## Precedence

Resolution order is:

1. load the referenced baseline profile;
2. replace each field explicitly supplied by the command;
3. validate selected paths and Git readiness against the current project;
4. create the canonical `ProjectContextPlan`.

An absent option inherits its profile field. An explicitly empty collection is an
empty set. In particular, `--exclude none` replaces profile Exclusions with an
empty set.

Local Desktop/TUI profiles persist complete state maps for the three parameter
sections: top-level folders, extensions, and Exclusions. A known row keeps its saved
state even while another filter temporarily hides it. A row first discovered after
the save uses the current default, so new source folders and file types do not become
silently unavailable. CLI and TUI resolve this same modern state instead of reducing
it to selected-name lists. Legacy selected-only local records are promoted into complete
maps by retaining their selected values as checked entries; all other rows use current
defaults consistently across surfaces. Explicit CLI fields are exact for that invocation
and never mutate the stored maps.

Desktop batches pending local-profile snapshots under one project-profile store lock
when the window closes. The batch retains the existing atomic file-replacement format
and has a ten-second shutdown budget; if contention outlives that budget, the last
durable profile data remains intact and the unsaved snapshots are logged rather than
blocking application exit indefinitely.

The Hide Secrets content-transformation state is stored separately from path
Exclusions and remains off in the built-in `standard` profile. Individual
keep-as-is decisions are session-only: profiles never store secret fingerprints,
values, or occurrence locations.

Hide Private Data is stored as the independent `hidePrivateData` Boolean and
remains off in the built-in `standard` profile. Profiles created before this
field was introduced load it as `false`.

Code compression is stored as the independent `compressCode` Boolean and also
remains off in the built-in `standard` profile. Profiles created before this field
was introduced load it as `false`.

Comment removal is stored as the independent `stripComments` Boolean. It remains off in
the built-in `standard` profile, and profiles created before the field existed load it as
`false`.

Blank-line removal is stored as the independent `stripBlankLines` Boolean. It remains off
in the built-in `standard` profile, and profiles created before the field existed load it
as `false`.

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
    "hideSecrets": false,
    "hidePrivateData": false,
    "compressCode": false,
    "stripComments": false,
    "stripBlankLines": false,
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
- `hideSecrets` independently enables the content transformation;
- `hidePrivateData` independently enables private-data redaction;
- `compressCode` independently enables syntax-aware code compression;
- `stripComments` independently removes syntax-tree comments and Python docstrings from output;
- `stripBlankLines` independently removes unprotected whitespace-only source lines from output;
- Exclusions contain only known path-filter tokens.

Profiles written by current DevProjex versions keep `hideSecrets` separate. For
v5 compatibility, a portable profile containing `hide-secrets` in `exclusions`
still loads with the transformation enabled. The legacy token is removed from the
canonical Exclusions collection, and an explicit `hideSecrets` property wins.

Portable-profile root arrays are normalized when they are loaded. Empty values are
discarded, while non-empty names retain significant whitespace because it can be part
of a valid path on Unix. Duplicates are removed and values are sorted by exact ordinal
identity, so case-distinct entries survive even on a case-sensitive Windows volume.
When a profile is applied on Windows, a differently cased legacy name is accepted
only if it resolves to one unambiguous discovered entry.

Unknown additive JSON properties are allowed for forward compatibility. A missing
or unsupported schema, unknown required Git mode, unknown exclusion token, or
invalid selected path is a validation failure.

## Commands

```shell
devprojex profile show .
devprojex profile show . --profile standard --format json

devprojex profile export . --profile standard -o ../devprojex-profile.json
devprojex profile show . --profile ../devprojex-profile.json --format json
devprojex profile validate ../devprojex-profile.json
devprojex profile import ../devprojex-profile.json .
devprojex profile reset .
```

Writing a portable profile uses the canonical file-output safety policy. The
destination must resolve outside the source project, including filesystem
aliases, and its parent directory must already exist. Source-safety failures are
reported before destination conflicts. Existing output returns exit code `4`;
`--force` atomically replaces an external file but never a directory. Success
prints the absolute committed path. A profile-store or file-write failure is a
runtime error with exit code `1`, not a syntax error.
`profile import` validates without modifying local state unless `--apply` is
present. Use `--profile local` only after Desktop or TUI has created valid local
settings for that project; an absent local profile is a usage error.

Legacy local state with both Git options enabled is normalized by the existing
security-first profile logic before conversion. The v1 portable schema cannot
represent two simultaneous Git modes.
