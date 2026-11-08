(class_declaration) @declaration.class
(function_declaration) @declaration.function
(export_statement
  declaration: (lexical_declaration
    kind: "const"
    (variable_declarator
      name: (identifier)
      value: [(arrow_function) (function_expression) (generator_function)]) @declaration.function))
