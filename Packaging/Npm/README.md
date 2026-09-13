# npm package sources

`devprojex/` is the dependency-free launcher package. `platform/` is the single
template used to stage the six optional binary packages. The build script replaces
only version and platform placeholders, adds the Linux `libc: ["glibc"]` constraint,
and runs `npm pack`; no install or lifecycle script participates.

Run the dependency-free launcher contract tests with:

```shell
node --test Packaging/Npm/test/*.test.js
```
