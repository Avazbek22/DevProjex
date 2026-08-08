using DevProjex.Application.Compression;

namespace DevProjex.Tests.Unit;

public sealed class CodeStructureGateTests
{
	// A method spanning [0, 100) whose body [30, 90) is removed, with a local function declared
	// inside that body. This is the shape that would reject every real C# file if the gate
	// compared raw declaration sets, and it would do so silently - a rejection is indistinguishable
	// from a conservative success unless something asserts it.
	private static readonly CodeDeclaration Method = new("method", "Run", 0, 100);
	private static readonly CodeDeclaration LocalFunction = new("local_function", "Helper", 40, 20);
	private static readonly CodeCompressionEdit BodyEdit = new(30, 60, "{ /* … */ }");

	private static readonly HashSet<string> OwnerKinds =
		new(StringComparer.Ordinal) { "method", "local_function" };

	private static CodeStructureGateVerdict Evaluate(
		IReadOnlyList<CodeDeclaration> original,
		IReadOnlyList<CodeDeclaration> compressed,
		IReadOnlyList<CodeParseDefect>? originalDefects = null,
		IReadOnlyList<CodeParseDefect>? compressedDefects = null,
		IReadOnlyList<CodeCompressionEdit>? edits = null,
		IReadOnlySet<string>? ownerKinds = null) =>
		CodeStructureGate.Evaluate(
			original,
			compressed,
			originalDefects ?? [],
			compressedDefects ?? [],
			edits ?? [BodyEdit],
			ownerKinds ?? OwnerKinds);

	[Fact]
	public void DeclarationInsideARemovedBody_IsNotExpectedToSurvive()
	{
		var verdict = Evaluate([Method, LocalFunction], [Method]);

		Assert.Equal(CodeStructureGateVerdict.Accepted, verdict);
	}

	[Fact]
	public void IsExcised_IsContainment_NotOverlap()
	{
		// The method contains the edit; the local function is contained by it. Only the second
		// is excised, which is the whole distinction the fingerprint rests on.
		Assert.False(CodeStructureGate.IsExcised(Method, [BodyEdit]));
		Assert.True(CodeStructureGate.IsExcised(LocalFunction, [BodyEdit]));
	}

	[Fact]
	public void SurvivingDeclarationThatDisappeared_IsRejected()
	{
		var sibling = new CodeDeclaration("method", "Other", 120, 40);

		var verdict = Evaluate([Method, sibling], [Method]);

		Assert.Equal(CodeStructureGateVerdict.RejectedDeclarationsLost, verdict);
	}

	[Fact]
	public void DeclarationAppearingFromNowhere_IsRejected()
	{
		var invented = new CodeDeclaration("method", "Ghost", 120, 40);

		var verdict = Evaluate([Method], [Method, invented]);

		Assert.Equal(CodeStructureGateVerdict.RejectedDeclarationsAdded, verdict);
	}

	[Fact]
	public void RenamedDeclarationAtTheSamePosition_IsRejected()
	{
		var renamed = Method with { Name = "Renamed" };

		var verdict = Evaluate([Method], [renamed]);

		Assert.NotEqual(CodeStructureGateVerdict.Accepted, verdict);
	}

	[Fact]
	public void SameDeclarationMultisetInDifferentParserOrder_IsAccepted()
	{
		var field = new CodeDeclaration("field", "_waitForRenderPasses", 120, 40);
		var initializerMethodGroup = new CodeDeclaration("field", "WaitForRenderPassesAsync", 120, 40);

		var verdict = Evaluate(
			[Method, field, initializerMethodGroup],
			[Method, initializerMethodGroup, field]);

		Assert.Equal(CodeStructureGateVerdict.Accepted, verdict);
	}

	[Fact]
	public void ReorderedDeclarationComparisonStillCountsDuplicates()
	{
		var field = new CodeDeclaration("field", "_waitForRenderPasses", 120, 40);

		Assert.Equal(
			CodeStructureGateVerdict.RejectedDeclarationsAdded,
			Evaluate([Method, field], [Method, field, field]));
		Assert.Equal(
			CodeStructureGateVerdict.RejectedDeclarationsLost,
			Evaluate([Method, field, field], [Method, field]));
	}

	[Fact]
	public void DefectInsideAReplacement_IsAlwaysNew()
	{
		// Unmappable: the node sits in text the splice invented, so it cannot have existed before.
		var verdict = Evaluate(
			[Method],
			[Method],
			originalDefects: [],
			compressedDefects: [new CodeParseDefect("ERROR", -1)]);

		Assert.Equal(CodeStructureGateVerdict.RejectedNewDefects, verdict);
	}

	[Fact]
	public void PreExistingDefectAtTheSameSourcePosition_IsTolerated()
	{
		// The shipped C# grammar does not understand collection expressions, so a quarter of a
		// modern codebase parses with defects. Refusing those files would cost that coverage for
		// no reason: what matters is that compression introduced nothing.
		var defect = new CodeParseDefect("MISSING", 210);

		var verdict = Evaluate([Method], [Method], [defect], [defect]);

		Assert.Equal(CodeStructureGateVerdict.Accepted, verdict);
	}

	[Fact]
	public void EqualDefectCountsWithDifferentPositions_AreRejected()
	{
		// One defect disappears, another appears. A counter sees no change; the file is broken.
		var verdict = Evaluate(
			[Method],
			[Method],
			[new CodeParseDefect("MISSING", 210)],
			[new CodeParseDefect("MISSING", 640)]);

		Assert.Equal(CodeStructureGateVerdict.RejectedNewDefects, verdict);
	}

	[Fact]
	public void SameDefectPositionWithADifferentKind_IsRejected()
	{
		var verdict = Evaluate(
			[Method],
			[Method],
			[new CodeParseDefect("MISSING", 210)],
			[new CodeParseDefect("ERROR", 210)]);

		Assert.Equal(CodeStructureGateVerdict.RejectedNewDefects, verdict);
	}

	[Fact]
	public void DuplicateDefectAtTheSamePosition_IsNotHiddenBySetComparison()
	{
		var defect = new CodeParseDefect("MISSING", 210);

		var verdict = Evaluate(
			[Method],
			[Method],
			[defect],
			[defect, defect]);

		Assert.Equal(CodeStructureGateVerdict.RejectedNewDefects, verdict);
	}

	[Fact]
	public void FewerDefectsThanBefore_IsAccepted()
	{
		var verdict = Evaluate(
			[Method],
			[Method],
			[new CodeParseDefect("MISSING", 210), new CodeParseDefect("ERROR", 300)],
			[new CodeParseDefect("MISSING", 210)]);

		Assert.Equal(CodeStructureGateVerdict.Accepted, verdict);
	}

	[Fact]
	public void EditCuttingThroughADeclaration_IsRejected()
	{
		// Neither contained nor containing: the query captured something that is not a leaf body,
		// and splicing it would cut a declaration in half.
		var straddling = new CodeCompressionEdit(80, 60, "{ /* … */ }");

		var verdict = Evaluate([Method], [Method], edits: [straddling]);

		Assert.Equal(CodeStructureGateVerdict.RejectedEditCrossesDeclaration, verdict);
	}

	[Fact]
	public void NestedTypesAndTheirMembers_SurviveTogether()
	{
		var outer = new CodeDeclaration("class", "Outer", 0, 300);
		var nested = new CodeDeclaration("class", "Nested", 50, 200);
		var member = new CodeDeclaration("method", "Work", 80, 100);
		var body = new CodeCompressionEdit(110, 60, "{ /* … */ }");

		var verdict = Evaluate([outer, nested, member], [outer, nested, member], edits: [body]);

		Assert.Equal(CodeStructureGateVerdict.Accepted, verdict);
	}

	[Fact]
	public void EditSwallowingAContainerBody_IsRejectedEvenThoughContainmentWouldExcuseIt()
	{
		// Found by an adversarial review and reproduced on the real code: containment alone is
		// self-justifying. An edit covering a whole interface body makes every method inside it
		// "excised", so the declaration comparison expects them gone and the gate would accept
		// wholesale deletion. The innermost declaration around an edit must be one that legitimately
		// owns an executable body.
		var contract = new CodeDeclaration("interface", "IStore", 0, 58);
		var save = new CodeDeclaration("method", "Save", 19, 18);
		var load = new CodeDeclaration("method", "Load", 38, 18);
		var wholeBody = new CodeCompressionEdit(17, 41, "{ /* … */ }");

		var verdict = Evaluate([contract, save, load], [contract], edits: [wholeBody]);

		Assert.Equal(CodeStructureGateVerdict.RejectedEditOutsideAnExecutableBody, verdict);
	}

	[Fact]
	public void EditInsideAMethodBody_IsStillAccepted()
	{
		// The same rule must not refuse the ordinary case it is guarding.
		var type = new CodeDeclaration("class", "Widget", 0, 200);
		var method = new CodeDeclaration("method", "Run", 20, 120);
		var body = new CodeCompressionEdit(40, 90, "{ /* … */ }");

		var verdict = Evaluate([type, method], [type, method], edits: [body]);

		Assert.Equal(CodeStructureGateVerdict.Accepted, verdict);
	}

	[Fact]
	public void WholeFileEdit_IsRejectedEvenThoughEverythingIsTriviallyExcised()
	{
		// The second half of the same adversarial finding: an edit covering the entire file makes
		// every declaration excised, so expected and actual are both empty and every other check
		// agrees. An edit that is inside no declaration at all is never a leaf body.
		var type = new CodeDeclaration("class", "Widget", 0, 105);
		var everything = new CodeCompressionEdit(0, 105, "// …\n");

		var verdict = Evaluate([type], [], edits: [everything]);

		Assert.Equal(CodeStructureGateVerdict.RejectedEditOutsideAnExecutableBody, verdict);
	}

	[Fact]
	public void NoEditsAtAll_AcceptsAnIdenticalStructure()
	{
		var verdict = Evaluate([Method], [Method], edits: []);

		Assert.Equal(CodeStructureGateVerdict.Accepted, verdict);
	}
}
