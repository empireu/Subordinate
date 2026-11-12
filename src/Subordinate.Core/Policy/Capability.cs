namespace Subordinate.Core.Policy;

/// <summary>
///     A fact about what a tool call does. Classification produces a set of these, and the policy grants a subset.
/// </summary>
public enum Capability
{
    Read,
    ReadOutside,
    Write,
    WriteOutside,
    Exec,
    Network,
    Unclassified,
    Unbounded,
    Sensitive,
    Dangerous
}

public static class CapabilityNames
{
    private static readonly Dictionary<Capability, string> Tokens = new()
    {
        [Capability.Read] = "read",
        [Capability.ReadOutside] = "read-outside",
        [Capability.Write] = "write",
        [Capability.WriteOutside] = "write-outside",
        [Capability.Exec] = "exec",
        [Capability.Network] = "network",
        [Capability.Unclassified] = "unclassified",
        [Capability.Unbounded] = "unbounded",
        [Capability.Sensitive] = "sensitive",
        [Capability.Dangerous] = "dangerous"
    };

    private static readonly Dictionary<string, Capability> ByToken = Tokens.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    public static string ToToken(this Capability capability)
    {
        return Tokens[capability];
    }

    public static bool TryParse(string token, out Capability capability)
    {
        return ByToken.TryGetValue(token, out capability);
    }

    /// <summary>
    ///     True for capabilities that a policy token can grant. The others are never granted automatically.
    /// </summary>
    public static bool IsPolicyGrantable(this Capability capability)
    {
        return capability is Capability.Read
            or Capability.ReadOutside
            or Capability.Write
            or Capability.WriteOutside
            or Capability.Exec
            or Capability.Network;
    }

    /// <summary>
    ///     True for capabilities that only a human should approve, never the driving agent on its own.
    /// </summary>
    public static bool RequiresHuman(this Capability capability)
    {
        return capability is Capability.WriteOutside
            or Capability.Network
            or Capability.Sensitive
            or Capability.Dangerous;
    }

    /// <summary>
    ///     True for capabilities that make a call unsuitable for a session-wide grant.
    /// </summary>
    public static bool IsOnceOnly(this Capability capability)
    {
        return capability is Capability.Unbounded
            or Capability.Sensitive
            or Capability.Dangerous;
    }

    public static IEnumerable<string> ToTokens(this IEnumerable<Capability> capabilities)
    {
        return capabilities.OrderBy(capability => (int)capability).Select(ToToken);
    }
}
