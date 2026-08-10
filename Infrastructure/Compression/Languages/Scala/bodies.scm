; The pinned grammar exposes Scala 3 optional-braces bodies as indented_block/indented_cases, but
; replacing those nodes can change the ownership of the following declaration on a reverse parse.
; Keep them complete until the grammar can prove stable splice boundaries. Braced blocks and
; ordinary expression nodes remain anchored to a named def, so template bodies and constructor
; statements at class scope can never be removed.

(function_definition body: (block) @body)
(function_definition body: (expression) @expression)
