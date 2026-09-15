# Code Compression

Code compression is an opt-in content transformation for fitting real codebases
into AI context windows. It keeps the declaration surface of your code — the part
a reader or a model needs to navigate and reason about an API — and empties named
implementation bodies. Source files are never modified; the transformation exists
only in generated output. Files are parsed with pinned
[Tree-sitter](https://tree-sitter.github.io/tree-sitter/) grammars; the
per-language preservation rules are DevProjex's own.

The rule to remember: **compression removes what a body does, never what a
declaration says.** Constant values, field and property initializers, parameter
lists, and one-line expression bodies all survive — by design, not by accident.
They describe project state and public behavior, which is exactly the context an
AI needs to reason correctly about the code it cannot see.

## What stays and what goes

Kept byte-for-byte:

- Declarations, signatures, parameter lists, and class/module structure.
- Fields and language-level properties, including initializers and property
  accessors.
- Constant values (`TaxRate = 0.21m` keeps its `0.21m`).
- Expression bodies that fit on one source line, as signature-level context.
- Free lambdas, closures, and bare callbacks: removing an unbound body would
  leave no useful name behind.
- Annotations, attributes, enum cases.

Replaced with a minimal syntax-valid placeholder:

- Block bodies of named methods, functions, and constructors — `{ }` in
  brace-based languages, `...` in Python, an empty body between the declaration
  and `end` in Ruby.
- Multiline expression bodies: a body that spans lines is implementation and is
  compressed like a block body.

The engine is deliberately conservative: an unsupported file, or one it cannot
process safely, stays complete rather than risking a broken transformation.

## Language by language, on real output

Fourteen languages have body compression: C, C++, C#, Go, Java, JavaScript,
Kotlin, PHP, Python, Ruby, Rust, Scala, TSX, and TypeScript. Every "after" block
below is actual DevProjex output, not an illustration.

### C#

Before:

```csharp
public class PriceCalculator
{
    public const decimal TaxRate = 0.21m;
    private readonly List<Order> _orders = new();
    public int MaxRetries { get; set; } = 3;

    public decimal Subtotal => _orders.Sum(o => o.Total);

    public decimal CalculateTotal(bool applyTax)
    {
        var subtotal = Subtotal;
        if (applyTax)
            subtotal *= 1 + TaxRate;
        return Math.Round(subtotal, 2);
    }
}
```

After:

```csharp
public class PriceCalculator
{
    public const decimal TaxRate = 0.21m;
    private readonly List<Order> _orders = new();
    public int MaxRetries { get; set; } = 3;

    public decimal Subtotal => _orders.Sum(o => o.Total);

    public decimal CalculateTotal(bool applyTax)
    { }
}
```

Only the multiline block body is gone. The constant keeps its value, the field
keeps its initializer, the auto-property keeps its default, and the one-line
expression-bodied member `Subtotal` survives byte-for-byte — a one-liner is
signature-level context, not implementation.

### Python

Before:

```python
class Repository:
    """Stores and finds orders."""

    def __init__(self, connection):
        self.connection = connection
        self.cache = {}

    def find_by_id(self, order_id):
        """Return one order or None."""
        if order_id in self.cache:
            return self.cache[order_id]
        row = self.connection.fetch(order_id)
        return Order(row) if row else None
```

After:

```python
class Repository:
    """Stores and finds orders."""

    def __init__(self, connection):
        self.connection = connection
        self.cache = {}

    def find_by_id(self, order_id):
        """Return one order or None."""
        ...
```

Python gets three special guarantees. `__init__` (and `__post_init__`) remain
complete, because instance state is declared there. A leading docstring is kept
even when the body under it is emptied — for a model, the docstring often carries
more signal than the implementation. And the placeholder is `...`, which is valid
Python.

### TypeScript / JavaScript / TSX

Before:

```typescript
export const settings = { retries: 3, verbose: false };

export function buildReport(orders: Order[]): Report {
    const lines = orders.map(formatLine);
    const total = orders.reduce((sum, o) => sum + o.total, 0);
    return { lines, total };
}

const formatLine = (o: Order) => `${o.id}: ${o.total}`;

items.forEach(function (item) {
    console.log(item.name);
});
```

After:

```typescript
export const settings = { retries: 3, verbose: false };

export function buildReport(orders: Order[]): Report { }

const formatLine = (o: Order) => `${o.id}: ${o.total}`;

items.forEach(function (item) {
    console.log(item.name);
});
```

Named, stably bound functions are compressed — exported functions, object
properties, and functions wrapped one or two calls deep under a binding. Two
things survive on purpose: the one-line arrow bound to `formatLine` (a one-liner
is context), and the bare callback inside `forEach` — it has no name, so an
emptied body would leave nothing useful behind.

### Kotlin

Before:

```kotlin
data class Order(val id: Int, val total: Double)

class Basket {
    var discount: Double = 0.0
        set(value) { field = value.coerceIn(0.0, 0.5) }

    private val items = mutableListOf<Order>()

    init {
        require(discount >= 0.0)
    }

    fun total(): Double {
        val sum = items.sumOf { it.total }
        return sum * (1 - discount)
    }

    fun count() = items.size
}
```

After:

```kotlin
data class Order(val id: Int, val total: Double)

class Basket {
    var discount: Double = 0.0
        set(value) { field = value.coerceIn(0.0, 0.5) }

    private val items = mutableListOf<Order>()

    init { }

    fun total(): Double { }

    fun count() = items.size
}
```

Data classes, properties with their initializers, and custom accessors are
preserved — the `set(value)` body stays because accessors describe behavior of
state. `init` blocks and named block functions are compressed; the one-line
expression function `count()` survives. Kotlin never receives an `= { }`
replacement, because in Kotlin that form means a lambda.

### Scala

Before:

```scala
case class Order(id: Int, total: BigDecimal)

class Basket(initial: List[Order]):
  val items: List[Order] = initial

object Basket {
  val TaxRate = BigDecimal("0.21")

  def totalWithTax(orders: List[Order]): BigDecimal = {
    val sum = orders.map(_.total).sum
    sum * (1 + TaxRate)
  }

  def count(orders: List[Order]) = orders.size
}
```

After:

```scala
case class Order(id: Int, total: BigDecimal)

class Basket(initial: List[Order]):
  val items: List[Order] = initial

object Basket {
  val TaxRate = BigDecimal("0.21")

  def totalWithTax(orders: List[Order]): BigDecimal = { }

  def count(orders: List[Order]) = orders.size
}
```

`val`, `var`, `given`, case-class parameters, and class-level constructor
statements are preserved; braced named `def` bodies are compressed. Note the
Scala 3 significant-indentation class: it stays complete, because its replacement
boundary is not structurally stable — the conservative choice wins over the
smaller output.

### Ruby

Before:

```ruby
class Basket
  attr_reader :items

  def initialize(items)
    @items = items
    @discount = 0.0
  end

  def total
    sum = @items.sum(&:price)
    sum * (1 - @discount)
  end
end
```

After:

```ruby
class Basket
  attr_reader :items

  def initialize(items)
    @items = items
    @discount = 0.0
  end

  def total
  end
end
```

`initialize` remains complete — that is where instance state lives. Named method
bodies are emptied between the declaration and `end`, without collapsing class,
module, or DSL blocks.

### PHP

Before:

```php
<?php
class Basket
{
    public const TAX_RATE = 0.21;
    private array $items = [];

    public function __construct(private LoggerInterface $logger)
    {
        $this->logger->info('basket created');
    }

    public function total(): float
    {
        $sum = array_sum(array_map(fn($i) => $i->price, $this->items));
        return $sum * (1 + self::TAX_RATE);
    }
}
```

After:

```php
<?php
class Basket
{
    public const TAX_RATE = 0.21;
    private array $items = [];

    public function __construct(private LoggerInterface $logger)
    {
        $this->logger->info('basket created');
    }

    public function total(): float
    { }
}
```

Constants, typed properties, enum cases, and `__construct` — including its body —
are kept; constructor promotion means state is declared there. Named functions
and methods are compressed in both PHP-only and mixed HTML/PHP files, while
standalone anonymous and arrow functions remain complete.

### C

Before:

```c
#include "basket.h"

static const double tax_rate = 0.21;

double basket_total(const struct basket *b)
{
    double sum = 0.0;
    for (size_t i = 0; i < b->count; i++)
        sum += b->items[i].price;
    return sum * (1.0 + tax_rate);
}
```

After:

```c
#include "basket.h"

static const double tax_rate = 0.21;

double basket_total(const struct basket *b)
{ }
```

Includes, constants with their values, and type definitions stay complete; named
function bodies become `{ }`.

### C++

Before:

```cpp
#include "basket.h"

const double kTaxRate = 0.21;

class Basket {
public:
    explicit Basket(std::vector<Order> items) : items_(std::move(items)) {
        validate();
        log_created();
    }

    double Total() const {
        double sum = 0.0;
        for (const auto& item : items_) {
            sum += item.price;
        }
        return sum * (1.0 + kTaxRate);
    }

    size_t Count() const { return items_.size(); }

private:
    std::vector<Order> items_;
};
```

After:

```cpp
#include "basket.h"

const double kTaxRate = 0.21;

class Basket {
public:
    explicit Basket(std::vector<Order> items) : items_(std::move(items)) { }

    double Total() const { }

    size_t Count() const { }

private:
    std::vector<Order> items_;
};
```

The constructor body is emptied, but its member-initializer list
`: items_(std::move(items))` survives — the initializer list declares member
state and belongs to the declaration, not the body. Note that the one-line
`Count()` is compressed too: C++ has no expression-body syntax, so a braced
body is a block body even when it fits on one line. Member fields and access
sections stay complete.

### Go

Before:

```go
package basket

const TaxRate = 0.21

type Basket struct {
	Items []Order
}

func (b *Basket) Total() float64 {
	sum := 0.0
	for _, item := range b.Items {
		sum += item.Price
	}
	return sum * (1 + TaxRate)
}
```

After:

```go
package basket

const TaxRate = 0.21

type Basket struct {
	Items []Order
}

func (b *Basket) Total() float64 { }
```

Package clause, constants, and struct definitions stay complete; named function
and method bodies become `{ }` with the receiver and signature intact.

### Java

Before:

```java
public class Basket {
    public static final double TAX_RATE = 0.21;
    private final List<Order> items = new ArrayList<>();

    public double total() {
        double sum = 0;
        for (Order item : items) {
            sum += item.price();
        }
        return sum * (1 + TAX_RATE);
    }
}
```

After:

```java
public class Basket {
    public static final double TAX_RATE = 0.21;
    private final List<Order> items = new ArrayList<>();

    public double total() { }
}
```

Constants and fields keep their initializers — including `new ArrayList<>()` —
and method bodies become `{ }`.

### Rust

Before:

```rust
pub const TAX_RATE: f64 = 0.21;

pub struct Basket {
    pub items: Vec<Order>,
}

impl Basket {
    pub fn total(&self) -> f64 {
        let sum: f64 = self.items.iter().map(|i| i.price).sum();
        sum * (1.0 + TAX_RATE)
    }
}
```

After:

```rust
pub const TAX_RATE: f64 = 0.21;

pub struct Basket {
    pub items: Vec<Order>,
}

impl Basket {
    pub fn total(&self) -> f64 { }
}
```

Constants with values, struct definitions, and the `impl` structure stay
complete; named function bodies become `{ }`.

## Combining with comment and blank-line stripping

Compression, comment removal (`--strip-comments`), and blank-line removal
(`--strip-blank-lines`) are independent switches that share one syntax engine.
They answer different questions, which is easiest to see on the same Python
class:

- `--compress-code` **keeps** the docstring and empties the body: the docstring
  is signature-level context.
- `--strip-comments` **removes** the docstring and keeps the body: docstrings
  are documentation, and this switch strips documentation.

Enable both for the smallest faithful output: signatures with neither bodies nor
comments.

Comment and blank-line stripping also extend coverage to 20 language packs in
total: the 14 compression languages plus six comments-only packs — HTML, CSS,
TOML, Bash, the XML project-file family, and YAML.

## Where it applies

The same transformed content feeds every surface: token metrics, the live
preview, context documents, and folder/ZIP project copies. In the desktop app it
is the **Compress code** switch; the status line reports the result with
estimated token counts before and after. In the CLI it is `--compress-code`,
available on `analyze`, `export context`, `export project`, and `open`, and off
in the `standard` profile. If compression fails, files are left unchanged in the
output rather than partially transformed.

As with every DevProjex transformation, a compressed project copy is intentionally
not a byte-for-byte copy and may not build or run.

## When grammars are unavailable

An unsupported language, a parse that contains errors, and an unavailable grammar
are different outcomes. An unsupported language is outside the selected
transformation's language set. A parse or structural safety failure leaves that
file complete without claiming that the grammar is missing. A missing, unreadable,
ABI-incompatible, or invalid native grammar instead marks compression unavailable
for that language; a delivery source with no discoverable grammars marks it
unavailable for every language.

Unavailable compression never produces a partial transformation: affected files
stay complete. CLI `analyze` and `export context` write
`DPX-COMPRESSION-UNAVAILABLE` to stderr with the grammar resource name or content
directory and include the same warning in JSON diagnostics. It remains a successful
command, including under `analyze --strict`, because the requested output is still
complete and safe. MCP `analyze`, `pack_context`, and `get_file` append trusted
`[Compression unavailable] ...` text outside project-data delimiters when their
effective selection requests compression; `analyze` also returns a structured
`compressionUnavailable` object. The Terminal Workspace shows a notification and
an unavailable marker in its metrics line. The desktop app marks the compression
switch and status text without opening a modal dialog.

Content delivery remains fail-closed. If a shipped `grammars` directory exists but
is incomplete, DevProjex does not fall back to embedded resources; the missing
grammar is reported and the affected source stays complete.

## What to expect in numbers

Savings depend on what a project contains. Measured on DevProjex's own C#
application sources (619 files), compression shrinks the packed context by about
69% — roughly 3× smaller. A mixed repository saves less, because compression only
touches code, never test fixtures, JSON assets, or documentation.

## See also

- [Command Line](CommandLine.md) — exact CLI flag semantics and profiles.
- [Hide Secrets](HideSecrets.md) and [Hide private data](HidePrivateData.md) —
  the redaction transformations, which run independently of compression.
