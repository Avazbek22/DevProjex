; The structural fingerprint the gate compares before and after splicing. It does not need to be
; exhaustive - it needs to be STABLE, so that anything the splice destroys shows up as a difference.
; Declarations captured inside a removed body (a local function, for instance) are expected to
; disappear; the gate filters those out by containment rather than pretending they survive.

(namespace_declaration              name: (_) @name) @declaration
(file_scoped_namespace_declaration  name: (_) @name) @declaration
(class_declaration                  name: (identifier) @name) @declaration
(struct_declaration                 name: (identifier) @name) @declaration
(interface_declaration              name: (identifier) @name) @declaration
(record_declaration                 name: (identifier) @name) @declaration
(enum_declaration                   name: (identifier) @name) @declaration
(delegate_declaration               name: (identifier) @name) @declaration
(method_declaration                 name: (identifier) @name) @declaration
(property_declaration               name: (identifier) @name) @declaration
(indexer_declaration                          ) @declaration
(constructor_declaration            name: (identifier) @name) @declaration
(destructor_declaration             name: (identifier) @name) @declaration
(operator_declaration                         ) @declaration
(conversion_operator_declaration              ) @declaration
(accessor_declaration                         ) @declaration
(event_declaration                            ) @declaration
(event_field_declaration (variable_declaration (variable_declarator (identifier) @name))) @declaration
(field_declaration       (variable_declaration (variable_declarator (identifier) @name))) @declaration
(local_function_statement           name: (identifier) @name) @declaration
(lambda_expression                                                ) @declaration
(anonymous_method_expression                                     ) @declaration
