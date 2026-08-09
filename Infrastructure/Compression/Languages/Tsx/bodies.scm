(function_declaration body: (statement_block) @body)
(generator_function_declaration body: (statement_block) @body)
(method_definition body: (statement_block) @body)
; These parent anchors leave a stable binding in the output. Bare callbacks intentionally remain.
(variable_declarator value: (arrow_function body: (statement_block) @body))
(variable_declarator value: (function_expression body: (statement_block) @body))
(variable_declarator value: (generator_function body: (statement_block) @body))
(assignment_expression right: (arrow_function body: (statement_block) @body))
(assignment_expression right: (function_expression body: (statement_block) @body))
(assignment_expression right: (generator_function body: (statement_block) @body))
(export_statement value: (arrow_function body: (statement_block) @body))
(export_statement value: (function_expression body: (statement_block) @body))
(export_statement value: (generator_function body: (statement_block) @body))
; Object data survives because only function-valued pairs have a captured body.
(pair value: (arrow_function body: (statement_block) @body))
(pair value: (function_expression body: (statement_block) @body))
(pair value: (generator_function body: (statement_block) @body))
; A call-wrapped function remains attributable when the outer expression has a stable binding.
(variable_declarator value: (call_expression arguments: (arguments
  [(arrow_function body: (statement_block) @body)
   (function_expression body: (statement_block) @body)
   (generator_function body: (statement_block) @body)])))
(variable_declarator value: (call_expression arguments: (arguments
  (call_expression arguments: (arguments
    [(arrow_function body: (statement_block) @body)
     (function_expression body: (statement_block) @body)
     (generator_function body: (statement_block) @body)])))))
(assignment_expression right: (call_expression arguments: (arguments
  [(arrow_function body: (statement_block) @body)
   (function_expression body: (statement_block) @body)
   (generator_function body: (statement_block) @body)])))
(assignment_expression right: (call_expression arguments: (arguments
  (call_expression arguments: (arguments
    [(arrow_function body: (statement_block) @body)
     (function_expression body: (statement_block) @body)
     (generator_function body: (statement_block) @body)])))))
(export_statement value: (call_expression arguments: (arguments
  [(arrow_function body: (statement_block) @body)
   (function_expression body: (statement_block) @body)
   (generator_function body: (statement_block) @body)])))
(export_statement value: (call_expression arguments: (arguments
  (call_expression arguments: (arguments
    [(arrow_function body: (statement_block) @body)
     (function_expression body: (statement_block) @body)
     (generator_function body: (statement_block) @body)])))))
