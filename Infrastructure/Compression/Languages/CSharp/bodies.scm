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
; whole file. Only blocks are compressed: expression bodies remain part of the signature-level
; context, and free lambdas remain intact unless an enclosing named block is removed.

(method_declaration              body: (block) @body)
(local_function_statement        body: (block) @body)
(constructor_declaration         body: (block) @body)
(destructor_declaration          body: (block) @body)
(operator_declaration            body: (block) @body)
(conversion_operator_declaration body: (block) @body)
(accessor_declaration            body: (block) @body)
