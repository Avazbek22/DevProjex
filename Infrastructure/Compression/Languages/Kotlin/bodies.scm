; Kotlin declares state in properties and primary-constructor parameters, so init blocks and
; secondary constructors are executable owners like C# constructors. Every capture is anchored to
; its owner; class bodies, property accessors and free lambdas are deliberately unreachable.
; Multiline expression functions use declaration replacement because `= { }` is a lambda literal
; in Kotlin. Scala intentionally uses inline replacement because the same syntax is a Scala block.

(function_declaration (function_body (block) @body))
(function_declaration (function_body (expression) @expression))
(anonymous_initializer (block) @body)
(secondary_constructor (block) @body)
