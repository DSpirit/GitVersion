using GitVersion.Configuration;
using GitVersion.Extensions;

namespace GitVersion.VersionCalculation.TrunkBased.Trunk;

internal abstract class CommitOnTrunkWithStableTagBase : ITrunkBasedIncrementer
{
    public virtual bool MatchPrecondition(TrunkBasedIteration iteration, TrunkBasedCommit commit, TrunkBasedContext context)
        => commit.Configuration.IsMainBranch && !commit.HasChildIteration
            && context.SemanticVersion?.IsPreRelease == false;

    public virtual IEnumerable<BaseVersionV2> GetIncrements(TrunkBasedIteration iteration, TrunkBasedCommit commit, TrunkBasedContext context)
    {
        context.BaseVersionSource = commit.Value;

        yield return BaseVersionV2.ShouldIncrementFalse(
            source: GetType().Name,
            baseVersionSource: context.BaseVersionSource,
            semanticVersion: context.SemanticVersion.NotNull()
        );

        context.Label = commit.Configuration.GetBranchSpecificLabel(commit.BranchName, null);
    }
}
