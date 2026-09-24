(struct_item) @declaration.struct
(enum_item) @declaration.enum
(trait_item) @declaration.interface
(union_item) @declaration.struct
(type_item) @declaration.class
(mod_item body: (declaration_list)) @declaration.module
(function_item) @declaration.function

(impl_item) @context.impl
