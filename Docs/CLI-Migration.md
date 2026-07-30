# CLI Migration

The pre-v1 flat syntax was experimental and has been removed. DevProjex does not
run two public parsers in parallel.

When a recognized legacy action flag is used, DevProjex:

1. does not execute the old request;
2. writes a concise migration message and a structured replacement argument
   vector to stderr;
3. exits with code `2`.

Legacy detection stops at `--`; every later token is argument data. Both
`--copy zip` and `--copy=zip` are recognized for the limited migration cases.
Malformed, incomplete, duplicated, unknown, or ambiguous legacy shapes do not
receive a guessed replacement. Replacement arguments are rendered one per line
as JSON strings and are not advertised as universally escaped for every shell.

## Common Replacements

```text
Old:
devprojex --path ./app --report -

Human-readable v1 equivalent (documentation shorthand, not migration output):
devprojex analyze ./app --format json -o -
```

```text
Old:
devprojex ./app --export tree-content -o context.txt

Human-readable v1 equivalent (documentation shorthand, not migration output):
devprojex export context ./app --view tree-content --format text -o context.txt
```

```text
Old:
devprojex ./app --copy zip -o app.zip

Human-readable v1 equivalent (documentation shorthand, not migration output):
devprojex export project ./app --as zip -o app.zip
```

```text
Old:
--ignore git-ignore

New:
--git-mode gitignore
```

```text
Old:
--ignore git-tracked-only

New:
--git-mode tracked
```

```text
Old:
--ignore smart-ignore

New:
--exclude smart-ignore
```

There are no hidden compatibility aliases for the old action model. This keeps
help, completion, validation, and automation on one unambiguous contract.
