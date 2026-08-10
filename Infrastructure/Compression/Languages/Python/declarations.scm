; The structural fingerprint for Python. Decorators are attached to the definition node, so they are
; covered implicitly: losing one changes the declaration's span and shows up as a difference.

(class_definition    name: (identifier) @name) @declaration
(function_definition name: (identifier) @name) @declaration
