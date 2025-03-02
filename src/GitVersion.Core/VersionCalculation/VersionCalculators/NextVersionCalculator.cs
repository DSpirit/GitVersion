using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using GitVersion.Configuration;
using GitVersion.Core;
using GitVersion.Extensions;
using GitVersion.Logging;

namespace GitVersion.VersionCalculation;

internal class NextVersionCalculator(
    ILog log,
    Lazy<GitVersionContext> versionContext,
    IEnumerable<IDeploymentModeCalculator> deploymentModeCalculators,
    IEnumerable<IVersionStrategy> versionStrategies,
    IEffectiveBranchConfigurationFinder effectiveBranchConfigurationFinder,
    IIncrementStrategyFinder incrementStrategyFinder,
    ITaggedSemanticVersionRepository taggedSemanticVersionRepository)
    : INextVersionCalculator
{
    private readonly ILog log = log.NotNull();
    private readonly Lazy<GitVersionContext> versionContext = versionContext.NotNull();
    private readonly IVersionStrategy[] versionStrategies = versionStrategies.NotNull().ToArray();
    private readonly IEffectiveBranchConfigurationFinder effectiveBranchConfigurationFinder = effectiveBranchConfigurationFinder.NotNull();
    private readonly IIncrementStrategyFinder incrementStrategyFinder = incrementStrategyFinder.NotNull();

    private GitVersionContext Context => this.versionContext.Value;

    public virtual SemanticVersion FindVersion()
    {
        this.log.Info($"Running against branch: {Context.CurrentBranch} ({Context.CurrentCommit.ToString() ?? "-"})");

        var branchConfiguration = Context.Configuration.GetBranchConfiguration(Context.CurrentBranch);
        EffectiveConfiguration effectiveConfiguration = new(Context.Configuration, branchConfiguration);

        bool someBranchRelatedPropertiesMightBeNotKnown = branchConfiguration.Increment == IncrementStrategy.Inherit;

        if (Context.IsCurrentCommitTagged && !someBranchRelatedPropertiesMightBeNotKnown && effectiveConfiguration.PreventIncrementWhenCurrentCommitTagged)
        {
            var allTaggedSemanticVersions = taggedSemanticVersionRepository.GetAllTaggedSemanticVersions(
                Context.Configuration, effectiveConfiguration, Context.CurrentBranch, null, Context.CurrentCommit.When
            );
            var taggedSemanticVersionsOfCurrentCommit = allTaggedSemanticVersions[Context.CurrentCommit].ToList();

            SemanticVersion? value;
            if (TryGetSemanticVersion(effectiveConfiguration, taggedSemanticVersionsOfCurrentCommit, out value))
            {
                return value;
            }
        }

        NextVersion nextVersion = CalculateNextVersion(Context.CurrentBranch, Context.Configuration);

        if (Context.IsCurrentCommitTagged && someBranchRelatedPropertiesMightBeNotKnown
            && nextVersion.Configuration.PreventIncrementWhenCurrentCommitTagged)
        {
            var allTaggedSemanticVersions = taggedSemanticVersionRepository.GetAllTaggedSemanticVersions(
                Context.Configuration, nextVersion.Configuration, Context.CurrentBranch, null, Context.CurrentCommit.When
            );
            var taggedSemanticVersionsOfCurrentCommit = allTaggedSemanticVersions[Context.CurrentCommit].ToList();

            SemanticVersion? value;
            if (TryGetSemanticVersion(nextVersion.Configuration, taggedSemanticVersionsOfCurrentCommit, out value))
            {
                return value;
            }
        }

        var semanticVersion = CalculateSemanticVersion(
            deploymentMode: nextVersion.Configuration.DeploymentMode,
            semanticVersion: nextVersion.IncrementedVersion,
            baseVersionSource: nextVersion.BaseVersion.BaseVersionSource
        );

        var ignore = Context.Configuration.Ignore;
        var alternativeSemanticVersion = taggedSemanticVersionRepository.GetTaggedSemanticVersionsOfBranch(
            branch: nextVersion.BranchConfiguration.Branch,
            tagPrefix: Context.Configuration.TagPrefix,
            format: Context.Configuration.SemanticVersionFormat,
            ignore: Context.Configuration.Ignore
        ).Where(element => element.Key.When <= Context.CurrentCommit.When
            && !(element.Key.When <= ignore.Before) && !ignore.Shas.Contains(element.Key.Sha)
        ).SelectMany(element => element).Max()?.Value;

        if (alternativeSemanticVersion is not null
            && semanticVersion.IsLessThan(alternativeSemanticVersion, includePreRelease: false))
        {
            semanticVersion = new SemanticVersion(semanticVersion)
            {
                Major = alternativeSemanticVersion.Major,
                Minor = alternativeSemanticVersion.Minor,
                Patch = alternativeSemanticVersion.Patch
            };
        }

        return semanticVersion;
    }

    private bool TryGetSemanticVersion(
        EffectiveConfiguration effectiveConfiguration,
        IReadOnlyCollection<SemanticVersionWithTag> taggedSemanticVersionsOfCurrentCommit,
        [NotNullWhen(true)] out SemanticVersion? result)
    {
        result = null;

        string? label = effectiveConfiguration.GetBranchSpecificLabel(Context.CurrentBranch.Name, null);
        SemanticVersionWithTag? currentCommitTaggedVersion = taggedSemanticVersionsOfCurrentCommit
            .Where(element => element.Value.IsMatchForBranchSpecificLabel(label)).Max();

        if (currentCommitTaggedVersion is not null)
        {
            SemanticVersionBuildMetaData semanticVersionBuildMetaData = new(
                versionSourceSha: Context.CurrentCommit.Sha,
                commitsSinceTag: null,
                branch: Context.CurrentBranch.Name.Friendly,
                commitSha: Context.CurrentCommit.Sha,
                commitShortSha: Context.CurrentCommit.Id.ToString(7),
                commitDate: Context.CurrentCommit?.When,
                numberOfUnCommittedChanges: Context.NumberOfUncommittedChanges
            );

            SemanticVersionPreReleaseTag preReleaseTag = currentCommitTaggedVersion.Value.PreReleaseTag;
            if (effectiveConfiguration.DeploymentMode == DeploymentMode.ContinuousDeployment)
            {
                preReleaseTag = SemanticVersionPreReleaseTag.Empty;
            }

            result = new SemanticVersion(currentCommitTaggedVersion.Value)
            {
                PreReleaseTag = preReleaseTag,
                BuildMetaData = semanticVersionBuildMetaData
            };
        }

        return result is not null;
    }

    private SemanticVersion CalculateSemanticVersion(
        DeploymentMode deploymentMode, SemanticVersion semanticVersion, ICommit? baseVersionSource)
    {
        IDeploymentModeCalculator deploymentModeCalculator = deploymentMode switch
        {
            DeploymentMode.ManualDeployment => deploymentModeCalculators.SingleOfType<ManualDeploymentVersionCalculator>(),
            DeploymentMode.ContinuousDelivery => deploymentModeCalculators.SingleOfType<ContinuousDeliveryVersionCalculator>(),
            DeploymentMode.ContinuousDeployment => deploymentModeCalculators.SingleOfType<ContinuousDeploymentVersionCalculator>(),
            _ => throw new InvalidEnumArgumentException(nameof(deploymentMode), (int)deploymentMode, typeof(DeploymentMode))
        };
        return deploymentModeCalculator.Calculate(semanticVersion, baseVersionSource);
    }

    private NextVersion CalculateNextVersion(IBranch branch, IGitVersionConfiguration configuration)
    {
        var nextVersions = GetNextVersions(branch, configuration).ToArray();
        log.Separator();
        var maxVersion = nextVersions.Max()!;

        var matchingVersionsOnceIncremented = nextVersions
            .Where(v => v.BaseVersion.BaseVersionSource != null && v.IncrementedVersion == maxVersion.IncrementedVersion)
            .ToList();
        ICommit? latestBaseVersionSource;

        if (matchingVersionsOnceIncremented.Count != 0)
        {
            var latestVersion = matchingVersionsOnceIncremented.Aggregate(CompareVersions);
            latestBaseVersionSource = latestVersion.BaseVersion.BaseVersionSource;
            maxVersion = latestVersion;
            log.Info(
                $"Found multiple base versions which will produce the same SemVer ({maxVersion.IncrementedVersion}), " +
                $"taking oldest source for commit counting ({latestVersion.BaseVersion.Source})");
        }
        else
        {
            IEnumerable<NextVersion> filteredVersions = nextVersions;
            if (!maxVersion.IncrementedVersion.PreReleaseTag.HasTag())
            {
                // If the maximal version has no pre-release tag defined than we want to determine just the latest previous
                // base source which are not coming from pre-release tag.
                filteredVersions = filteredVersions.Where(v => !v.BaseVersion.GetSemanticVersion().PreReleaseTag.HasTag());
            }

            var versions = filteredVersions as NextVersion[] ?? filteredVersions.ToArray();
            var version = versions
                .Where(v => v.BaseVersion.BaseVersionSource != null)
                .OrderByDescending(v => v.IncrementedVersion)
                .ThenByDescending(v => v.BaseVersion.BaseVersionSource?.When)
                .FirstOrDefault();

            version ??= versions.Where(v => v.BaseVersion.BaseVersionSource == null)
                .OrderByDescending(v => v.IncrementedVersion)
                .First();
            latestBaseVersionSource = version.BaseVersion.BaseVersionSource;
        }

        var calculatedBase = new BaseVersion(
            maxVersion.BaseVersion.Source,
            maxVersion.BaseVersion.ShouldIncrement,
            maxVersion.BaseVersion.GetSemanticVersion(),
            latestBaseVersionSource,
            maxVersion.BaseVersion.BranchNameOverride
        );

        log.Info($"Base version used: {calculatedBase}");
        log.Separator();

        return new(maxVersion.IncrementedVersion, calculatedBase, maxVersion.BranchConfiguration);
    }

    private static NextVersion CompareVersions(NextVersion versions1, NextVersion version2)
    {
        if (versions1.BaseVersion.BaseVersionSource == null)
            return version2;

        if (version2.BaseVersion.BaseVersionSource == null)
            return versions1;

        return versions1.BaseVersion.BaseVersionSource.When < version2.BaseVersion.BaseVersionSource.When
            ? versions1
            : version2;
    }

    private IReadOnlyCollection<NextVersion> GetNextVersions(IBranch branch, IGitVersionConfiguration configuration)
    {
        using (log.IndentLog("Fetching the base versions for version calculation..."))
        {
            if (branch.Tip == null)
                throw new GitVersionException("No commits found on the current branch.");

            var nextVersions = GetNextVersionsInternal().ToList();
            if (nextVersions.Count == 0)
                throw new GitVersionException("No base versions determined on the current branch.");
            return nextVersions;
        }

        IEnumerable<NextVersion> GetNextVersionsInternal()
        {
            var effectiveBranchConfigurations = this.effectiveBranchConfigurationFinder.GetConfigurations(branch, configuration).ToArray();
            foreach (var effectiveBranchConfiguration in effectiveBranchConfigurations)
            {
                this.log.Info($"Calculating base versions for '{effectiveBranchConfiguration.Branch.Name}'");
                var atLeastOneBaseVersionReturned = false;
                foreach (var versionStrategy in this.versionStrategies)
                {
                    using (this.log.IndentLog($"[Using '{versionStrategy.GetType().Name}' strategy]"))
                    {
                        foreach (var baseVersion in versionStrategy.GetBaseVersions(effectiveBranchConfiguration))
                        {
                            log.Info(baseVersion.ToString());
                            if (IncludeVersion(baseVersion, configuration.Ignore)
                                && TryGetNextVersion(out var nextVersion, effectiveBranchConfiguration, baseVersion))
                            {
                                yield return nextVersion;
                                atLeastOneBaseVersionReturned = true;
                            }
                        }
                    }
                }

                if (!atLeastOneBaseVersionReturned)
                {
                    var baseVersion = new BaseVersion("Fallback base version", true, SemanticVersion.Empty, null, null);
                    if (TryGetNextVersion(out var nextVersion, effectiveBranchConfiguration, baseVersion))
                        yield return nextVersion;
                }
            }
        }
    }

    private bool TryGetNextVersion([NotNullWhen(true)] out NextVersion? result,
                                   EffectiveBranchConfiguration effectiveConfiguration, BaseVersion baseVersion)
    {
        result = null;

        var label = effectiveConfiguration.Value.GetBranchSpecificLabel(
            Context.CurrentBranch.Name, baseVersion.BranchNameOverride
        );
        if (effectiveConfiguration.Value.Label != label)
        {
            log.Info("Using current branch name to calculate version tag");
        }

        var incrementedVersion = GetIncrementedVersion(effectiveConfiguration, baseVersion, label);
        if (incrementedVersion.IsMatchForBranchSpecificLabel(label))
        {
            result = new(incrementedVersion, baseVersion, effectiveConfiguration);
        }

        return result is not null;
    }

    private SemanticVersion GetIncrementedVersion(EffectiveBranchConfiguration configuration, BaseVersion baseVersion, string? label)
    {
        if (baseVersion is BaseVersionV2 baseVersionV2)
        {
            SemanticVersion result;
            if (baseVersion.ShouldIncrement)
            {
                result = baseVersionV2.GetSemanticVersion().Increment(
                   baseVersionV2.Increment, baseVersionV2.Label, baseVersionV2.ForceIncrement
               );
            }
            else
            {
                result = baseVersion.GetSemanticVersion();
            }

            if (baseVersionV2.AlternativeSemanticVersion is not null
                && result.IsLessThan(baseVersionV2.AlternativeSemanticVersion, includePreRelease: false))
            {
                return new SemanticVersion(result)
                {
                    Major = baseVersionV2.AlternativeSemanticVersion.Major,
                    Minor = baseVersionV2.AlternativeSemanticVersion.Minor,
                    Patch = baseVersionV2.AlternativeSemanticVersion.Patch
                };
            }

            return result;
        }
        else
        {
            var incrementStrategy = incrementStrategyFinder.DetermineIncrementedField(
                currentCommit: Context.CurrentCommit,
                baseVersion: baseVersion,
                configuration: configuration.Value,
                label: label
            );
            return baseVersion.GetSemanticVersion().Increment(incrementStrategy, label);
        }
    }

    private bool IncludeVersion(BaseVersion baseVersion, IIgnoreConfiguration ignoreConfiguration)
    {
        foreach (var versionFilter in ignoreConfiguration.ToFilters())
        {
            if (versionFilter.Exclude(baseVersion, out var reason))
            {
                if (reason != null)
                {
                    log.Info(reason);
                }

                return false;
            }
        }

        return true;
    }
}
