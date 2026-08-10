; Named functions and methods are the only executable owners in phase one. Anonymous functions and
; arrow functions deliberately remain intact because removing them would leave no callable name.

(function_definition body: (compound_statement) @body)
(method_declaration  body: (compound_statement) @body)
