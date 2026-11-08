using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpDeclarationHeadTests
{
	[Theory]
	[InlineData("a.py", "@app.route(\n    \"/x\",\n)\ndef handler(request):\n    '''Serve x.'''\n    return 1", 5)]
	[InlineData("a.py", "class Model(Base):\n    name = 1\n    size = 2", 1)]
	[InlineData("a.py", "async def fetch(url: str = \"a:b\") -> bytes:  # note:\n    data = 1", 1)]
	[InlineData("a.py", "def shout(text): return text.upper()\nx = 1", 2)]
	[InlineData("a.cs", "[Fact]\npublic void Test(\n    int value)\n{\n    var x = 1;", 4)]
	[InlineData("a.cs", "public int Size => items.Count;\nint other;", 1)]
	[InlineData("a.js", "function pick(sep = '{', end = \"}\") {\n  return sep;", 1)]
	[InlineData("a.ts", "export const run = async (\n  options: { fast: boolean },\n) => {\n  go();", 3)]
	[InlineData("a.go", "func (s *Server) Serve(listener net.Listener) error {\n\treturn nil", 1)]
	[InlineData("a.rs", "fn borrow<'a>(text: &'a str) -> &'a str {\n    text", 1)]
	[InlineData("a.java", "/* start { */\n@Override\npublic String toString() {\n  return \"\";", 3)]
	[InlineData("a.rb", "def name\n  @name\nend", 3)]
	[InlineData("a.cs", "public void Unfinished(\n    int value,\n    int other", 3)]
	public void CountsTheSignatureLinesOfTheDeclarationHead(string path, string text, int expected)
	{
		Assert.Equal(expected, McpDeclarationHead.CountHeadLines(path, text.Split('\n')));
	}

	[Fact]
	public void CountsNoLinesForAnEmptyHead()
	{
		Assert.Equal(0, McpDeclarationHead.CountHeadLines("a.py", []));
	}
}
