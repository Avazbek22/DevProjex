(import_declaration) @import.java

(type_identifier) @reference.type
(scoped_type_identifier) @reference.type
(type_parameter) @context.type_parameter

(method_invocation
  object: [
    (identifier)
    (field_access)
  ] @reference.expression_receiver)

(field_access
  object: (identifier) @reference.expression_receiver)

(variable_declarator
  name: (identifier) @context.value_name)

(formal_parameter
  name: (identifier) @context.value_name)

(spread_parameter
  (variable_declarator
    name: (identifier) @context.value_name))

(catch_formal_parameter
  name: (identifier) @context.value_name)
