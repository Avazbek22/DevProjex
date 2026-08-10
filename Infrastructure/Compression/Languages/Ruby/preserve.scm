; Ruby instance state is declared by assignments inside initialize, so the constructor is part of
; the class's declarative shape rather than an implementation body.
(
  (class
    body: (body_statement
      (method name: (identifier) @constructor) @preserve))
  (#eq? @constructor "initialize")
)
