(property_declaration) @preserve
(const_declaration) @preserve
(enum_case) @preserve

; The shipped PHP grammar can expose a '#' line inside heredoc/nowdoc as a comment child.
; String content is declarative data, so the enclosing string node is the authoritative range.
(heredoc) @preserve
(nowdoc) @preserve

(
  (method_declaration name: (name) @constructor) @preserve
  (#eq? @constructor "__construct")
)
