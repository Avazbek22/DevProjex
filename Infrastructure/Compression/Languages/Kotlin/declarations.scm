(class_declaration  name: (identifier) @name) @declaration
(object_declaration name: (identifier) @name) @declaration
(function_declaration name: (identifier) @name) @declaration
(source_file
  (property_declaration (variable_declaration (identifier) @name)) @declaration)
(class_body
  (property_declaration (variable_declaration (identifier) @name)) @declaration)
(enum_class_body
  (property_declaration (variable_declaration (identifier) @name)) @declaration)
(type_alias type: (identifier) @name) @declaration
(enum_entry (identifier) @name) @declaration
(anonymous_initializer "init" @name) @declaration
(secondary_constructor "constructor" @name) @declaration
