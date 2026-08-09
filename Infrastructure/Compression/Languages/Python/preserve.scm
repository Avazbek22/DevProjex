(
  (decorated_definition
    (decorator (identifier) @property_decorator)
    definition: (function_definition)) @preserve
  (#any-of? @property_decorator "property" "cached_property")
)

(
  (decorated_definition
    (decorator
      (attribute attribute: (identifier) @property_accessor))
    definition: (function_definition)) @preserve
  (#any-of? @property_accessor "setter" "deleter" "cached_property")
)
