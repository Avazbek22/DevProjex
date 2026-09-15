# Explicit answer contradictions

Criteria were fixed on 2026-09-15 at product base
`af88c2bf47f9481858eda5d435c764183d4d1a1a`, before another comparison series.
The pinned repository commits are the ones in `reachability.json`. Saved answers
were not used to select or revise these criteria.

| Task | Incorrect affirmative proposition | Source that disproves it |
| --- | --- | --- |
| T1 | Authorization survives a host change. | httpx `_client.py:546-563`: different origin removes authorization unless it is a same-host standard HTTP-to-HTTPS upgrade. |
| T2 | AsyncHTTPTransport uses a synchronous ConnectionPool. | httpx `_transports/default.py:279-331`: AsyncConnectionPool and AsyncHTTPProxy. |
| T3 | build_request discards per-request extensions. | httpx `_client.py:370-389`: extensions are preserved and passed to Request, adding timeout only when absent. |
| T4 | Malformed percent encoding always throws. | hono `src/utils/url.ts:81-93,221-222`: tryDecode catches failure and decodes valid fragments. |
| T5 | Context.json returns an unserialized object. | hono `src/context.ts:725-742`: JSON.stringify feeds a Response with JSON content type. |
| T6 | New middleware is automatically exported without distribution registration. | hono `package.json:38-247`, `jsr.json:8-50`: explicit middleware subpath exports, not a wildcard registration. |
| T7 | The shortest/least-specific override wins. | serilog `Core/LevelOverrideMap.cs:53-88`: descending ordering, first matching context, exact length or dot boundary. |
| T8 | Enrichment happens after sink dispatch. | serilog `Core/Logger.cs:469-482`: Enrich runs before `_sink.Emit`. |
| N1 | ByteMemoryScalarConversionPolicy compiles on every target. | serilog `Policies/ByteMemoryScalarConversionPolicy.cs:15-22`: FEATURE_SPAN compilation guard. |
| N2 | 65,536 is rejected or 65,537 is accepted. | httpx `_urlparse.py:29,218-219,268-269`: limit 65,536; both checks use `>` and raise InvalidURL. |
| N3 | Context performs router matching. | hono `src/hono-base.ts:408-438`: dispatch calls router.match before constructing Context with matchResult. |
| N4 | BatchingSink.Emit waits for capacity or throws on a full queue. | serilog `Core/Sinks/Batching/BatchingSink.cs:86-105`: TryWrite is nonblocking; a full queue drops the event. Null-input validation is a separate case. |

`affirmative-phrase` matches explicit proposition phrases within one clause.
English/Russian denial prefixes and explicit false/incorrect suffixes suppress
that proposition; a negation inserted inside a phrase cannot match it. Number
group separators and simple Markdown emphasis are normalized. A bare path is not
a causal assertion. Omission remains incomplete, not incorrect.

This is not unrestricted natural-language entailment. Unlisted paraphrases,
sarcasm, nested quotations, and complex negation remain unverified and require
the separate two-order correctness assessment. Tool preference is independent.
