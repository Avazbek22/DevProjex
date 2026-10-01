(import) @import.kotlin

(user_type) @reference.type
(navigation_expression
  (identifier) @reference.expression_receiver
  .
  (identifier))
(variable_declaration (identifier) @context.value_name)
(parameter (identifier) @context.value_name)
(type_parameter) @context.type_parameter
