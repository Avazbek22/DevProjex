(function_definition) @navigation.function
(struct_specifier
  name: (type_identifier)
  body: (field_declaration_list)) @navigation.type
(union_specifier
  name: (type_identifier)
  body: (field_declaration_list)) @navigation.type
(enum_specifier
  name: (type_identifier)
  body: (enumerator_list)) @navigation.type
(field_declaration) @navigation.field
(translation_unit
  (declaration) @navigation.field)
