using ShellSyntaxTree;
using Subordinate.Core.Policy;

using Subordinate.Core.Platform;

namespace Subordinate.Core.Classification;

/// <summary>
///     Classifies shell commands from parser facts and the command database. Every uncertainty fails closed to a capability that needs approval.
/// </summary>
public static class ShellClassifier
{
    public const string ToolName = "shell";

    private static readonly HashSet<string> SafeLocaleNames = new(StringComparer.Ordinal)
    {
        "LC_ALL", "LANG", "LANGUAGE", "LC_CTYPE", "LC_COLLATE", "LC_MESSAGES", "LC_NUMERIC", "LC_TIME", "TZ"
    };

    public static ActionClassification Classify(string source, ShellKind shell, ClassificationScope scope)
    {
        return ClassifyInternal(source, shell, scope, 0);
    }

    private static ActionClassification ClassifyInternal(string source, ShellKind shell, ClassificationScope scope, int depth)
    {
        var sharedCapabilities = new HashSet<Capability> { Capability.Read };
        var sharedReasons = new List<string>();
        var units = new List<ActionUnit>();

        var effectiveSource = shell == ShellKind.Bash ? StripSafeLocaleAssignments(source) : source;
        var parsed = Parse(effectiveSource, shell, scope, out var parseError);

        if (parsed == null)
        {
            sharedCapabilities.Add(Capability.Unbounded);
            sharedReasons.Add(parseError);
        }
        else if (parsed.IsUnparseable)
        {
            sharedCapabilities.Add(Capability.Unbounded);
            sharedReasons.Add($"the command cannot be analyzed statically: {parsed.UnparseableReason ?? "unsupported syntax"}");
        }
        else if (parsed.Commands.Count == 0)
        {
            sharedCapabilities.Add(Capability.Unbounded);
            sharedReasons.Add("the command produced no analyzable command occurrences");
        }
        else
        {
            var accountedRegionArguments = FindAccountedRegionArguments(parsed);
            string? workingDirectory = scope.WorkingDirectory;

            foreach (var occurrence in parsed.Commands)
            {
                var fallback = occurrence.Clause.Operator == CompoundOperator.AndIf ? workingDirectory : null;
                units.Add(ClassifyOccurrence(occurrence, shell, scope, accountedRegionArguments, depth, fallback));
                workingDirectory = UpdateWorkingDirectory(occurrence, workingDirectory, scope);
            }
        }

        return new ActionClassification
        {
            Tool = ToolName,
            Summary = source,
            Units = units,
            SharedCapabilities = sharedCapabilities,
            SharedReasons = sharedReasons
        };
    }

    private static ParsedCommand? Parse(string source, ShellKind shell, ClassificationScope scope, out string error)
    {
        error = string.Empty;

        try
        {
            switch (shell)
            {
                case ShellKind.Bash:
                {
                    if (!PlatformServices.Current.Paths.SupportsShellWorkingDirectory(ShellKind.Bash, scope.WorkingDirectory))
                    {
                        error = "the working directory cannot be expressed as a Git Bash path";
                        return null;
                    }

                    var parser = new BashParser(new BashParserOptions
                    {
                        WorkingDirectory = scope.WorkingDirectory,
                        InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup
                    });

                    return parser.Parse(source);
                }
                case ShellKind.WindowsPowerShell:
                case ShellKind.PowerShell7:
                {
                    var parser = new PwshParser(new PwshParserOptions
                    {
                        WorkingDirectory = scope.WorkingDirectory,
                        InitialStateMode = PwshInitialStateMode.IsolatedNonInteractiveNoProfile,
                        Dialect = shell == ShellKind.PowerShell7 ? PwshDialect.PowerShell7 : PwshDialect.WindowsPowerShell51
                    });

                    return parser.Parse(source);
                }
                default:
                {
                    error = "the shell is not supported by the classifier";
                    return null;
                }
            }
        }
        catch (Exception ex)
        {
            error = $"the command parser failed: {ex.GetType().Name}";
            return null;
        }
    }

    private static ActionUnit ClassifyOccurrence(CommandOccurrence occurrence, ShellKind shell, ClassificationScope scope, IReadOnlyList<ClauseElement> accountedRegionArguments, int depth, string? fallbackWorkingDirectory)
    {
        var capabilities = new HashSet<Capability> { Capability.Read };
        var reasons = new List<string>();
        var paths = new List<PathAccess>();
        var display = BuildDisplay(occurrence);
        var words = occurrence.CommandWords is ShellCommandWords.Known known
            ? known.Words.Select(word => word.ToLowerInvariant()).ToArray()
            : [];
        var suspectWords = words.Skip(1).Any(word => word.StartsWith('-') || word.Contains('='));

        void Unbounded(string reason)
        {
            capabilities.Add(Capability.Unbounded);
            if (!reasons.Contains(reason))
            {
                reasons.Add(reason);
            }
        }

        if (!occurrence.IsComplete)
        {
            Unbounded($"`{display}` is not a complete, statically bounded command");
        }

        if (!IsKnownRole(occurrence.ImmediateRole))
        {
            Unbounded($"`{display}` runs in a context the classifier does not understand");
        }

        foreach (var frame in occurrence.Ancestry)
        {
            if (frame.Region == CommandAncestryRegion.Unknown || !Enum.IsDefined(frame.Region))
            {
                Unbounded($"`{display}` is nested in an unrecognized construct");
            }

            if (frame.Ancestor is GroupSyntax group && group.GroupKind is ShellGroupKind.Background or ShellGroupKind.Unknown)
            {
                Unbounded($"`{display}` runs as a background job that can outlive the tool call");
            }
        }

        if (occurrence.Assignments.Count > 0 && !AreSafeLocaleAssignments(occurrence.Assignments))
        {
            Unbounded($"`{display}` sets variables that can change how programs behave");
        }

        var verb = occurrence.Clause.Verb;
        if (verb.IsDynamic || verb.Tokens.Count == 0)
        {
            Unbounded($"the program run by `{display}` cannot be determined statically");

            return new ActionUnit
            {
                Display = display,
                Key = null,
                Capabilities = capabilities,
                Reasons = reasons,
                Paths = paths
            };
        }

        var baseDirectory = ResolveWorkingDirectory(occurrence.WorkingDirectory, scope) ?? fallbackWorkingDirectory;
        var programWord = verb.CanonicalVerb ?? verb.Tokens[0];
        var program = CommandDatabase.NormalizeProgram(programWord);
        var hasSubcommands = CommandDatabase.TryGet(program, out var baseRule) && baseRule.Subcommands != null;

        if (suspectWords && hasSubcommands)
        {
            Unbounded($"the subcommand of `{display}` cannot be determined because an option comes before it");
            words = [];
        }

        var rule = ResolveRule(programWord, program, words, baseDirectory, scope, out var ruleReason);

        var arguments = new List<ArgumentView>();
        var skipped = new HashSet<int>();

        for (var index = 0; index < occurrence.Arguments.Count; index++)
        {
            var analyzed = occurrence.Arguments[index];

            if (analyzed.Argument.Kind == ArgKind.DynamicSkip && accountedRegionArguments.Any(element => ReferenceEquals(element, analyzed.Element)))
            {
                skipped.Add(index);
            }

            arguments.Add(new ArgumentView
            {
                Index = index,
                Raw = analyzed.Argument.Raw,
                Value = analyzed.Value is ShellValueDomain.Exact exact ? exact.Value : null,
                IsFlag = analyzed.Argument.IsFlag
            });
        }

        var hasAuthoredInput = occurrence.Redirects.Any(redirect => redirect is HereDocumentRedirectAnalysis or HereStringRedirectAnalysis);
        var context = new RuleContext
        {
            Program = program,
            Words = words,
            RawElements = occurrence.Clause.Elements.Select(element => element.Raw).ToList(),
            Arguments = arguments,
            Role = occurrence.ImmediateRole,
            HasAuthoredStandardInput = hasAuthoredInput
        };

        if (rule == null)
        {
            capabilities.Add(Capability.Unclassified);
            reasons.Add(ruleReason ?? $"`{program}` is not in the command database");
        }
        else
        {
            capabilities.UnionWith(rule.Capabilities);

            if (ruleReason != null)
            {
                reasons.Add(ruleReason);
            }

            rule.Refine?.Invoke(context);

            foreach (var (capability, reason) in context.Added)
            {
                capabilities.Add(capability);
                if (!reasons.Contains(reason))
                {
                    reasons.Add(reason);
                }
            }

            foreach (var capability in rule.Capabilities)
            {
                var reason = DescribeCategory(capability, program);
                if (reason != null && !reasons.Contains(reason))
                {
                    reasons.Add(reason);
                }
            }
        }

        if (context.NestedCommands.Count > 0)
        {
            if (depth >= 2)
            {
                Unbounded("the command nests another command too deeply");
            }
            else
            {
                var nestedWrites = false;

                foreach (var nested in context.NestedCommands)
                {
                    var nestedClassification = ClassifyInternal(nested, shell, scope, depth + 1);
                    nestedWrites |= nestedClassification.AllCapabilities.Contains(Capability.Write);
                    capabilities.UnionWith(nestedClassification.SharedCapabilities);

                    foreach (var reason in nestedClassification.SharedReasons)
                    {
                        if (!reasons.Contains(reason))
                        {
                            reasons.Add(reason);
                        }
                    }

                    foreach (var nestedUnit in nestedClassification.Units)
                    {
                        capabilities.UnionWith(nestedUnit.Capabilities);

                        foreach (var reason in nestedUnit.Reasons)
                        {
                            if (!reasons.Contains(reason))
                            {
                                reasons.Add(reason);
                            }
                        }

                        paths.AddRange(nestedUnit.Paths);
                    }
                }

                if (context.NestedOperandsFromInput)
                {
                    var reason = nestedWrites
                        ? $"`{display}` writes files named on standard input, which can be anywhere"
                        : $"`{display}` reads files named on standard input, which can be anywhere";
                    capabilities.Add(nestedWrites ? Capability.WriteOutside : Capability.ReadOutside);

                    if (!reasons.Contains(reason))
                    {
                        reasons.Add(reason);
                    }
                }
            }
        }

        var key = BuildKey(programWord, program, words, hasSubcommands, suspectWords, rule, context, occurrence, baseDirectory, scope);
        var operandAccess = rule?.Operands ?? OperandAccess.Read;
        var writeValueArguments = new HashSet<int>(context.WriteArguments);

        if (rule?.WriteValueFlags != null)
        {
            for (var index = 0; index < arguments.Count - 1; index++)
            {
                if (arguments[index].IsFlag && rule.WriteValueFlags.Contains(arguments[index].Text))
                {
                    writeValueArguments.Add(index + 1);
                }
            }
        }

        for (var index = 0; index < occurrence.Arguments.Count; index++)
        {
            if (skipped.Contains(index))
            {
                continue;
            }

            var analyzed = occurrence.Arguments[index];

            if (analyzed.Argument.IsFlag)
            {
                if (analyzed.Value is not ShellValueDomain.Exact && analyzed.Argument.Kind == ArgKind.DynamicSkip)
                {
                    Unbounded($"an option of `{display}` cannot be determined statically");
                }

                continue;
            }

            var isWrite = writeValueArguments.Contains(index);

            if (!isWrite && (context.DataArguments.Contains(index) || operandAccess == OperandAccess.Data))
            {
                continue;
            }

            if (!isWrite && operandAccess == OperandAccess.Write)
            {
                isWrite = true;
            }

            if (!TryGetPathCandidates(analyzed, baseDirectory, out var candidates))
            {
                Unbounded($"the argument {analyzed.Argument.Raw} of `{display}` cannot be resolved statically");
                paths.Add(new PathAccess(isWrite, new ResolvedPath(ResolvedPathKind.Unknown, null, analyzed.Argument.Raw)));
                continue;
            }

            foreach (var candidate in candidates)
            {
                paths.Add(new PathAccess(isWrite, candidate));
            }

            if (candidates.Count == 0)
            {
                foreach (var match in SensitivePaths.FindMatches(analyzed.Argument.Raw))
                {
                    capabilities.Add(Capability.Sensitive);
                    var reason = $"the command references a credential store ({match})";

                    if (!reasons.Contains(reason))
                    {
                        reasons.Add(reason);
                    }
                }
            }
        }

        foreach (var redirect in occurrence.Redirects)
        {
            ClassifyRedirect(redirect, display, baseDirectory, paths, Unbounded);
        }

        if (rule == null || rule.Operands != OperandAccess.Data)
        {
            if (baseDirectory == null)
            {
                Unbounded($"the working directory of `{display}` cannot be determined statically");
            }
            else
            {
                var writesDirectory = rule != null && rule.WritesWorkingDirectory && !rule.Capabilities.Contains(Capability.Exec);
                paths.Add(new PathAccess(writesDirectory, new ResolvedPath(ResolvedPathKind.Local, baseDirectory, baseDirectory)));
            }
        }

        scope.ApplyPathCapabilities(paths, capabilities, reasons);
        AddSensitivePaths(paths, baseDirectory, capabilities, reasons);

        return new ActionUnit
        {
            Display = display,
            Key = key,
            Capabilities = capabilities,
            Reasons = reasons,
            Paths = paths
        };
    }

    private static string? UpdateWorkingDirectory(CommandOccurrence occurrence, string? current, ClassificationScope scope)
    {
        if (occurrence.WorkingDirectoryEffect is ShellWorkingDirectoryEffect.ChangesOnSuccess changed && changed.Target is ShellValueDomain.Exact exact)
        {
            var resolved = ShellPaths.Resolve(exact.Value, current ?? scope.WorkingDirectory);
            return resolved.Kind == ResolvedPathKind.Local ? resolved.Path : null;
        }

        if (occurrence.WorkingDirectoryEffect is ShellWorkingDirectoryEffect.Unknown)
        {
            return null;
        }

        return current;
    }

    private static string? BuildKey(string programWord, string program, IReadOnlyList<string> words, bool hasSubcommands, bool suspectWords, CommandRule? rule, RuleContext context, CommandOccurrence occurrence, string? baseDirectory, ClassificationScope scope)
    {
        if (rule?.IsInterpreter == true)
        {
            return BuildInterpreterKey(program, occurrence, baseDirectory, scope);
        }

        if (context.NestedCommands.Count > 0)
        {
            return null;
        }

        if (hasSubcommands)
        {
            if (suspectWords || words.Count == 0)
            {
                return null;
            }

            return PolicySet.NormalizeCommandWords(string.Join(" ", words));
        }

        return PolicySet.NormalizeCommandWords(program);
    }

    private static string? BuildInterpreterKey(string program, CommandOccurrence occurrence, string? baseDirectory, ClassificationScope scope)
    {
        if (program is "python" or "python3" or "py")
        {
            for (var index = 0; index < occurrence.Arguments.Count - 1; index++)
            {
                var argument = occurrence.Arguments[index];

                if (argument.Argument.IsFlag && (argument.Argument.Raw == "-m" || argument.Argument.Raw == "--module"))
                {
                    var module = occurrence.Arguments[index + 1];

                    if (module.Value is ShellValueDomain.Exact moduleValue)
                    {
                        return PolicySet.NormalizeCommandWords($"{program} -m {moduleValue.Value}");
                    }

                    return null;
                }
            }
        }

        if (program is "pwsh" or "powershell")
        {
            for (var index = 0; index < occurrence.Arguments.Count - 1; index++)
            {
                var argument = occurrence.Arguments[index];

                if (argument.Argument.IsFlag && argument.Argument.Raw.Equals("-File", StringComparison.OrdinalIgnoreCase))
                {
                    return BuildScriptKey(program, occurrence.Arguments[index + 1], baseDirectory, scope);
                }
            }
        }

        var inlineFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "-c", "-e", "-command", "-encodedcommand", "/c", "-p", "--eval", "--print" };
        var operands = new List<AnalyzedArgument>();
        var sawFlagBeforeOperand = false;

        foreach (var argument in occurrence.Arguments)
        {
            if (argument.Argument.IsFlag)
            {
                if (inlineFlags.Contains(argument.Argument.Raw))
                {
                    return null;
                }

                if (operands.Count == 0)
                {
                    sawFlagBeforeOperand = true;
                }

                continue;
            }

            operands.Add(argument);
        }

        if (operands.Count == 0)
        {
            return PolicySet.NormalizeCommandWords(program);
        }

        if (sawFlagBeforeOperand)
        {
            return null;
        }

        return BuildScriptKey(program, operands[0], baseDirectory, scope);
    }

    private static string? BuildScriptKey(string program, AnalyzedArgument script, string? baseDirectory, ClassificationScope scope)
    {
        if (script.Value is not ShellValueDomain.Exact exact)
        {
            return null;
        }

        var resolved = ShellPaths.Resolve(exact.Value, baseDirectory);

        if (resolved.Kind != ResolvedPathKind.Local)
        {
            return null;
        }

        var path = resolved.Path!;

        if (scope.IsInsideWorkdir(path))
        {
            path = Path.GetRelativePath(scope.Workdir, path);
        }

        path = path.Replace('\\', '/');

        return PolicySet.NormalizeCommandWords($"{program} {path}");
    }

    private static void AddSensitivePaths(IEnumerable<PathAccess> paths, string? workingDirectory, HashSet<Capability> capabilities, List<string> reasons)
    {
        foreach (var access in paths)
        {
            foreach (var match in SensitivePaths.FindMatches(access.Path.Display))
            {
                capabilities.Add(Capability.Sensitive);
                var reason = $"the command references a credential store ({match})";

                if (!reasons.Contains(reason))
                {
                    reasons.Add(reason);
                }
            }
        }

        if (workingDirectory == null)
        {
            return;
        }

        foreach (var match in SensitivePaths.FindMatches(workingDirectory))
        {
            capabilities.Add(Capability.Sensitive);
            var reason = $"the command runs in a credential store ({match})";

            if (!reasons.Contains(reason))
            {
                reasons.Add(reason);
            }
        }
    }

    private static bool AreSafeLocaleAssignments(IReadOnlyList<ShellVariableAssignment> assignments)
    {
        foreach (var assignment in assignments)
        {
            if (!SafeLocaleNames.Contains(assignment.Name) || assignment.EffectiveValue is not ShellValueDomain.Exact)
            {
                return false;
            }
        }

        return true;
    }

    private static string StripSafeLocaleAssignments(string source)
    {
        var index = 0;
        var stripped = false;

        while (true)
        {
            while (index < source.Length && char.IsWhiteSpace(source[index]))
            {
                index++;
            }

            var start = index;

            while (index < source.Length && (char.IsAsciiLetterOrDigit(source[index]) || source[index] == '_'))
            {
                index++;
            }

            if (index == start || index >= source.Length || source[index] != '=')
            {
                return stripped ? source[start..] : source;
            }

            var name = source[start..index];
            index++;

            var valueStart = index;

            while (index < source.Length && !char.IsWhiteSpace(source[index]) && source[index] is not (';' or '|' or '&' or '<' or '>'))
            {
                index++;
            }

            var value = source[valueStart..index];

            if (!SafeLocaleNames.Contains(name) || !IsSimpleAssignmentValue(value))
            {
                return stripped ? source[start..] : source;
            }

            var next = index;

            while (next < source.Length && char.IsWhiteSpace(source[next]))
            {
                next++;
            }

            if (next >= source.Length || source[next] is ';' or '|' or '&')
            {
                return stripped ? source[start..] : source;
            }

            stripped = true;
        }
    }

    private static bool IsSimpleAssignmentValue(string value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1];
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '.' or '-' or '/' or ':' or '+' or ','))
            {
                return false;
            }
        }

        return true;
    }

    private static CommandRule? ResolveRule(string programWord, string program, IReadOnlyList<string> words, string? baseDirectory, ClassificationScope scope, out string? reason)
    {
        reason = null;

        if (!CommandDatabase.TryGet(program, out var rule))
        {
            if (programWord.Contains('/') || programWord.Contains('\\'))
            {
                var location = ShellPaths.Resolve(programWord, baseDirectory);

                if (location.Kind == ResolvedPathKind.Local && scope.IsInsideWorkdir(location.Path!))
                {
                    reason = $"runs {programWord}, a program or script from the workdir";
                    return new CommandRule { Capabilities = [Capability.Exec], Operands = OperandAccess.Read, WritesWorkingDirectory = true };
                }
            }

            return null;
        }

        if (rule.Subcommands == null)
        {
            return rule;
        }

        if (words.Count == 0)
        {
            reason = $"the subcommand of `{program}` cannot be determined statically";
            return new CommandRule { Capabilities = [Capability.Unbounded], Operands = OperandAccess.Read };
        }

        if (words.Count == 1)
        {
            return rule.WithoutSubcommand ?? rule;
        }

        if (rule.Subcommands.TryGetValue(words[1], out var subcommandRule))
        {
            return subcommandRule;
        }

        if (rule.OtherSubcommand != null)
        {
            return rule.OtherSubcommand;
        }

        reason = $"`{program} {words[1]}` is not in the command database";
        return null;
    }

    private static string? DescribeCategory(Capability capability, string program)
    {
        return capability switch
        {
            Capability.Exec => $"{program} executes project code or build tooling",
            Capability.Network => $"{program} uses the network",
            Capability.Write => $"{program} modifies files",
            Capability.Dangerous => $"{program} is classified as dangerous",
            Capability.Sensitive => $"{program} touches secrets",
            _ => null
        };
    }

    private static void ClassifyRedirect(RedirectAnalysis redirect, string display, string? baseDirectory, List<PathAccess> paths, Action<string> unbounded)
    {
        if (!redirect.IsComplete || redirect.Source is not (RedirectSource.Default or RedirectSource.Descriptor or RedirectSource.PowerShellAllStreams))
        {
            unbounded($"a redirect of `{display}` cannot be resolved statically");
            return;
        }

        switch (redirect)
        {
            case FileRedirectAnalysis file:
            {
                var isWrite = file.Mode != FileRedirectMode.Input;

                if (!Enum.IsDefined(file.Mode))
                {
                    unbounded($"a redirect of `{display}` uses an unrecognized mode");
                    return;
                }

                switch (file.Target)
                {
                    case ShellValueDomain.Exact exact:
                    {
                        paths.Add(new PathAccess(isWrite, ShellPaths.Resolve(exact.Value, baseDirectory)));
                        break;
                    }
                    case ShellValueDomain.FiniteSet finite:
                    {
                        foreach (var value in finite.Values)
                        {
                            paths.Add(new PathAccess(isWrite, ShellPaths.Resolve(value, baseDirectory)));
                        }

                        break;
                    }
                    case ShellValueDomain.PathPattern pattern:
                    {
                        paths.Add(new PathAccess(isWrite, ShellPaths.Resolve(pattern.CoveringDirectory, baseDirectory)));
                        break;
                    }
                    default:
                    {
                        unbounded($"the redirect target of `{display}` cannot be determined statically");
                        paths.Add(new PathAccess(isWrite, new ResolvedPath(ResolvedPathKind.Unknown, null, redirect.Authored?.Target ?? "?")));
                        break;
                    }
                }

                break;
            }
            case DescriptorDuplicateRedirectAnalysis:
            case DescriptorMoveRedirectAnalysis:
            case DescriptorCloseRedirectAnalysis:
            {
                break;
            }
            case HereDocumentRedirectAnalysis hereDocument:
            {
                if (!hereDocument.Document.IsComplete)
                {
                    unbounded($"a here-document of `{display}` is incomplete");
                }

                break;
            }
            case HereStringRedirectAnalysis:
            {
                break;
            }
            default:
            {
                unbounded($"a redirect of `{display}` is not recognized");
                break;
            }
        }
    }

    private static bool TryGetPathCandidates(AnalyzedArgument analyzed, string? baseDirectory, out List<ResolvedPath> candidates)
    {
        candidates = [];
        var argument = analyzed.Argument;

        if (OperatingSystem.IsLinux() && argument.Raw.Contains('\\'))
        {
            candidates.Add(new ResolvedPath(ResolvedPathKind.Unknown, null, argument.Raw));
            return true;
        }

        switch (analyzed.AuthoredFileSystemValue)
        {
            case ShellValueDomain.Exact exactFile:
            {
                candidates.Add(ShellPaths.Resolve(exactFile.Value, baseDirectory));
                return true;
            }
            case ShellValueDomain.FiniteSet finiteFile:
            {
                candidates.AddRange(finiteFile.Values.Select(value => ShellPaths.Resolve(value, baseDirectory)));
                return true;
            }
        }

        if (!string.IsNullOrEmpty(argument.Resolved))
        {
            candidates.Add(ShellPaths.Resolve(argument.Resolved, baseDirectory));
            return true;
        }

        switch (analyzed.Value)
        {
            case ShellValueDomain.Exact exact:
            {
                if (argument.Kind == ArgKind.Glob || (argument.IsPath && ContainsWildcard(exact.Value)))
                {
                    candidates.Add(ShellPaths.Resolve(ShellPaths.GlobCoveringPrefix(exact.Value), baseDirectory));
                    return true;
                }

                if (argument.IsPath || LooksLikePath(exact.Value))
                {
                    candidates.Add(ShellPaths.Resolve(exact.Value, baseDirectory));
                }

                return true;
            }
            case ShellValueDomain.FiniteSet finite:
            {
                if (argument.IsPath || finite.Values.Any(LooksLikePath))
                {
                    candidates.AddRange(finite.Values.Select(value => ShellPaths.Resolve(value, baseDirectory)));
                }

                return true;
            }
            case ShellValueDomain.PathPattern pattern:
            {
                candidates.Add(ShellPaths.Resolve(pattern.CoveringDirectory, baseDirectory));
                return true;
            }
            case ShellValueDomain.IntegerRange:
            {
                return true;
            }
            case ShellValueDomain.Unknown:
            {
                if (argument.Kind == ArgKind.Glob && !argument.Raw.Contains('$') && !argument.Raw.Contains('`') && !argument.Raw.Contains('{'))
                {
                    var prefix = ShellPaths.GlobCoveringPrefix(argument.Raw.Trim('\'', '"'));
                    candidates.Add(ShellPaths.Resolve(prefix, baseDirectory));
                    return true;
                }

                return false;
            }
            default:
            {
                return false;
            }
        }
    }

    private static bool ContainsWildcard(string value)
    {
        return value.IndexOfAny(['*', '?', '[']) >= 0;
    }

    private static bool LooksLikePath(string value)
    {
        if (value.Length == 0 || ShellPaths.IsUrl(value))
        {
            return false;
        }

        return value.Contains('/') || value.Contains('\\') || value.StartsWith('~') || value.StartsWith('.') || (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':');
    }

    private static string? ResolveWorkingDirectory(ShellValueDomain directory, ClassificationScope scope)
    {
        if (directory is not ShellValueDomain.Exact exact)
        {
            return null;
        }

        var resolved = ShellPaths.Resolve(exact.Value, scope.WorkingDirectory);
        return resolved.Kind == ResolvedPathKind.Local ? resolved.Path : null;
    }

    private static string BuildDisplay(CommandOccurrence occurrence)
    {
        var elements = occurrence.Clause.Elements;

        if (elements.Count == 0)
        {
            return occurrence.Clause.Verb.Joined;
        }

        return string.Join(" ", elements.Select(element => element.Raw));
    }

    private static bool IsKnownRole(CommandOccurrenceRole role)
    {
        return role is CommandOccurrenceRole.Ordinary
            or CommandOccurrenceRole.PipelineStage
            or CommandOccurrenceRole.Iterator
            or CommandOccurrenceRole.LoopBody
            or CommandOccurrenceRole.Substitution
            or CommandOccurrenceRole.ExecutionRegion
            or CommandOccurrenceRole.Condition
            or CommandOccurrenceRole.Branch;
    }

    private static IReadOnlyList<ClauseElement> FindAccountedRegionArguments(ParsedCommand parsed)
    {
        var accounted = new List<ClauseElement>();

        foreach (var occurrence in parsed.Commands)
        {
            if (!occurrence.IsComplete)
            {
                continue;
            }

            foreach (var frame in occurrence.Ancestry)
            {
                if (frame.Region == CommandAncestryRegion.ExecutionRegion
                    && frame.Ancestor is ExecutionRegionSyntax region
                    && IsKnownCommandArgumentRegion(region)
                    && !accounted.Any(element => ReferenceEquals(element, region.HostArgument)))
                {
                    accounted.Add(region.HostArgument!);
                }
            }
        }

        return accounted;
    }

    private static bool IsKnownCommandArgumentRegion(ExecutionRegionSyntax region)
    {
        return region.Origin == ExecutionRegionOrigin.CommandArgument
            && region.HostArgument != null
            && Enum.IsDefined(region.Phase)
            && region.Phase != ExecutionRegionPhase.Unknown
            && Enum.IsDefined(region.Timing)
            && region.Timing != ExecutionRegionTiming.Unknown
            && Enum.IsDefined(region.Cardinality)
            && region.Cardinality != ExecutionRegionCardinality.Unknown;
    }
}
