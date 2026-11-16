using ShellSyntaxTree;
using Subordinate.Core.Policy;

namespace Subordinate.Core.Classification;

/// <summary>
///     The curated command database. Lookups are case-insensitive on the program name, with any directory and executable extension removed.
/// </summary>
public static class CommandDatabase
{
    private static readonly Dictionary<string, CommandRule> Rules = Build();

    public static bool TryGet(string program, out CommandRule rule)
    {
        return Rules.TryGetValue(program, out rule!);
    }

    public static int Count => Rules.Count;

    /// <summary>
    ///     Reduces a program word to its lookup name, for example C:\Program Files\Git\cmd\git.exe to git.
    /// </summary>
    public static string NormalizeProgram(string program)
    {
        var name = program.Replace('\\', '/');
        var slash = name.LastIndexOf('/');

        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        foreach (var extension in new[] { ".exe", ".cmd", ".bat", ".com", ".ps1" })
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^extension.Length];
                break;
            }
        }

        return name.ToLowerInvariant();
    }

    private static CommandRule Data()
    {
        return new CommandRule { Operands = OperandAccess.Data };
    }

    private static CommandRule ReadOnly(Action<RuleContext>? refine = null)
    {
        return new CommandRule { Operands = OperandAccess.Read, Refine = refine };
    }

    private static CommandRule Writes(Action<RuleContext>? refine = null)
    {
        return new CommandRule { Capabilities = [Capability.Write], Operands = OperandAccess.Write, Refine = refine };
    }

    private static CommandRule Exec(Action<RuleContext>? refine = null)
    {
        return new CommandRule { Capabilities = [Capability.Exec], Operands = OperandAccess.Read, WritesWorkingDirectory = true, Refine = refine };
    }

    private static CommandRule Network(OperandAccess operands = OperandAccess.Read, Action<RuleContext>? refine = null)
    {
        return new CommandRule { Capabilities = [Capability.Network], Operands = operands, Refine = refine };
    }

    private static CommandRule NetworkWrite()
    {
        return new CommandRule { Capabilities = [Capability.Network, Capability.Write], Operands = OperandAccess.Read, WritesWorkingDirectory = true };
    }

    private static CommandRule NetworkExec()
    {
        return new CommandRule { Capabilities = [Capability.Network, Capability.Write, Capability.Exec], Operands = OperandAccess.Read, WritesWorkingDirectory = true };
    }

    private static CommandRule Dangerous(params Capability[] extra)
    {
        return new CommandRule { Capabilities = [Capability.Dangerous, .. extra], Operands = OperandAccess.Write };
    }

    private static CommandRule Interpreter()
    {
        return new CommandRule { Capabilities = [Capability.Exec], Operands = OperandAccess.Read, WritesWorkingDirectory = true, IsInterpreter = true, Refine = RefineInterpreter };
    }

    private static Dictionary<string, CommandRule> Build()
    {
        var rules = new Dictionary<string, CommandRule>(StringComparer.OrdinalIgnoreCase);

        void Add(CommandRule rule, params string[] names)
        {
            foreach (var name in names)
            {
                rules.Add(name, rule);
            }
        }

        Add(Data(), "echo", "printf", "true", "false", "sleep", "seq", "expr", "test", "[", "date", "uname", "whoami", "id", "hostname", "pwd", "nproc", "arch",
            "basename", "dirname", "which", "type", "command", "where", ":", "clear", "exit", "return", "break", "continue", "read", "shift", "wait", "export");
        Add(ReadOnly(RefineSearch), "grep", "egrep", "fgrep", "rg", "ag", "ack", "fd", "findstr");
        Add(ReadOnly(), "cd", "pushd", "popd", "cat", "head", "tail", "less", "more", "wc", "tree", "ls", "dir",
            "stat", "file", "du", "df", "realpath", "readlink", "cut", "nl", "column", "paste", "join", "comm", "diff", "cmp", "md5sum", "sha1sum", "sha256sum",
            "sha512sum", "cksum", "base64", "xxd", "od", "hexdump", "strings", "jq", "yq", "tac", "rev", "fold", "fmt", "expand", "unexpand", "tasklist",
            "printenv", "fc", "systeminfo", "ver");
        Add(ReadOnly(RefinePs), "ps");
        Add(ReadOnly(RefineTr), "tr");
        Add(ReadOnly(RefineFind), "find");
        Add(ReadOnly(RefineSed), "sed");
        Add(ReadOnly(RefineAwk), "awk", "gawk", "mawk");
        Add(new CommandRule { Operands = OperandAccess.Read, WriteValueFlags = new HashSet<string> { "-o", "--output" } }, "sort");
        Add(ReadOnly(RefineUniq), "uniq");
        Add(ReadOnly(RefineEnv), "env");
        Add(ReadOnly(RefineXargs), "xargs");
        Add(new CommandRule { Capabilities = [Capability.Unclassified], Operands = OperandAccess.Read }, "timeout", "nice", "nohup", "time", "watch", "mktemp", "parallel");

        Add(Writes(), "touch", "mkdir", "rmdir", "rm", "del", "erase", "mv", "move", "cp", "copy", "xcopy", "robocopy", "ln", "chmod", "truncate", "install", "tee", "patch",
            "zip", "unzip", "gzip", "gunzip", "bzip2", "xz", "7z", "attrib", "mklink", "rename", "ren");
        Add(Writes(RefineTar), "tar");

        Add(Dangerous(), "dd", "shred", "mkfs", "fdisk", "format", "diskpart", "shutdown", "reboot", "halt", "poweroff", "kill", "killall", "pkill", "taskkill",
            "sudo", "su", "runas", "doas", "chown", "chgrp", "crontab", "schtasks", "at", "setx", "netsh", "bcdedit", "wmic", "icacls", "takeown", "cipher", "sc",
            "eval", "exec", "source", ".");
        Add(new CommandRule { Operands = OperandAccess.Data, Refine = RefineRegistry }, "reg");

        Add(Network(OperandAccess.Data, RefineCurl), "curl");
        Add(new CommandRule { Capabilities = [Capability.Network, Capability.Write], Operands = OperandAccess.Data, WritesWorkingDirectory = true }, "wget");
        Add(Network(OperandAccess.Data), "ssh", "nc", "ncat", "telnet", "ftp", "ping", "nslookup", "dig", "host", "traceroute", "tracert", "gh", "glab", "az", "aws",
            "gcloud", "kubectl", "helm");
        Add(new CommandRule { Capabilities = [Capability.Network, Capability.Write], Operands = OperandAccess.Write }, "scp", "sftp", "rsync");
        Add(new CommandRule { Capabilities = [Capability.Network, Capability.Exec], Operands = OperandAccess.Read, WritesWorkingDirectory = true }, "terraform", "npx", "pnpx", "bunx");

        Add(Exec(), "make", "cmake", "ninja", "msbuild", "gradle", "gradlew", "mvn", "mvnw", "ant", "sbt", "bazel", "pytest", "tox", "jest", "vitest", "tsc", "eslint",
            "prettier", "black", "ruff", "mypy", "pylint", "flake8", "rustc", "gcc", "g++", "cc", "clang", "clang++", "javac", "kotlinc", "swiftc", "nuget");
        Add(Interpreter(), "bash", "sh", "zsh", "dash", "fish", "pwsh", "powershell", "cmd", "python", "python3", "py", "node", "deno", "bun", "ruby", "perl",
            "php", "lua", "rscript", "java", "dotnet-script");

        Add(Git(), "git");
        Add(Dotnet(), "dotnet");
        Add(NodePackageManager(), "npm", "pnpm", "yarn");
        Add(Cargo(), "cargo");
        Add(Go(), "go");
        Add(Docker(), "docker", "podman");
        Add(Pip(), "pip", "pip3");

        AddPowerShell(Add);

        return rules;
    }

    private static void AddPowerShell(Action<CommandRule, string[]> add)
    {
        add(Data(), ["write-output", "write-host", "write-verbose", "write-warning", "write-error", "write-information", "write-debug", "out-null", "out-string", "out-host",
            "get-date", "start-sleep", "get-random", "get-location", "get-host", "get-culture", "select-object", "where-object", "foreach-object", "sort-object",
            "group-object", "measure-object", "format-table", "format-list", "format-wide", "format-custom", "convertto-json", "convertfrom-json", "convertto-csv",
            "convertfrom-csv", "convertto-xml", "compare-object", "get-member", "get-unique", "get-command", "get-help", "get-alias", "get-psdrive", "get-variable",
            "join-path", "split-path", "get-process", "get-service", "get-computerinfo", "get-uptime", "get-error", "test-json"]);
        add(ReadOnly(), ["get-childitem", "get-content", "get-item", "get-itemproperty", "test-path", "resolve-path", "select-string", "get-filehash", "get-acl",
            "set-location", "push-location", "pop-location", "import-csv", "select-xml", "get-itempropertyvalue"]);
        add(Writes(), ["set-content", "add-content", "out-file", "new-item", "remove-item", "copy-item", "move-item", "rename-item", "clear-content", "clear-item",
            "expand-archive", "compress-archive", "export-csv", "export-clixml", "tee-object", "set-itemproperty"]);
        add(Network(OperandAccess.Data), ["invoke-webrequest", "invoke-restmethod", "start-bitstransfer", "test-netconnection", "test-connection", "resolve-dnsname"]);
        add(Dangerous(), ["start-process", "invoke-expression", "invoke-command", "invoke-item", "stop-process", "restart-computer", "stop-computer",
            "set-executionpolicy", "new-service", "set-service", "remove-itemproperty", "set-item", "new-itemproperty", "remove-psdrive", "new-psdrive",
            "register-scheduledtask", "set-acl", "add-type", "import-module", "enable-psremoting", "start-job", "send-mailmessage", "new-object",
            "set-alias", "new-alias", "remove-variable", "set-psdebug", "new-temporaryfile", "import-clixml", "measure-command"]);
    }

    private static CommandRule Git()
    {
        var read = ReadOnly(RefineGitCommon);
        var write = new CommandRule { Capabilities = [Capability.Write], Operands = OperandAccess.Read, Refine = RefineGitCommon };
        var network = new CommandRule { Capabilities = [Capability.Network], Operands = OperandAccess.Read, Refine = RefineGitCommon };
        var networkWrite = new CommandRule { Capabilities = [Capability.Network, Capability.Write], Operands = OperandAccess.Read, Refine = RefineGitCommon };

        var subcommands = new Dictionary<string, CommandRule>(StringComparer.Ordinal);

        foreach (var name in new[] { "status", "diff", "log", "show", "blame", "annotate", "rev-parse", "ls-files", "ls-tree", "cat-file", "grep", "describe",
                     "shortlog", "merge-base", "name-rev", "for-each-ref", "rev-list", "count-objects", "check-ignore", "check-attr", "whatchanged", "show-ref",
                     "var", "help", "version", "show-branch", "cherry", "range-diff", "diff-tree", "diff-files", "diff-index", "verify-commit", "verify-tag",
                     "fsck", "reflog" })
        {
            subcommands[name] = read;
        }

        foreach (var name in new[] { "add", "commit", "checkout", "switch", "restore", "merge", "rebase", "cherry-pick", "revert", "mv", "rm", "apply", "am",
                     "init", "notes", "bisect", "sparse-checkout", "update-index", "mergetool" })
        {
            subcommands[name] = write;
        }

        foreach (var name in new[] { "fetch", "ls-remote", "push", "request-pull", "send-email" })
        {
            subcommands[name] = network;
        }

        foreach (var name in new[] { "pull", "clone", "submodule" })
        {
            subcommands[name] = networkWrite;
        }

        subcommands["branch"] = ReadOnly(RefineGitBranch);
        subcommands["tag"] = ReadOnly(RefineGitTag);
        subcommands["stash"] = ReadOnly(RefineGitStash);
        subcommands["config"] = ReadOnly(RefineGitConfig);
        subcommands["remote"] = ReadOnly(RefineGitRemote);
        subcommands["worktree"] = ReadOnly(RefineGitWorktree);
        subcommands["reset"] = ReadOnly(RefineGitReset);
        subcommands["clean"] = ReadOnly(RefineGitClean);
        subcommands["gc"] = Writes(RefineGitCommon);
        subcommands["prune"] = Dangerous();
        subcommands["filter-branch"] = Dangerous();
        subcommands["filter-repo"] = Dangerous();
        subcommands["update-ref"] = Dangerous();
        subcommands["difftool"] = Exec(RefineGitCommon);

        return new CommandRule
        {
            Operands = OperandAccess.Read,
            Subcommands = subcommands,
            WithoutSubcommand = Data()
        };
    }

    private static CommandRule Dotnet()
    {
        var exec = Exec();
        var subcommands = new Dictionary<string, CommandRule>(StringComparer.Ordinal)
        {
            ["build"] = exec,
            ["test"] = exec,
            ["run"] = exec,
            ["pack"] = exec,
            ["publish"] = exec,
            ["clean"] = exec,
            ["msbuild"] = exec,
            ["build-server"] = exec,
            ["watch"] = exec,
            ["vstest"] = exec,
            ["format"] = new CommandRule { Capabilities = [Capability.Write, Capability.Exec], Operands = OperandAccess.Read, WritesWorkingDirectory = true },
            ["restore"] = NetworkWrite(),
            ["add"] = NetworkWrite(),
            ["remove"] = Writes(),
            ["new"] = Writes(),
            ["nuget"] = Network(),
            ["tool"] = NetworkExec(),
            ["workload"] = Dangerous(Capability.Network),
            ["dev-certs"] = Dangerous(),
            ["user-secrets"] = new CommandRule { Capabilities = [Capability.Sensitive], Operands = OperandAccess.Data },
            ["sln"] = ReadOnly(context =>
            {
                if (context.Words.Count < 3 || context.Words[2] != "list")
                {
                    context.Add(Capability.Write, "dotnet sln modifies the solution file");
                }
            }),
            ["list"] = ReadOnly(context =>
            {
                if (context.HasFlag("--outdated", "--vulnerable", "--deprecated"))
                {
                    context.Add(Capability.Network, "dotnet list queries package feeds");
                }
            }),
            ["help"] = Data(),
            ["ef"] = NetworkExec()
        };

        return new CommandRule
        {
            Operands = OperandAccess.Read,
            Subcommands = subcommands,
            WithoutSubcommand = Data()
        };
    }

    private static CommandRule NodePackageManager()
    {
        var install = NetworkExec();
        var exec = Exec();
        var read = ReadOnly();
        var readNetwork = Network();
        var publish = Dangerous(Capability.Network);

        var subcommands = new Dictionary<string, CommandRule>(StringComparer.Ordinal);

        foreach (var name in new[] { "install", "i", "ci", "add", "update", "up", "upgrade", "uninstall", "remove", "rm", "un", "dedupe", "prune", "link" })
        {
            subcommands[name] = install;
        }

        foreach (var name in new[] { "run", "run-script", "test", "t", "start", "stop", "restart", "exec", "x", "rebuild", "build", "lint", "dlx" })
        {
            subcommands[name] = exec;
        }

        foreach (var name in new[] { "ls", "list", "ll", "la", "why", "explain", "root", "prefix", "bin", "help", "--version", "version", "config", "get" })
        {
            subcommands[name] = read;
        }

        foreach (var name in new[] { "view", "info", "show", "outdated", "audit", "search", "ping", "whoami", "doctor" })
        {
            subcommands[name] = readNetwork;
        }

        foreach (var name in new[] { "publish", "unpublish", "deprecate", "dist-tag", "owner", "token", "adduser", "login", "logout", "access", "set" })
        {
            subcommands[name] = publish;
        }

        subcommands["pack"] = Writes();
        subcommands["init"] = Writes();
        subcommands["config"] = ReadOnly(context =>
        {
            if (context.Words.Count < 3 || context.Words[2] is not ("get" or "list" or "ls"))
            {
                context.Add(Capability.Dangerous, "changing package manager configuration writes outside the workdir");
            }
        });

        return new CommandRule
        {
            Operands = OperandAccess.Read,
            Subcommands = subcommands,
            WithoutSubcommand = install,
            OtherSubcommand = exec
        };
    }

    private static CommandRule Cargo()
    {
        var exec = Exec();
        var subcommands = new Dictionary<string, CommandRule>(StringComparer.Ordinal);

        foreach (var name in new[] { "build", "b", "check", "c", "test", "t", "run", "r", "bench", "doc", "clippy", "clean", "fix" })
        {
            subcommands[name] = exec;
        }

        subcommands["fmt"] = new CommandRule { Capabilities = [Capability.Write, Capability.Exec], Operands = OperandAccess.Read, WritesWorkingDirectory = true };

        foreach (var name in new[] { "fetch", "update", "add", "remove", "generate-lockfile" })
        {
            subcommands[name] = NetworkWrite();
        }

        foreach (var name in new[] { "install", "uninstall", "login", "logout", "publish", "yank", "owner" })
        {
            subcommands[name] = Dangerous(Capability.Network);
        }

        foreach (var name in new[] { "tree", "metadata", "version", "search", "help", "pkgid", "locate-project", "verify-project" })
        {
            subcommands[name] = ReadOnly();
        }

        return new CommandRule { Operands = OperandAccess.Read, Subcommands = subcommands, WithoutSubcommand = Data() };
    }

    private static CommandRule Go()
    {
        var exec = Exec();
        var subcommands = new Dictionary<string, CommandRule>(StringComparer.Ordinal)
        {
            ["build"] = exec,
            ["test"] = exec,
            ["run"] = exec,
            ["vet"] = exec,
            ["generate"] = exec,
            ["env"] = ReadOnly(),
            ["version"] = ReadOnly(),
            ["list"] = ReadOnly(),
            ["doc"] = ReadOnly(),
            ["help"] = ReadOnly(),
            ["fmt"] = Writes(),
            ["clean"] = Writes(),
            ["get"] = NetworkWrite(),
            ["install"] = Dangerous(Capability.Network),
            ["work"] = Writes(),
            ["mod"] = ReadOnly(context =>
            {
                if (context.Words.Count < 3 || context.Words[2] is not ("graph" or "why" or "verify"))
                {
                    context.Add(Capability.Network, "go mod can download modules");
                    context.Add(Capability.Write, "go mod can rewrite go.mod and go.sum");
                }
            })
        };

        return new CommandRule { Operands = OperandAccess.Read, Subcommands = subcommands, WithoutSubcommand = Data() };
    }

    private static CommandRule Docker()
    {
        var read = Data();
        var subcommands = new Dictionary<string, CommandRule>(StringComparer.Ordinal);

        foreach (var name in new[] { "ps", "images", "inspect", "logs", "version", "info", "stats", "top", "port", "diff", "history", "search" })
        {
            subcommands[name] = read;
        }

        subcommands["build"] = NetworkExec();
        subcommands["pull"] = Network(OperandAccess.Data);

        return new CommandRule
        {
            Operands = OperandAccess.Read,
            Subcommands = subcommands,
            WithoutSubcommand = Data(),
            OtherSubcommand = Dangerous(Capability.Network)
        };
    }

    private static CommandRule Pip()
    {
        var subcommands = new Dictionary<string, CommandRule>(StringComparer.Ordinal)
        {
            ["list"] = Data(),
            ["show"] = Data(),
            ["freeze"] = Data(),
            ["check"] = Data(),
            ["help"] = Data(),
            ["download"] = NetworkWrite()
        };

        return new CommandRule
        {
            Operands = OperandAccess.Read,
            Subcommands = subcommands,
            WithoutSubcommand = Data(),
            OtherSubcommand = Dangerous(Capability.Network)
        };
    }

    private static void RefineGitCommon(RuleContext context)
    {
        for (var index = 0; index < context.Arguments.Count; index++)
        {
            var argument = context.Arguments[index];

            if (!argument.IsFlag)
            {
                continue;
            }

            if (argument.Text == "-c" && index + 1 < context.Arguments.Count && context.Arguments[index + 1].Text.Contains('='))
            {
                context.Add(Capability.Unbounded, "git -c overrides configuration, which can run arbitrary programs");
            }

            if (argument.Text is "--config-env" or "--exec-path" or "--upload-pack" or "--receive-pack" or "--ext-diff" or "--open-files-in-pager" or "-O")
            {
                context.Add(Capability.Unbounded, $"git option {argument.Text} can run arbitrary programs");
            }

            if (RuleContext.FlagMatches(argument.Text, "--output"))
            {
                var value = context.ValueOf("--output");

                if (value != null)
                {
                    context.MarkWrite(value);
                }
                else
                {
                    context.Add(Capability.Write, "git --output writes a file");
                }
            }
        }

        if (context.Subcommand is "diff" or "log" or "show" or "grep" or "blame" or "rev-parse" or "describe" or "rev-list" or "shortlog" or "cherry")
        {
            foreach (var operand in context.Operands)
            {
                if (!LooksLikeExplicitPath(operand.Text))
                {
                    context.MarkData(operand);
                }
            }
        }
    }

    private static bool LooksLikeExplicitPath(string text)
    {
        return text.StartsWith('/') || text.StartsWith('~') || text.StartsWith("..", StringComparison.Ordinal) || (text.Length >= 2 && text[1] == ':') || text.Contains('\\');
    }

    private static void RefineGitBranch(RuleContext context)
    {
        RefineGitCommon(context);

        if (context.HasFlag("-D") || (context.HasFlag("-d", "--delete") && context.HasFlag("-f", "--force")))
        {
            context.Add(Capability.Dangerous, "git branch -D deletes a branch even if it is not merged");
            return;
        }

        if (context.HasFlag("-d", "--delete", "-m", "-M", "--move", "-c", "-C", "--copy", "-u", "--set-upstream-to", "--unset-upstream", "--edit-description", "-f", "--force"))
        {
            context.Add(Capability.Write, "git branch modifies branches");
            return;
        }

        if (context.Operands.Any() && !context.HasFlag("-l", "--list", "--contains", "--no-contains", "--merged", "--no-merged", "--points-at"))
        {
            context.Add(Capability.Write, "git branch with a name creates a branch");
        }

        MarkOperandsAsData(context);
    }

    private static void RefineGitTag(RuleContext context)
    {
        RefineGitCommon(context);

        if (context.HasFlag("-d", "--delete"))
        {
            context.Add(Capability.Dangerous, "git tag -d deletes a tag");
            return;
        }

        if (context.Operands.Any() && !context.HasFlag("-l", "--list", "-n", "--contains", "--no-contains", "--merged", "--no-merged", "--points-at", "-v", "--verify"))
        {
            context.Add(Capability.Write, "git tag with a name creates a tag");
        }

        MarkOperandsAsData(context);
    }

    private static void RefineGitStash(RuleContext context)
    {
        RefineGitCommon(context);
        var action = context.Words.Count > 2 ? context.Words[2] : "push";

        switch (action)
        {
            case "list":
            case "show":
            {
                break;
            }
            case "drop":
            case "clear":
            {
                context.Add(Capability.Dangerous, "git stash drop and clear discard stashed work");
                break;
            }
            default:
            {
                context.Add(Capability.Write, "git stash modifies the working tree");
                break;
            }
        }

        MarkOperandsAsData(context);
    }

    private static void RefineGitConfig(RuleContext context)
    {
        RefineGitCommon(context);

        if (context.HasFlag("--global", "--system"))
        {
            if (!context.HasFlag("--get", "--get-all", "--get-regexp", "-l", "--list", "--show-origin", "--show-scope", "--get-urlmatch"))
            {
                context.Add(Capability.WriteOutside, "git config --global or --system writes configuration outside the workdir");
            }

            MarkOperandsAsData(context);
            return;
        }

        if (context.HasFlag("--get", "--get-all", "--get-regexp", "-l", "--list", "--show-origin", "--show-scope", "--get-urlmatch"))
        {
            MarkOperandsAsData(context);
            return;
        }

        var operandCount = context.Operands.Count();
        if (operandCount >= 2 || context.HasFlag("--unset", "--unset-all", "--add", "--replace-all", "--rename-section", "--remove-section", "-e", "--edit"))
        {
            context.Add(Capability.Write, "git config changes repository configuration");
        }

        MarkOperandsAsData(context);
    }

    private static void RefineGitRemote(RuleContext context)
    {
        RefineGitCommon(context);
        var action = context.Words.Count > 2 ? context.Words[2] : null;

        switch (action)
        {
            case null:
            case "get-url":
            {
                break;
            }
            case "show":
            case "update":
            case "prune":
            {
                context.Add(Capability.Network, "git remote contacts the remote repository");
                break;
            }
            default:
            {
                context.Add(Capability.Write, "git remote changes the remote configuration");
                break;
            }
        }

        MarkOperandsAsData(context);
    }

    private static void RefineGitWorktree(RuleContext context)
    {
        RefineGitCommon(context);
        var action = context.Words.Count > 2 ? context.Words[2] : null;

        if (action != "list")
        {
            context.Add(Capability.Write, "git worktree modifies worktrees");
        }
    }

    private static void RefineGitReset(RuleContext context)
    {
        RefineGitCommon(context);

        if (context.HasFlag("--hard", "--merge", "--keep"))
        {
            context.Add(Capability.Dangerous, "git reset --hard discards uncommitted work");
        }
        else
        {
            context.Add(Capability.Write, "git reset moves the branch or the index");
        }

        MarkOperandsAsData(context);
    }

    private static void RefineGitClean(RuleContext context)
    {
        RefineGitCommon(context);

        if (context.HasFlag("-n", "--dry-run"))
        {
            return;
        }

        context.Add(Capability.Dangerous, "git clean deletes untracked files");
    }

    private static void MarkOperandsAsData(RuleContext context)
    {
        foreach (var operand in context.Operands)
        {
            if (!LooksLikeExplicitPath(operand.Text))
            {
                context.MarkData(operand);
            }
        }
    }

    private static void RefineSearch(RuleContext context)
    {
        var valueFlags = context.Program switch
        {
            "rg" => new[] { "-e", "--regexp", "-g", "--glob", "--iglob", "-t", "--type", "-T", "--type-not", "-E", "--encoding", "--sort", "--sortr", "-r", "--replace" },
            "fd" => new[] { "-e", "--extension", "-t", "--type", "-E", "--exclude" },
            _ => new[] { "-e", "--regexp", "--include", "--exclude", "--exclude-dir" }
        };

        foreach (var flag in valueFlags)
        {
            for (var index = 0; index < context.Arguments.Count - 1; index++)
            {
                if (context.Arguments[index].IsFlag && context.Arguments[index].Text == flag)
                {
                    context.MarkData(context.Arguments[index + 1]);
                }
            }
        }

        if (context.HasFlag("--pre"))
        {
            context.Add(Capability.Unbounded, $"{context.Program} --pre runs a preprocessor program");
        }

        if (context.HasFlag("-e", "--regexp", "-f", "--file", "--files"))
        {
            return;
        }

        var pattern = context.Operands.FirstOrDefault(operand => !context.DataArguments.Contains(operand.Index));

        if (pattern != null)
        {
            context.MarkData(pattern);
        }
    }

    private static void RefinePs(RuleContext context)
    {
        foreach (var text in context.RawElements.Skip(1))
        {
            if (!text.StartsWith('-'))
            {
                if (text.Contains('e'))
                {
                    context.Add(Capability.Sensitive, "ps can print the environment of other processes");
                    return;
                }

                continue;
            }

            if (text.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var flags = text[1..];

            if (flags.Contains('e') && (flags.Contains('u') || flags.Contains('x')))
            {
                context.Add(Capability.Sensitive, "ps can print the environment of other processes");
                return;
            }
        }
    }

    private static void RefineTr(RuleContext context)
    {
        foreach (var operand in context.Operands)
        {
            context.MarkData(operand);
        }
    }

    private static void RefineFind(RuleContext context)
    {
        if (context.HasFlag("-delete"))
        {
            context.Add(Capability.Write, "find -delete removes files");
        }

        if (context.HasFlag("-fprint", "-fprint0", "-fprintf", "-fls"))
        {
            context.Add(Capability.Write, "find -fprint writes a file");
        }

        var execFlags = new HashSet<string>(StringComparer.Ordinal) { "-exec", "-execdir", "-ok", "-okdir" };

        for (var index = 0; index < context.Arguments.Count; index++)
        {
            var argument = context.Arguments[index];

            if (!argument.IsFlag || !execFlags.Contains(argument.Text))
            {
                continue;
            }

            var parts = new List<string>();
            var terminated = false;
            var innerIndex = index + 1;

            for (; innerIndex < context.Arguments.Count; innerIndex++)
            {
                var part = context.Arguments[innerIndex];
                context.MarkData(part);

                if (!part.IsFlag && (part.Text == ";" || part.Text == "\\;" || part.Text == "+"))
                {
                    terminated = true;
                    break;
                }

                parts.Add(part.Raw.Replace("{}", "."));
            }

            if (terminated && parts.Count > 0)
            {
                context.AddNestedCommand(string.Join(" ", parts));
            }
            else
            {
                context.Add(Capability.Unbounded, "find -exec runs another program for every match");
            }

            index = innerIndex;
        }

        var patternFlags = new HashSet<string>(StringComparer.Ordinal) { "-name", "-iname", "-path", "-ipath", "-regex", "-iregex", "-wholename", "-iwholename", "-type", "-newer", "-perm", "-user", "-group", "-size", "-mtime", "-mmin", "-maxdepth", "-mindepth", "-printf" };

        for (var index = 0; index < context.Arguments.Count - 1; index++)
        {
            if (context.Arguments[index].IsFlag && patternFlags.Contains(context.Arguments[index].Text))
            {
                context.MarkData(context.Arguments[index + 1]);
            }
        }
    }

    private static void RefineXargs(RuleContext context)
    {
        var valueFlags = new HashSet<string>(StringComparer.Ordinal)
        {
            "-a", "--arg-file", "-d", "--delimiter", "-E", "--eof", "-I", "--replace", "-L", "--max-lines",
            "-n", "--max-args", "-P", "--max-procs", "-s", "--max-chars", "--process-slot-var"
        };

        var argumentFile = context.ValueOf("-a", "--arg-file");

        foreach (var argument in context.Arguments)
        {
            if (argumentFile == null || argument.Index != argumentFile.Index)
            {
                context.MarkData(argument);
            }
        }

        var elements = context.RawElements.Skip(1).ToList();
        var index = 0;

        while (index < elements.Count)
        {
            var element = elements[index];

            if (!element.StartsWith('-'))
            {
                break;
            }

            index += valueFlags.Contains(element) ? 2 : 1;
        }

        if (index >= elements.Count)
        {
            return;
        }

        var parts = new List<string>();

        for (var inner = index; inner < elements.Count; inner++)
        {
            parts.Add(elements[inner].Replace("{}", "."));
        }

        context.AddNestedCommand(string.Join(" ", parts));

        var innerProgram = NormalizeProgram(elements[index].Trim('\'', '"'));

        if (!TryGet(innerProgram, out var innerRule) || innerRule.Operands != OperandAccess.Data)
        {
            context.MarkNestedOperandsFromInput();
        }
    }

    private static void RefineSed(RuleContext context)
    {
        if (context.HasFlag("-i", "--in-place"))
        {
            context.Add(Capability.Write, "sed -i edits files in place");

            var operands = context.Operands.ToList();
            for (var index = 0; index < operands.Count; index++)
            {
                if (index == 0 && !context.HasFlag("-e", "--expression", "-f", "--file"))
                {
                    context.MarkData(operands[index]);
                    continue;
                }

                context.MarkWrite(operands[index]);
            }
        }
        else
        {
            var first = context.Operands.FirstOrDefault();

            if (first != null && !context.HasFlag("-e", "--expression", "-f", "--file"))
            {
                context.MarkData(first);
            }
        }

        foreach (var flagValue in new[] { context.ValueOf("-e"), context.ValueOf("--expression") })
        {
            if (flagValue != null)
            {
                context.MarkData(flagValue);
            }
        }

        foreach (var operand in context.Arguments)
        {
            if (!context.DataArguments.Contains(operand.Index))
            {
                continue;
            }

            if (operand.Value == null || SedScriptCanWriteOrExecute(operand.Value))
            {
                context.Add(Capability.Unbounded, "the sed script may write files or run commands");
            }
        }
    }

    private static bool SedScriptCanWriteOrExecute(string script)
    {
        for (var index = 0; index < script.Length; index++)
        {
            var character = script[index];

            if (character is not ('w' or 'W' or 'e'))
            {
                continue;
            }

            var previous = index == 0 ? ';' : script[index - 1];
            var next = index + 1 < script.Length ? script[index + 1] : ' ';

            if (previous is ';' or '}' or '{' or '/' or ' ' or '\n' && next is ' ' or ';' or '\n' or '}')
            {
                return true;
            }

            if (index == 0 && (next == ' ' || script.Length == 1))
            {
                return true;
            }
        }

        return false;
    }

    private static void RefineAwk(RuleContext context)
    {
        if (context.HasFlag("-f"))
        {
            context.Add(Capability.Unclassified, "awk -f runs a program from a file");
            return;
        }

        foreach (var flag in new[] { "-v", "--assign", "-F", "--field-separator" })
        {
            var value = context.ValueOf(flag);

            if (value != null)
            {
                context.MarkData(value);
            }
        }

        var program = context.Operands.FirstOrDefault(operand => !context.DataArguments.Contains(operand.Index));

        if (program == null)
        {
            return;
        }

        context.MarkData(program);

        if (program.Value == null || AwkProgramCanWriteOrExecute(program.Value))
        {
            context.Add(Capability.Unbounded, "the awk program may write files or run commands");
        }
    }

    private static bool AwkProgramCanWriteOrExecute(string program)
    {
        if (program.Contains("system", StringComparison.Ordinal) || program.Contains("getline", StringComparison.Ordinal) || program.Contains("@load", StringComparison.Ordinal) || program.Contains("@include", StringComparison.Ordinal))
        {
            return true;
        }

        var inString = false;
        var inRegex = false;
        var escaped = false;

        for (var index = 0; index < program.Length; index++)
        {
            var character = program[index];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (character == '\\')
            {
                escaped = true;
                continue;
            }

            if (inString)
            {
                if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (inRegex)
            {
                if (character == '/')
                {
                    inRegex = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
                continue;
            }

            if (character == '/')
            {
                inRegex = true;
                continue;
            }

            if (character == '|')
            {
                if (index + 1 < program.Length && program[index + 1] == '|')
                {
                    index++;
                    continue;
                }

                return true;
            }

            if (character == '>' && AwkStatementStartsWithPrint(program, index))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AwkStatementStartsWithPrint(string program, int index)
    {
        var start = index - 1;

        while (start >= 0 && program[start] is not (';' or '{' or '}' or '\n'))
        {
            start--;
        }

        var statement = program[(start + 1)..index].TrimStart();

        return statement.StartsWith("print", StringComparison.Ordinal) || statement.StartsWith("printf", StringComparison.Ordinal);
    }

    private static void RefineUniq(RuleContext context)
    {
        var operands = context.Operands.ToList();

        if (operands.Count >= 2)
        {
            context.MarkWrite(operands[1]);
            context.Add(Capability.Write, "uniq writes its second operand");
        }
    }

    private static void RefineEnv(RuleContext context)
    {
        if (context.Arguments.Count > 0)
        {
            context.Add(Capability.Unclassified, "env with arguments runs another program");
        }
    }

    private static void RefineTar(RuleContext context)
    {
        var listing = context.HasFlag("-t", "--list");

        if (!listing && context.Arguments.Count > 0 && !context.Arguments[0].IsFlag && context.Arguments[0].Text.Contains('t') && !context.Arguments[0].Text.Contains('/'))
        {
            listing = true;
        }

        if (listing)
        {
            return;
        }

        foreach (var argument in context.Arguments)
        {
            if (argument.IsFlag && argument.Text is "--to-command" or "--use-compress-program" or "-I" or "--checkpoint-action" or "--info-script" or "-F" or "--new-volume-script")
            {
                context.Add(Capability.Unbounded, $"tar {argument.Text} runs another program");
            }
        }
    }

    private static void RefineRegistry(RuleContext context)
    {
        if (context.Subcommand is not "query")
        {
            context.Add(Capability.Dangerous, "reg modifies or exports the Windows registry");
        }
    }

    private static void RefineCurl(RuleContext context)
    {
        foreach (var flag in new[] { "-o", "--output", "-T", "--upload-file", "-K", "--config", "-c", "--cookie-jar", "-D", "--dump-header", "--trace", "--trace-ascii", "--stderr" })
        {
            var value = context.ValueOf(flag);

            if (value != null)
            {
                if (flag is "-T" or "--upload-file" or "-K" or "--config")
                {
                    continue;
                }

                context.MarkWrite(value);
                context.Add(Capability.Write, "curl writes its output to a file");
            }
        }

        if (context.HasFlag("-O", "--remote-name", "--remote-name-all", "-J", "--remote-header-name"))
        {
            context.Add(Capability.Write, "curl saves the download in the working directory");
        }

        foreach (var flag in new[] { "-T", "--upload-file", "-K", "--config", "-d", "--data", "--data-binary", "-F", "--form" })
        {
            var value = context.ValueOf(flag);

            if (value != null && (flag is "-T" or "--upload-file" or "-K" or "--config" || value.Text.StartsWith('@')))
            {
                var path = value.Text.StartsWith('@') ? value.Text[1..] : value.Text;
                var sensitive = SensitivePaths.FindMatches(path);
                context.Add(Capability.Dangerous, $"curl sends the contents of a local file ({path}) over the network" + (sensitive.Count > 0 ? ", and the file is a credential store" : string.Empty));

                if (sensitive.Count > 0)
                {
                    context.Add(Capability.Sensitive, $"curl sends a credential store ({path}) over the network");
                }
            }
        }
    }

    private static void RefineInterpreter(RuleContext context)
    {
        var operands = context.Operands.ToList();
        var hasInlineCode = context.HasFlag("-c", "-e", "-Command", "-command", "-EncodedCommand", "-encodedcommand", "/c", "/C", "-p", "--eval", "--print");
        var hasModule = (context.Program is "python" or "python3" or "py") && context.HasFlag("-m", "--module");

        if (operands.Count == 0 && !hasInlineCode && !context.HasAuthoredStandardInput && context.Role == CommandOccurrenceRole.PipelineStage)
        {
            context.Add(Capability.Dangerous, $"{context.Program} executes whatever the previous pipeline stage produces");
        }

        if (hasInlineCode || hasModule)
        {
            foreach (var operand in operands)
            {
                context.MarkData(operand);
            }

            return;
        }

        for (var index = 1; index < operands.Count; index++)
        {
            context.MarkData(operands[index]);
        }
    }
}
