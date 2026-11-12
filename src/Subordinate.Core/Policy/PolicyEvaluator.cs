using Subordinate.Core.Classification;

namespace Subordinate.Core.Policy;

public enum ApprovalScope
{
    Once,
    Session
}

/// <summary>
///     A grant created when a pending item is approved for the rest of the session.
/// </summary>
public sealed class SessionGrant
{
    public required string PendingId { get; init; }

    public required string Tool { get; init; }

    public required string Key { get; init; }

    public required IReadOnlySet<Capability> Capabilities { get; init; }
}

public sealed class PolicyDecision
{
    public required bool IsAllowed { get; init; }

    public required IReadOnlySet<Capability> Missing { get; init; }

    public required bool NeedsHuman { get; init; }

    public required ApprovalScope ProposedScope { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }

    /// <summary>
    ///     The units that were not covered, which a session-scoped approval would grant.
    /// </summary>
    public required IReadOnlyList<ActionUnit> UncoveredUnits { get; init; }
}

/// <summary>
///     Evaluates classification facts against the fixed policy and the session grants. This is code over facts, never a model call.
/// </summary>
public static class PolicyEvaluator
{
    private static readonly Capability[] CommandGrantCapabilities = [Capability.Read, Capability.Write, Capability.Exec, Capability.Network, Capability.Unclassified];

    public static PolicyDecision Evaluate(ActionClassification classification, PolicySet policy, IReadOnlyList<SessionGrant> grants)
    {
        var missing = new HashSet<Capability>();
        var reasons = new List<string>();
        var uncovered = new List<ActionUnit>();

        foreach (var capability in classification.SharedCapabilities)
        {
            if (!policy.Capabilities.Contains(capability))
            {
                missing.Add(capability);
            }
        }

        if (missing.Count > 0)
        {
            reasons.AddRange(classification.SharedReasons);
        }

        if (classification.Units.Count == 0 && missing.Count == 0)
        {
            missing.Add(Capability.Unbounded);
            reasons.Add("the call has nothing that can be authorized");
        }

        foreach (var unit in classification.Units)
        {
            var granted = new HashSet<Capability>(policy.Capabilities);

            if (unit.Key != null)
            {
                if (classification.Tool == ShellClassifier.ToolName && policy.CommandGrants.Contains(unit.Key))
                {
                    granted.UnionWith(CommandGrantCapabilities);
                }

                foreach (var grant in grants)
                {
                    if (grant.Tool == classification.Tool && string.Equals(grant.Key, unit.Key, StringComparison.Ordinal))
                    {
                        granted.UnionWith(grant.Capabilities.Where(capability => !capability.IsOnceOnly()));
                    }
                }
            }

            var unitMissing = unit.Capabilities.Where(capability => !granted.Contains(capability) || capability.IsOnceOnly()).ToArray();

            if (unitMissing.Length == 0)
            {
                continue;
            }

            missing.UnionWith(unitMissing);
            uncovered.Add(unit);

            foreach (var reason in unit.Reasons)
            {
                if (!reasons.Contains(reason))
                {
                    reasons.Add(reason);
                }
            }
        }

        if (missing.Count == 0)
        {
            return new PolicyDecision
            {
                IsAllowed = true,
                Missing = missing,
                NeedsHuman = false,
                ProposedScope = ApprovalScope.Once,
                Reasons = [],
                UncoveredUnits = []
            };
        }

        foreach (var capability in missing)
        {
            var reason = $"the policy does not grant {capability.ToToken()}";
            if (capability.IsPolicyGrantable() && !reasons.Contains(reason))
            {
                reasons.Add(reason);
            }
        }

        var allCapabilities = classification.AllCapabilities;
        var onceOnly = allCapabilities.Any(capability => capability.IsOnceOnly())
            || uncovered.Any(unit => unit.Key == null)
            || missing.Contains(Capability.ReadOutside)
            || missing.Contains(Capability.WriteOutside);

        return new PolicyDecision
        {
            IsAllowed = false,
            Missing = missing,
            NeedsHuman = missing.Any(capability => capability.RequiresHuman()),
            ProposedScope = onceOnly ? ApprovalScope.Once : ApprovalScope.Session,
            Reasons = reasons,
            UncoveredUnits = uncovered
        };
    }
}
