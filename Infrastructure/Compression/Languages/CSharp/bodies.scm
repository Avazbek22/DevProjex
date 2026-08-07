; Leaf executable bodies only. Every pattern is anchored on the parent declaration and its body:
; field, never on a bare node type. In six of the ten grammars DevProjex targets, the leaf body node
; and a container body node are literally the same type, so a bare "(block) @body" would collapse
; class and namespace bodies. C# is not one of those six, but the discipline is uniform on purpose:
; the container list in language.json is then a loud assertion rather than the thing keeping us safe.
;
; body: (_) rather than body: (block) is deliberate - Java constructors use constructor_body, and an
; over-specific pattern fails silently by matching nothing at all.

(method_declaration              body: (_) @body)
(local_function_statement        body: (_) @body)
(constructor_declaration         body: (_) @body)
(destructor_declaration          body: (_) @body)
(operator_declaration            body: (_) @body)
(conversion_operator_declaration body: (_) @body)
(accessor_declaration            body: (_) @body)
(property_declaration            value: (arrow_expression_clause) @body)
(indexer_declaration             value: (arrow_expression_clause) @body)
(lambda_expression               body: (block) @body)
(anonymous_method_expression           (block) @body)
