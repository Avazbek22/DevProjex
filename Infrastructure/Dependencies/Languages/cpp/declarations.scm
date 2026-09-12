(namespace_definition
  name: (_)) @context.namespace
(class_specifier
  name: (type_identifier)
  body: (field_declaration_list)) @declaration.cpp_class
(struct_specifier
  name: (type_identifier)
  body: (field_declaration_list)) @declaration.cpp_struct
(union_specifier
  name: (type_identifier)
  body: (field_declaration_list)) @declaration.cpp_union
(enum_specifier
  name: (type_identifier)
  body: (enumerator_list)) @declaration.cpp_enum
(function_definition) @declaration.cpp_function
(declaration
  declarator: (function_declarator)) @declaration.cpp_function
