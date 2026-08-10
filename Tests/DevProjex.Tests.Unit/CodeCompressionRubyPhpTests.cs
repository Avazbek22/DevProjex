using DevProjex.Application.Compression;
using TreeSitter;

namespace DevProjex.Tests.Unit;

public sealed class CodeCompressionRubyPhpTests
{
	[Theory]
	[InlineData("\n")]
	[InlineData("\r\n")]
	public void RubyNamedMethodRemovesCompleteBodyLinesWithoutLeavingBlankLines(string lineEnding)
	{
		var source = string.Join(lineEnding,
			"class Service",
			"  def work(value)",
			"    normalized = value.to_s",
			"    normalized.upcase",
			"  end",
			"end",
			string.Empty);
		var expected = string.Join(lineEnding,
			"class Service",
			"  def work(value)",
			"  end",
			"end",
			string.Empty);

		var (plan, text) = Compress("service.rb", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Equal(expected, text);
		Assert.Equal(string.Empty, Assert.Single(plan.Edits).Replacement);
		AssertStructurePreserved("ruby", source, text);
	}

	[Fact]
	public void RubyPreservesInitializationDslConstantsAndAnonymousCallables()
	{
		const string source = """
			DESCRIPTION = <<~TEXT
			  account #{ENV.fetch("APP_ENV", "development")}
			TEXT

			class Account
			  DEFAULT_ROLE = :member
			  attr_accessor :name
			  has_many :posts
			  validates :name, presence: true

			  def initialize(name)
			    @name = name
			    @formatter = ->(value) { value.to_s.strip }
			    @values = [1, 2].map { |value| value + 1 }
			  end

			  def summary(prefix)
			    ruby_summary_marker = "#{prefix}: #{@name}"
			    ruby_summary_marker
			  end
			end

			describe "Account" do
			  ruby_describe_marker = Account.new("Ada")
			  ruby_describe_marker
			end

			items.each { |item| puts item }
			transform = ->(value) { value + 1 }
			""";

		var (plan, text) = Compress("account.rb", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("DESCRIPTION = <<~TEXT", text, StringComparison.Ordinal);
		Assert.Contains("account #{ENV.fetch", text, StringComparison.Ordinal);
		Assert.Contains("DEFAULT_ROLE = :member", text, StringComparison.Ordinal);
		Assert.Contains("attr_accessor :name", text, StringComparison.Ordinal);
		Assert.Contains("has_many :posts", text, StringComparison.Ordinal);
		Assert.Contains("validates :name, presence: true", text, StringComparison.Ordinal);
		Assert.Contains("@name = name", text, StringComparison.Ordinal);
		Assert.Contains("@formatter = ->(value) { value.to_s.strip }", text, StringComparison.Ordinal);
		Assert.Contains("@values = [1, 2].map { |value| value + 1 }", text, StringComparison.Ordinal);
		Assert.Contains("ruby_describe_marker", text, StringComparison.Ordinal);
		Assert.Contains("items.each { |item| puts item }", text, StringComparison.Ordinal);
		Assert.Contains("transform = ->(value) { value + 1 }", text, StringComparison.Ordinal);
		Assert.DoesNotContain("ruby_summary_marker", text, StringComparison.Ordinal);
		AssertStructurePreserved("ruby", source, text);
	}

	[Fact]
	public void RubyKeepsContainersAndEndlessMethodsButCompressesSingletonMethods()
	{
		const string source = """
			module Billing
			  class Invoice
			    FIELDS = %i[id total]

			    def label = "invoice"

			    def self.build(value)
			      ruby_singleton_marker = new(value)
			      ruby_singleton_marker
			    end
			  end
			end
			""";

		var (plan, text) = Compress("invoice.rb", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("module Billing", text, StringComparison.Ordinal);
		Assert.Contains("class Invoice", text, StringComparison.Ordinal);
		Assert.Contains("FIELDS = %i[id total]", text, StringComparison.Ordinal);
		Assert.Contains("def label = \"invoice\"", text, StringComparison.Ordinal);
		Assert.Contains("def self.build(value)\n    end", text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
		Assert.DoesNotContain("ruby_singleton_marker", text, StringComparison.Ordinal);
		AssertStructurePreserved("ruby", source, text);
	}

	[Theory]
	[InlineData("task.rake")]
	[InlineData("sample.gemspec")]
	public void RubySourceExtensionsUseTheRubyPack(string path)
	{
		var (plan, _) = Compress(path, CodeCompressionFixtures.Ruby);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Equal("ruby", plan.LanguageId);
	}

	[Fact]
	public void PhpMixedDocumentPreservesHtmlStateAndFreeCallables()
	{
		const string htmlBefore = "<!doctype html>\n<main data-page=\"account\">\n";
		const string htmlAfter = "\n</main>\n<footer>Unchanged HTML</footer>\n";
		const string php = """
			<?php
			#[Attribute]
			final class Account
			{
			    private const DEFAULT_ROLE = 'member';
			    private string $status = 'new';

			    public function __construct(
			        public readonly string $name,
			        private int $limit = 10,
			    ) {
			        $this->status = "ready:{$name}";
			        $this->normalizer = function (string $value): string {
			            return trim($value);
			        };
			    }

			    /** Calculate one account score. */
			    #[Route('/score')]
			    public function score(int $value): int
			    {
			        $php_score_marker = $value + $this->limit;
			        return $php_score_marker;
			    }
			}

			$freeClosure = function (int $value): int {
			    $php_free_closure_marker = $value + 1;
			    return $php_free_closure_marker;
			};
			$freeArrow = fn(int $value): int => $value + 1;
			$heredoc = <<<TEXT
			Hello {$freeArrow(1)}
			TEXT;
			$nowdoc = <<<'TEXT'
			Literal {$value}
			TEXT;
			?>
			""";
		var source = htmlBefore + php + htmlAfter;

		var (plan, text) = Compress("account.phtml", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Equal("php", plan.LanguageId);
		Assert.StartsWith(htmlBefore, text, StringComparison.Ordinal);
		Assert.EndsWith(htmlAfter, text, StringComparison.Ordinal);
		Assert.Contains("private const DEFAULT_ROLE = 'member';", text, StringComparison.Ordinal);
		Assert.Contains("private string $status = 'new';", text, StringComparison.Ordinal);
		Assert.Contains("public readonly string $name", text, StringComparison.Ordinal);
		Assert.Contains("private int $limit = 10", text, StringComparison.Ordinal);
		Assert.Contains("$this->status = \"ready:{$name}\";", text, StringComparison.Ordinal);
		Assert.Contains("$this->normalizer = function", text, StringComparison.Ordinal);
		Assert.Contains("/** Calculate one account score. */", text, StringComparison.Ordinal);
		Assert.Contains("#[Route('/score')]", text, StringComparison.Ordinal);
		Assert.Contains("$php_free_closure_marker", text, StringComparison.Ordinal);
		Assert.Contains("$freeArrow = fn(int $value): int => $value + 1;", text, StringComparison.Ordinal);
		Assert.Contains("Hello {$freeArrow(1)}", text, StringComparison.Ordinal);
		Assert.Contains("Literal {$value}", text, StringComparison.Ordinal);
		Assert.DoesNotContain("$php_score_marker", text, StringComparison.Ordinal);
		AssertStructurePreserved("php", source, text);
	}

	[Fact]
	public void PhpPreservesEnumCasesAndCompressesEnumAndTraitMethods()
	{
		const string source = """
			<?php
			trait Auditable
			{
			    public function audit(): string
			    {
			        $php_trait_marker = 'audit';
			        return $php_trait_marker;
			    }
			}

			enum Status: string
			{
			    case Ready = 'ready';
			    case Failed = 'failed';
			    public const DEFAULT_LABEL = 'unknown';

			    public function label(): string
			    {
			        $php_enum_marker = $this->value;
			        return $php_enum_marker;
			    }
			}
			""";

		var (plan, text) = Compress("status.php", source);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Contains("case Ready = 'ready';", text, StringComparison.Ordinal);
		Assert.Contains("case Failed = 'failed';", text, StringComparison.Ordinal);
		Assert.Contains("public const DEFAULT_LABEL = 'unknown';", text, StringComparison.Ordinal);
		Assert.Contains("public function audit(): string", text, StringComparison.Ordinal);
		Assert.Contains("public function label(): string", text, StringComparison.Ordinal);
		Assert.DoesNotContain("$php_trait_marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("$php_enum_marker", text, StringComparison.Ordinal);
		AssertStructurePreserved("php", source, text);
	}

	[Theory]
	[InlineData("service.php")]
	[InlineData("template.phtml")]
	public void PhpSourceExtensionsUseTheMixedPhpPack(string path)
	{
		var (plan, _) = Compress(path, CodeCompressionFixtures.Php);

		Assert.Equal(CodeCompressionOutcome.Compressed, plan.Outcome);
		Assert.Equal("php", plan.LanguageId);
	}

	private static (CodeCompressionPlan Plan, string Text) Compress(string path, string source)
	{
		using var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var scope = compressor.CreateScope(Path.GetTempPath());
		var analysis = scope.Analyze(path, path, source, TestContext.Current.CancellationToken);
		return (analysis.Plan, analysis.GetResult(source).Text);
	}

	private static void AssertStructurePreserved(string languageId, string source, string transformed)
	{
		using var harness = CodeCompressionTestHarness.For(languageId);
		Assert.True(
			CountParseDefects(harness.Parser, transformed) <= CountParseDefects(harness.Parser, source),
			$"{languageId}: compression introduced a parse defect");
		Assert.Equal(
			ReadDeclarations(harness, source),
			ReadDeclarations(harness, transformed));
	}

	private static string[] ReadDeclarations(CodeCompressionTestHarness harness, string source)
	{
		using var tree = harness.Parser.Parse(source)!;
		using var cursor = harness.Declarations.Execute(tree.RootNode);
		return cursor.Matches
			.Select(static match =>
			{
				var declaration = match.Captures.First(static capture => capture.Name == "declaration");
				var name = match.Captures.FirstOrDefault(static capture => capture.Name == "name");
				return $"{declaration.Node.Type}:{name?.Node.Text ?? string.Empty}";
			})
			.Order(StringComparer.Ordinal)
			.ToArray();
	}

	private static int CountParseDefects(Parser parser, string source)
	{
		using var tree = parser.Parse(source)!;
		using var cursor = new TreeCursor(tree.RootNode);
		var defects = 0;
		while (true)
		{
			var node = cursor.CurrentNode;
			if (node.IsError || node.IsMissing || node.IsNamed && node.StartIndex == node.EndIndex)
				defects++;
			if (cursor.GotoFirstChild())
				continue;
			while (!cursor.GotoNextSibling())
			{
				if (!cursor.GotoParent())
					return defects;
			}
		}
	}
}
