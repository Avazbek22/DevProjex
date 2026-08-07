; Python keeps documentation INSIDE the body, so a naive body splice deletes it. This captures the
; first string of a function suite so the docstring can be preserved while the rest of the body goes.
;
; The form matters and is version-dependent. Upstream commit 26855ea changed whether a docstring is
; a bare (string) child of the block or is wrapped in (expression_statement). Each form is an
; IMPOSSIBLE PATTERN on the other side of that commit, and an impossible pattern fails compilation of
; the WHOLE query file - which, in a single-file binary with no post-hoc patching, would be a shipped
; and unfixable defect. Measured against the grammar this project actually ships:
;
;   (block . (expression_statement (string) @doc))   -> Query error: Structure   REJECTED
;   (block . (string) @doc)                          -> 3 captures               ACCEPTED
;
; CodeCompressionQueryContractTests compiles every .scm against the shipped grammars for this reason.

(function_definition body: (block . (string) @doc))
