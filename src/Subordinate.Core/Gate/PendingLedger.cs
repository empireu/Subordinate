using System.Security.Cryptography;
using System.Text;
using Subordinate.Core.Classification;
using Subordinate.Core.Policy;
using Subordinate.Core.Sessions;
using Subordinate.Core.Tools;

namespace Subordinate.Core.Gate;

/// <summary>
///     The pending approvals, decisions and session grants of a session, stored inside its record.
/// </summary>
public sealed class PendingLedger(SessionRecord record)
{
    public SessionRecord Record { get; } = record;

    public PolicySet Policy { get; } = PolicySet.FromTokens(record.Policy);

    /// <summary>
    ///     Derives a short, stable approval id from a tool call id. The same call id always produces the same id.
    /// </summary>
    public static string DeriveId(string callId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("subordinate-pending:" + callId));
        return Convert.ToHexStringLower(hash)[..10];
    }

    public string AllocateId(string callId)
    {
        var id = DeriveId(callId);
        var attempt = 2;

        while (Record.Pending.Any(item => item.Id == id))
        {
            id = DeriveId(callId + "#" + attempt);
            attempt++;
        }

        return id;
    }

    public IReadOnlyList<SessionGrant> Grants => Record.Grants.Select(grant => new SessionGrant
    {
        PendingId = grant.PendingId,
        Tool = grant.Tool,
        Key = grant.Key,
        Capabilities = grant.Capabilities.Select(token => CapabilityNames.TryParse(token, out var capability) ? capability : Capability.Unbounded).ToHashSet()
    }).ToArray();

    public PendingRecord? FindById(string id)
    {
        return Record.Pending.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public PendingRecord? FindByCallId(string callId)
    {
        return Record.Pending.LastOrDefault(item => item.CallId == callId);
    }

    public PendingRecord? FindDenied(string fingerprint)
    {
        return Record.Pending.FirstOrDefault(item => item.Status == PendingStatuses.Denied && item.Fingerprint == fingerprint);
    }

    public PendingRecord? FindOpen(string fingerprint)
    {
        return Record.Pending.FirstOrDefault(item => item.Status == PendingStatuses.Pending && item.Fingerprint == fingerprint);
    }

    public PendingRecord Add(string callId, string tool, string runId, string argumentsJson, string fingerprint, CallDescription description, ActionClassification classification, PolicyDecision decision, string shell)
    {
        var id = AllocateId(callId);
        var reasons = decision.Reasons.Count > 0 ? decision.Reasons : classification.AllReasons;

        var item = new PendingRecord
        {
            Id = id,
            CallId = callId,
            Tool = tool,
            RunId = runId,
            CreatedAt = DateTimeOffset.UtcNow,
            Summary = description.Summary,
            Command = description.Command,
            Shell = tool == ShellClassifier.ToolName ? shell : null,
            WorkingDirectory = description.WorkingDirectory,
            Path = description.Path,
            ArgumentsJson = argumentsJson,
            Fingerprint = fingerprint,
            Capabilities = classification.AllCapabilities.ToTokens().ToList(),
            Missing = decision.Missing.ToTokens().ToList(),
            Reasons = reasons.ToList(),
            ProposedScope = decision.ProposedScope == ApprovalScope.Session ? "session" : "once",
            NeedsHuman = decision.NeedsHuman,
            GrantKeys = decision.UncoveredUnits
                .Where(unit => unit.Key != null)
                .Select(unit => new GrantKeyRecord
                {
                    Key = unit.Key!,
                    Capabilities = unit.Capabilities.Where(capability => !capability.IsOnceOnly()).ToTokens().ToList()
                })
                .ToList()
        };

        item.DeferralMessage = BuildDeferralMessage(item);
        Record.Pending.Add(item);
        return item;
    }

    public void Approve(PendingRecord item, ApprovalScope scope, string? reason)
    {
        if (scope == ApprovalScope.Session && item.ProposedScope != "session")
        {
            throw new SubordinateException(ErrorCode.BadApproval, $"Pending item {item.Id} can only be approved once, not for the session: {string.Join("; ", item.Reasons)}.");
        }

        item.Status = PendingStatuses.Approved;
        item.Decision = new DecisionRecord
        {
            DecidedAt = DateTimeOffset.UtcNow,
            Scope = scope == ApprovalScope.Session ? "session" : "once",
            Reason = reason
        };

        if (scope != ApprovalScope.Session)
        {
            return;
        }

        foreach (var grantKey in item.GrantKeys)
        {
            Record.Grants.Add(new GrantRecord
            {
                PendingId = item.Id,
                Tool = item.Tool,
                Key = grantKey.Key,
                Capabilities = grantKey.Capabilities
            });
        }
    }

    public void Deny(PendingRecord item, string? reason)
    {
        item.Status = PendingStatuses.Denied;
        item.Decision = new DecisionRecord
        {
            DecidedAt = DateTimeOffset.UtcNow,
            Scope = "once",
            Reason = reason
        };
    }

    public static string BuildDeferralMessage(PendingRecord item)
    {
        return $"PENDING APPROVAL {item.Id}: this call was not executed. Why: {string.Join("; ", item.Reasons)}. "
            + "The supervisor will decide later. Do not repeat this call. Continue with other parts of the task if you can. "
            + "If you cannot make progress without it, end your turn and state what you need and why. "
            + "If it is approved, it will be run for you and you will receive its output in a later message.";
    }

    public static string BuildDenialMessage(PendingRecord item)
    {
        var reason = string.IsNullOrWhiteSpace(item.Decision?.Reason) ? string.Empty : $" Reason: {item.Decision!.Reason}.";
        return $"DENIED {item.Id}: the supervisor denied this exact call.{reason} Do not retry it. Find another way, or explain in your final answer what you need.";
    }

    public static string BuildDuplicateMessage(PendingRecord item)
    {
        return $"NOT EXECUTED: this call is identical to {item.Id}, which is still waiting for approval. Do not repeat it. Work on something else or end your turn.";
    }
}
