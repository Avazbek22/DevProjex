; A body_statement also represents class, module and do-block bodies. These captures must stay
; anchored to named method owners or a query change could erase an entire container or DSL block.

(method           body: (body_statement) @body)
(singleton_method body: (body_statement) @body)
