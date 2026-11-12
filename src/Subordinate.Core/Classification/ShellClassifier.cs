using ShellSyntaxTree;
using Subordinate.Core.Policy;

namespace Subordinate.Core.Classification;

/// <summary>
///     Classifies shell commands from parser facts and the command database. Every uncertainty fails closed to a capability that needs approval.
/// </summary>
public static class ShellClassifier
{
    public const string ToolName = "shell";

    public static ActionClassification Classify(string source, ShellKind shell, ClassificationScope scope)
    {
        var sharedCapabilities = new HashSet<Capability> { Capability.Read };
        var sharedReasons = new List<string>();
        var units = new List<ActionUnit>();

        foreach (var match in SensitivePaths.FindMatches(source))
        {
            sharedCapabilities.Add(Capability.Sensitive);
            sharedReasons.Add($"the command references a credential store ({match})");
        }

        var parsed = Parse(source, shell, scope, out var parseError);

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

            foreach (var occurrence in parsed.Commands)
            {
                units.Add(ClassifyOccurrence(occurrence, scope, accountedRegionArguments));
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
                    var bashDirectory = ShellPaths.ToBashPath(scope.WorkingDirectory);

                    if (bashDirectory == null)
                    {
                        error = "the working directory cannot be expressed as a Git Bash path";
                        return null;
                    }

                    var parser = new BashParser(new BashParserOptions
                    {
                        WorkingDirectory = bashDirectory,
                        InitialStateMode = BashInitialStateMode.Unknown
                    });

                    return parser.Parse(source);
                }
                case ShellKind.WindowsPowerShell:
                case ShellKind.PowerShell7:
                {
                    var parser = new PwshParser(new PwshParserOptions
                    {
                        WorkingDirectory = scope.WorkingDirectory,
                        InitialStateMode = PwshInitialStateMode.Unknown,
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

    private static ActionUnit ClassifyOccurrence(CommandOccurrence occurrence, ClassificationScope scope, IReadOnlyList<ClauseElement> accountedRegionArguments)
    {
        var capabilities = new HashSet<Capability> { Capability.Read };
        var reasons = new List<string>();
        var paths = new List<PathAccess>();
        var display = BuildDisplay(occurrence);
        var words = occurrence.CommandWords is ShellCommandWords.Known known
            ? known.Words.Select(word => word.ToLowerInvariant()).ToArray()
            : [];
        var suspectWords = words.Skip(1).Any(word => word.StartsWith('-') || word.Contains('='));
        var key = words.Length > 0 && !suspectWords ? PolicySet.NormalizeCommandWords(string.Join(" ", words)) : null;

        void Unbounded(string reason)
        {
            capabilities.Add(Capability.Unbounded);
            if (!reasons.Contains(reason))
            {
                reasons.Add(reason);
            }
        }

        if (suspectWords)
        {
            Unbounded($"the subcommand of `{display}` cannot be determined because an option comes before it");
            words = [];
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

        if (occurrence.Assignments.Count > 0)
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

        var baseDirectory = ResolveWorkingDirectory(occurrence.WorkingDirectory, scope);
        var programWord = verb.CanonicalVerb ?? verb.Tokens[0];
        var program = CommandDatabase.NormalizeProgram(programWord);
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

        return new ActionUnit
        {
            Display = display,
            Key = key,
            Capabilities = capabilities,
            Reasons = reasons,
            Paths = paths
        };
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
