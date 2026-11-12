using System.Text.RegularExpressions;

namespace Subordinate.Core.Classification;

/// <summary>
///     Detects references to credential stores. This is independent of classification, so a read-only command that touches a key file is still flagged.
/// </summary>
public static class SensitivePaths
{
    private const string Boundary = @"(?:^|[\s/'""=:<>|;&(),`@])";
    private const string End = @"(?=$|[\s/'""<>|;&),`])";

    private static readonly (string Description, Regex Pattern)[] Patterns =
    [
        ("SSH directory", Create(Boundary + @"\.ssh" + End)),
        ("SSH private key", Create(Boundary + @"id_(?:rsa|dsa|ecdsa|ed25519)(?:_sk)?" + End)),
        ("GPG keyring", Create(Boundary + @"\.gnupg" + End)),
        ("AWS credentials", Create(@"\.aws/(?:credentials|sso|cli/cache)")),
        ("Azure credentials", Create(Boundary + @"\.azure" + End)),
        ("Google Cloud credentials", Create(@"\.config/gcloud|application_default_credentials\.json")),
        ("Kubernetes config", Create(@"\.kube/config|" + Boundary + "kubeconfig" + End)),
        ("Docker credentials", Create(@"\.docker/config\.json")),
        ("environment file", Create(Boundary + @"\.env(?:\.(?!example|sample|template|dist|defaults?)[\w-]+)*" + End)),
        ("direnv file", Create(Boundary + @"\.envrc" + End)),
        ("netrc", Create(Boundary + @"[._]netrc" + End)),
        ("npm credentials", Create(Boundary + @"\.npmrc" + End)),
        ("yarn credentials", Create(Boundary + @"\.yarnrc\.yml" + End)),
        ("PyPI credentials", Create(Boundary + @"\.pypirc" + End)),
        ("RubyGems credentials", Create(@"\.gem/credentials")),
        ("Cargo credentials", Create(@"\.cargo/credentials")),
        ("Composer credentials", Create(@"\.composer/auth\.json")),
        ("git credentials", Create(Boundary + @"\.git-credentials" + End + @"|\.config/git/credentials")),
        ("PostgreSQL password file", Create(Boundary + @"\.pgpass" + End)),
        ("MySQL credentials", Create(Boundary + @"\.my\.cnf" + End)),
        ("Claude token store", Create(Boundary + @"\.claude\.json" + End + @"|\.claude/\.credentials\.json")),
        ("GitHub CLI token store", Create(@"(?:^|/)gh/hosts\.yml")),
        ("GitLab CLI token store", Create(@"glab-cli/config\.yml")),
        ("Subordinate configuration", Create(@"\.subordinate/config\.json")),
        ("private key or certificate store", Create(@"\.(?:pem|p12|pfx|jks|keystore|ppk|key)" + End)),
        ("Vault or Terraform token", Create(Boundary + @"\.vault-token" + End + @"|" + Boundary + @"\.terraformrc" + End + @"|credentials\.tfrc\.json")),
        (".NET user secrets", Create(@"microsoft/usersecrets")),
        ("Windows credential store", Create(@"appdata/(?:roaming|local)/microsoft/(?:credentials|protect)")),
        ("PowerShell environment provider", Create(@"(?:^|[\s'""=(,])env:"))
    ];

    private static Regex Create(string pattern)
    {
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    /// <summary>
    ///     Returns a description for every credential store referenced in the text.
    /// </summary>
    public static IReadOnlyList<string> FindMatches(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var normalized = text.Replace('\\', '/');
        var matches = new List<string>();

        foreach (var (description, pattern) in Patterns)
        {
            if (pattern.IsMatch(normalized))
            {
                matches.Add(description);
            }
        }

        return matches;
    }
}
