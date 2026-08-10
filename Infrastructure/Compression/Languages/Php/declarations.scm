(namespace_definition name: (namespace_name) @name) @declaration
(class_declaration     name: (name) @name) @declaration
(interface_declaration name: (name) @name) @declaration
(trait_declaration     name: (name) @name) @declaration
(enum_declaration      name: (name) @name) @declaration
(function_definition   name: (name) @name) @declaration
(method_declaration    name: (name) @name) @declaration
(property_declaration (property_element name: (variable_name) @name)) @declaration
(const_declaration (const_element (name) @name)) @declaration
(enum_case name: (name) @name) @declaration
