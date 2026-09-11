(call
  method: (identifier) @import.ruby.name
  arguments: (argument_list) @import.ruby.arguments
  (#any-of? @import.ruby.name "require" "require_relative")) @import.ruby

(constant) @reference.type
(scope_resolution) @reference.type
(assignment left: (constant) @context.assigned_constant)
