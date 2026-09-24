using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("DevProjex.RankingEval")]

namespace DevProjex.Application.Ranking;

public enum FocusSeedState
{
	Resolved,
	NoResolvedNeighbors,
	ExtractionFailed,
	Unsupported
}

public enum FocusRankingRelation
{
	DependentOf,
	DependencyOf,
	LinkedWith
}

public sealed record FocusRankingSeedRequest(string Requested, string FullPath);

public sealed record FocusRankingRequest(IReadOnlyList<FocusRankingSeedRequest> Seeds);

public sealed record FocusRankingSeed(
	string Requested,
	string Path,
	FocusSeedState State,
	string? Reason = null);

public sealed record FocusRankingVia(string Path, FocusRankingRelation Relation);

public sealed record FocusRankingSummary(
	string Algorithm,
	string WithinHop,
	IReadOnlyList<FocusRankingSeed> Seeds,
	IReadOnlyDictionary<int, int> Hops,
	int HopsBeyond,
	int MaxHop,
	int Unreachable);
