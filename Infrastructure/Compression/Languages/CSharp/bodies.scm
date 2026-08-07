; Leaf executable bodies only. Every pattern is anchored on the parent declaration and its body:
; field, never on a bare node type. In six of the ten grammars DevProjex targets, the leaf body node
; and a container body node are literally the same type, so a bare "(block) @body" would collapse
; class and namespace bodies. C# is not one of those six, but the discipline is uniform on purpose:
; the container list in language.json is then a loud assertion rather than the thing keeping us safe.
;
; Explicit body node types are a correctness boundary. With a conditional attribute immediately
; before a constructor, the C# grammar can expose the surrounding preprocessor node through the
; generic body field. Capturing (_) then removes the directive and the declaration signature along
; with the implementation. The reverse-parse gate catches that corruption, but has to reject the
; whole file. Blocks and expression clauses cover the executable C# forms without that ambiguity.

(method_declaration              body: [(block) (arrow_expression_clause)] @body)
(local_function_statement        body: [(block) (arrow_expression_clause)] @body)
(constructor_declaration         body: [(block) (arrow_expression_clause)] @body)
(destructor_declaration          body: [(block) (arrow_expression_clause)] @body)
(operator_declaration            body: [(block) (arrow_expression_clause)] @body)
(conversion_operator_declaration body: [(block) (arrow_expression_clause)] @body)
(accessor_declaration            body: (block) @body)
(property_declaration            value: (arrow_expression_clause) @body)
(indexer_declaration             value: (arrow_expression_clause) @body)
(lambda_expression               body: (block) @body)
(anonymous_method_expression           (block) @body)
