; Python is the one language in the set where the leaf body node type (block) is IDENTICAL to the
; container body node type: a class suite and a function suite are both "block". A bare "(block) @body"
; would therefore delete whole class bodies. Anchoring on function_definition is what keeps this safe.

(function_definition body: (block) @body)
