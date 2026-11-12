using System.Text;

namespace Subordinate.Core.Policy;

/// <summary>
///     The permission policy of a session. It is fixed when the session starts and never widened afterwards.
/// </summary>
public sealed class PolicySet
{
    public const string CommandGrantPrefix = "cmd:";

    private static readonly Dictionary<string, Capability[]> Presets = new(StringComparer.Ordinal)
    {
        ["readonly"] = [Capability.Read],
        ["build"] = [Capability.Read, Capability.Exec],
        ["edit"] = [Capability.Read, Capability.Write, Capability.Exec]
    };

    private PolicySet(IReadOnlySet<Capability> capabilities, IReadOnlyList<string> commandGrants)
    {
        Capabilities = capabilities;
        CommandGrants = commandGrants;
    }

    /// <summary>
    ///     The capabilities granted for every call.
    /// </summary>
    public IReadOnlySet<Capability> Capabilities { get; }

    /// <summary>
    ///     Command identities, as normalized command words, that are allowed whatever their command category.
    ///     Path scope, sensitive paths and unbounded facts still apply.
    /// </summary>
    public IReadOnlyList<string> CommandGrants { get; }

    /// <summary>
    ///     The canonical token list, as persisted in the session.
    /// </summary>
    public IReadOnlyList<string> Tokens => Capabilities.ToTokens().Concat(CommandGrants.Select(grant => CommandGrantPrefix + grant)).ToArray();

    public static IReadOnlyList<string> PresetNames => Presets.Keys.ToArray();

    /// <summary>
    ///     Parses policy values. Each value holds comma separated tokens, so a command grant may contain spaces.
    /// </summary>
    public static bool TryParse(IEnumerable<string> values, out PolicySet? policy, out string error)
    {
        var capabilities = new HashSet<Capability>();
        var commandGrants = new SortedSet<string>(StringComparer.Ordinal);
        var sawToken = false;

        foreach (var value in values)
        {
            foreach (var rawToken in value.Split(','))
            {
                var token = rawToken.Trim();

                if (token.Length == 0)
                {
                    continue;
                }

                sawToken = true;

                if (token.StartsWith(CommandGrantPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var words = NormalizeCommandWords(token[CommandGrantPrefix.Length..]);

                    if (words.Length == 0)
                    {
                        policy = null;
                        error = $"The policy token \"{token}\" names no command. Use cmd:<command words>, for example cmd:npm run lint.";
                        return false;
                    }

                    commandGrants.Add(words);
                    continue;
                }

                var lowered = token.ToLowerInvariant();

                if (Presets.TryGetValue(lowered, out var presetCapabilities))
                {
                    capabilities.UnionWith(presetCapabilities);
                    continue;
                }

                if (CapabilityNames.TryParse(lowered, out var capability) && capability.IsPolicyGrantable())
                {
                    capabilities.Add(capability);
                    continue;
                }

                policy = null;
                error = $"Unknown policy token \"{token}\". Valid tokens are the presets {string.Join(", ", Presets.Keys)}, the capabilities read, read-outside, write, write-outside, exec, network, and cmd:<command words>.";
                return false;
            }
        }

        if (!sawToken)
        {
            policy = null;
            error = "The policy is empty. Pass at least one token, for example --policy readonly.";
            return false;
        }

        policy = new PolicySet(capabilities, commandGrants.ToArray());
        error = string.Empty;
        return true;
    }

    /// <summary>
    ///     Rebuilds a policy from its persisted canonical tokens.
    /// </summary>
    public static PolicySet FromTokens(IReadOnlyList<string> tokens)
    {
        if (!TryParse([string.Join(",", tokens)], out var policy, out var error))
        {
            throw new InvalidDataException($"The stored policy is invalid: {error}");
        }

        return policy!;
    }

    public static string NormalizeCommandWords(string words)
    {
        var builder = new StringBuilder();
        foreach (var part in words.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(part.ToLowerInvariant());
        }

        return builder.ToString();
    }

    public string Describe()
    {
        var parts = new List<string> { string.Join(", ", Capabilities.ToTokens()) };

        if (CommandGrants.Count > 0)
        {
            parts.Add("commands: " + string.Join("; ", CommandGrants));
        }

        return string.Join(" | ", parts);
    }
}
