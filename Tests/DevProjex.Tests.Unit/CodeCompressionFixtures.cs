namespace DevProjex.Tests.Unit;

/// <summary>
/// Golden inputs for compression. Each one carries a trap that has already caused, or would cause,
/// a silent defect — they are not decorative samples.
/// </summary>
internal static class CodeCompressionFixtures
{
	/// <summary>
	/// Traps: a nested type inside a class body; a local function declared inside a method body
	/// (which legitimately disappears and would otherwise make the gate reject the file forever);
	/// an expression-bodied member too short to be worth replacing; a collection expression, which
	/// the shipped grammar does not understand and which therefore parses with a defect.
	/// </summary>
	public const string CSharp = """
		namespace Sample.Services;

		public sealed class Widget : IWidget
		{
		    private readonly IStore _store;
		    private const string Key = "widget";

		    public Widget(IStore store)
		    {
		        _store = store;
		    }

		    public int Count => _store.Count;

		    public IReadOnlyList<string> Names { get; } = [];

		    public async Task<int> SumAsync(IEnumerable<int> values)
		    {
		        static int Double(int value) => value * 2;

		        var total = 0;
		        foreach (var value in values)
		            total += Double(value);
		        return await Task.FromResult(total);
		    }

		    private string Describe(int value)
		    {
		        // implementation comment, not documentation
		        return $"{Key}:{value}";
		    }

		    public enum Mode
		    {
		        Fast,
		        Slow
		    }

		    private sealed class Nested
		    {
		        public void Work()
		        {
		            Console.WriteLine("nested");
		        }
		    }
		}
		""";

	/// <summary>
	/// Traps: a docstring, which lives INSIDE the body and must survive; a leading implementation
	/// comment, which must NOT be mistaken for one; a body that is only a docstring; a one-line
	/// body too short to pay for a placeholder; a nested class whose suite must never be collapsed.
	/// </summary>
	public const string Python = """
		import os


		class Model:
		    \"\"\"Trains and evaluates a model.\"\"\"

		    def __init__(self, options):
		        \"\"\"Build a model.

		        Multi-line docstring.
		        \"\"\"
		        self.options = options
		        self._cache = {}

		    def run(self, data):
		        # implementation comment, not a docstring
		        total = 0
		        for row in data:
		            total += row
		        return total

		    def only_doc(self):
		        \"\"\"Nothing but documentation.\"\"\"

		    class Inner:
		        def work(self):
		            print("nested")


		def free_function(a, b):
		    \"\"\"Doc.\"\"\"
		    return a + b
		""";

	/// <summary>Python source with the escaped triple quotes turned back into real ones.</summary>
	public static string PythonSource => Python.Replace("\\\"\\\"\\\"", "\"\"\"", StringComparison.Ordinal);

	public const string C = """
		int add(int left, int right) {
		    int total = left + right;
		    return total;
		}
		""";

	public const string Cpp = """
		class Calculator {
		public:
		    int add(int left, int right) {
		        int total = left + right;
		        return total;
		    }
		};
		""";

	public const string Java = """
		class Calculator {
		    Calculator() {
		        System.out.println("created");
		    }

		    int add(int left, int right) {
		        int total = left + right;
		        return total;
		    }
		}
		""";

	public const string JavaScript = """
		function add(left, right) {
		    const total = left + right;
		    return total;
		}

		class Calculator {
		    multiply(left, right) {
		        return left * right;
		    }
		}
		""";

	public const string TypeScript = """
		export function add(left: number, right: number): number {
		    const total = left + right;
		    return total;
		}

		class Calculator {
		    multiply(left: number, right: number): number {
		        return left * right;
		    }
		}
		""";

	public const string Tsx = """
		export function Widget({ name }: { name: string }) {
		    const label = name.toUpperCase();
		    return <section>{label}</section>;
		}
		""";

	public const string Go = """
		package sample

		func add(left int, right int) int {
			total := left + right
			return total
		}
		""";

	public const string Rust = """
		pub fn add(left: i32, right: i32) -> i32 {
		    let total = left + right;
		    total
		}
		""";

	public const string Ruby = """
		class Calculator
		  UNIT = "points"
		  attr_reader :offset

		  def initialize(offset)
		    @offset = offset
		  end

		  def add(left, right)
		    total = left + right + @offset
		    "#{total} #{UNIT}"
		  end

		  def self.identity(value)
		    normalized = value.to_s
		    normalized
		  end
		end
		""";

	public const string Php = """
		<!doctype html>
		<title>Calculator</title>
		<?php
		final class Calculator
		{
		    private const UNIT = 'points';
		    private int $offset = 0;

		    public function __construct(private string $name)
		    {
		        $this->offset = strlen($name);
		    }

		    public function add(int $left, int $right): int
		    {
		        $total = $left + $right + $this->offset;
		        return $total;
		    }
		}
		?>
		<footer>Unchanged HTML</footer>
		""";

	public const string Scala = """
		package sample

		final class Repository(private val root: String):
		  val normalize: String => String = value => value.trim
		  var count: Int = 0

		  def load(name: String): String =
		    val path = s"$root/$name"
		    count += 1
		    path

		  def size: Int = count

		object Repository {
		  def create(root: String): Repository = {
		    val normalized = root.trim
		    new Repository(normalized)
		  }
		}
		""";
}
