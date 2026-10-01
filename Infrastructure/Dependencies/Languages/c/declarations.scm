(function_definition) @declaration.c_function
(declaration
  declarator: (function_declarator)) @declaration.c_function
(struct_specifier
  name: (type_identifier)
  body: (field_declaration_list)) @declaration.c_struct
(union_specifier
  name: (type_identifier)
  body: (field_declaration_list)) @declaration.c_union
(enum_specifier
  name: (type_identifier)
  body: (enumerator_list)) @declaration.c_enum
(type_definition) @declaration.c_typedef
