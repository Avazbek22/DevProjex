(class_declaration) @declaration.class
(abstract_class_declaration) @declaration.class
(interface_declaration) @declaration.interface
(type_alias_declaration) @declaration.type
(enum_declaration) @declaration.enum
(function_declaration) @declaration.function
(internal_module) @declaration.module
(export_statement
  declaration: (lexical_declaration
    kind: "const"
    (variable_declarator
      name: (identifier)
      value: [(arrow_function) (function_expression) (generator_function)]) @declaration.function))
