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

## Portable schema versions

DevProjex writes schema version 2:

```json
{
  "schemaVersion": 2,
  "kind": "devprojex-profile",
  "selection": {
    "roots": null,
    "extensions": null,
    "selectedPaths": null,
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

Schema-v2 semantics:

- `roots: null` means all currently available roots;
- `extensions: null` means all currently available extensions;
- `selectedPaths: null` (or an omitted property) means the full effective tree;
- an empty `selectedPaths` array means an explicit empty selection;
- a non-empty `selectedPaths` array narrows the effective tree to those paths;
- selected file and directory paths are relative to the source root;
- a directory includes its effective subtree;
- Git mode is exactly one of `none`, `gitignore`, or `tracked`;
- `hideSecrets` independently enables the content transformation;
- `hidePrivateData` independently enables private-data redaction;
- `compressCode` independently enables syntax-aware code compression;
- `stripComments` independently removes syntax-tree comments and Python docstrings from output;
- `stripBlankLines` independently removes unprotected whitespace-only source lines from output;
- Exclusions contain only known path-filter tokens.

The reader also accepts schema version 1 profiles written by v5.1. In schema v1,
an omitted or null `selectedPaths` and an empty `selectedPaths` array all mean the
full effective tree; only a non-empty array narrows the selection. This preserves
the v5.1 representation, which wrote an empty array for a full selection. Loading
and then saving such a profile writes schema version 2 with `selectedPaths: null`.
`profile validate` and `profile import` report that a valid schema-v1 document is
legacy and will be rewritten as version 2 when saved.

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

Unknown additive JSON properties are allowed for forward compatibility. An
unrecognized selection property that looks like a misspelled or incorrectly
cased security setting is rejected so it cannot silently disable redaction. A
missing or unsupported schema, unknown required Git mode, unknown exclusion
token, or invalid selected path is also a validation failure.

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
prints the absolute committed path. The same bounded document-size limit applies
to both writing and reading, so every successful save can be loaded again. A
profile-store or file-write failure is a
runtime error with exit code `1`, not a syntax error.
`profile import` validates without modifying local state unless `--apply` is
present. For a schema-v1 import, the migration notice is written to stderr while
the existing success path remains the only stdout line. Use `--profile local`
only after Desktop or TUI has created valid local
settings for that project; an absent local profile is a usage error. Local lookup
reports missing (`DPX-CLI-PROFILE-NOT-FOUND`), temporary contention
(`DPX-CLI-PROFILE-BUSY`), corrupt storage (`DPX-CLI-PROFILE-CORRUPT`), and a
newer unsupported schema (`DPX-CLI-PROFILE-FUTURE-SCHEMA`) separately. These
failures never fall back to the broader standard profile. Saving is refused when
both the primary profile database and its backup are corrupt; their original
bytes are retained for manual recovery instead of being replaced by an empty
database.

Legacy local state with both Git options enabled is normalized by the existing
security-first profile logic before conversion. The v1 portable schema cannot
represent two simultaneous Git modes.

## Persistence limitations in v5.2

Selection profiles and persistent secret marks use separate durable stores. Reset
removes persistent marks first. If that stage fails, selection remains unchanged.
If the later selection-store stage fails, the command reports
`DPX-CLI-PROFILE-PARTIAL` and policy exit code `3`; repeat the command to finish
the idempotent cleanup. CLI profile saves compare the profile revision observed
before planning with the revision held under the store lock. A concurrent update
returns `DPX-CLI-PROFILE-CONFLICT` and policy exit code `3`; repeating the command
reloads the newer profile before planning again.

The durable JSON writer distinguishes a committed primary whose backup refresh
failed from a rejected or failed primary commit. Persistent secret-mark writes
treat that state as committed and repair the backup on the next successful write,
so callers do not repeat an operation that is already durable in the primary.
Payload limits are enforced while JSON is streamed to private staging: exceeding
the cap rejects and removes staging before primary or backup is changed.
