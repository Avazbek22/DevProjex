# Code Compression

Code compression is an opt-in content transformation for fitting real codebases
into AI context windows. It keeps the declaration surface of your code — the part
a reader or a model needs to navigate and reason about an API — and empties named
implementation bodies. Source files are never modified; the transformation exists
only in generated output.

```csharp
// Before — the file on disk (unchanged)
private Command BuildMcpCommand()
{
    var command = new Command("mcp", L("Terminal.Command.Mcp"));
    // … ~40 more lines of implementation
}

// After — the packed context
private Command BuildMcpCommand()
{ }
```

## What stays and what goes

Kept byte-for-byte:

- Declarations, signatures, parameter lists, and class/module structure.
- Fields and language-level properties, including initializers and property
  accessors — they describe project state and public behavior.
- Expression bodies that fit on one source line, as signature-level context.
- Free lambdas and closures: removing an unbound body would leave no useful name.
- Annotations, attributes, enum entries.

Replaced with a minimal syntax-valid placeholder:

- Block bodies of named methods, functions, and constructors — `{ }` in
  brace-based languages, `...` in Python, an empty body between the declaration
  and `end` in Ruby.
- Multiline expression bodies: a body that spans lines is implementation and is
  compressed like a block body.

## Supported languages

Fourteen languages have body compression: C, C++, C#, Go, Java, JavaScript,
Kotlin, PHP, Python, Ruby, Rust, Scala, TSX, and TypeScript. Each pack encodes
what must survive for that language:

- **JavaScript / TypeScript / TSX** — block functions stored in object
  properties, assigned or exported under a stable binding, or wrapped one or two
  calls deep under that binding are compressed; the binding name and parameters
  stay visible. Bare callbacks without a binding remain complete.
- **Python** — a leading function docstring is kept, and class `__init__` and
  `__post_init__` methods remain complete, because instance state is declared
  there.
- **Ruby** — class `initialize` methods are kept; named `method` and
  `singleton_method` bodies are removed without collapsing class, module, or DSL
  blocks.
- **PHP** — properties, constants, enum cases, and `__construct` are kept; named
  functions and methods are compressed in both PHP-only and mixed HTML/PHP files,
  while anonymous and arrow functions remain complete.
- **Kotlin** — properties, custom accessors, primary-constructor state, data
  classes, enum entries, and annotations are preserved; named block functions,
  `init` blocks, and secondary constructors are compressed. Kotlin never receives
  an `= { }` replacement, because that form is a lambda.
- **Scala** — `val`, `var`, `given`, case-class parameters, and class-level
  constructor statements are preserved; braced named `def` bodies are compressed.
  Scala 3 significant-indentation bodies remain complete because their
  replacement boundary is not structurally stable.

The engine is deliberately conservative: an unsupported file, or one it cannot
process safely, stays complete rather than risking a broken transformation.

## Where it applies

The same transformed content feeds every surface: token metrics, the live
preview, context documents, and folder/ZIP project copies. In the desktop app it
is the **Compress code** switch; the status line reports the result with
estimated token counts before and after. In the CLI it is `--compress-code`,
available on `analyze`, `export context`, `export project`, and `open`, and off
in the `standard` profile. If compression fails, files are left unchanged in the
output rather than partially transformed.

As with every DevProjex transformation, a compressed project copy is intentionally
not a byte-for-byte copy and may not build or run.

## Combining with comment and blank-line stripping

Compression, comment removal (`--strip-comments`), and blank-line removal
(`--strip-blank-lines`) are independent switches that share one syntax engine.
Comment and blank-line stripping extend coverage to 20 language packs in total:
the 14 compression languages plus six comments-only packs — HTML, CSS, TOML,
Bash, the XML project-file family, and YAML. The three can be combined freely for
the smallest faithful context.

## What to expect in numbers

Savings depend on what a project contains. Measured on DevProjex's own C#
application sources (619 files), compression shrinks the packed context by about
69% — roughly 3× smaller. A mixed repository saves less, because compression only
touches code, never test fixtures, JSON assets, or documentation.

## See also

- [Command Line](CommandLine.md) — exact CLI flag semantics and profiles.
- [Hide Secrets](HideSecrets.md) and [Hide private data](HidePrivateData.md) —
  the redaction transformations, which run independently of compression.
