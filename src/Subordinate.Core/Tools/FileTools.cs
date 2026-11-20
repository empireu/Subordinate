using System.Globalization;
using System.Text;
using KnowledgeSystem.Agents.Orchestration;
using KnowledgeSystem.Agents.Orchestration.Tools;
using KnowledgeSystem.Agents.Tools;
using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Classification;
using Subordinate.Core.Images;
using Subordinate.Core.Platform;
using Subordinate.Core.Policy;
using Subordinate.Core.Running;

namespace Subordinate.Core.Tools;

internal static class FileToolPaths
{
    public static ResolvedPath Resolve(string requested, ToolEnvironment environment)
    {
        return ShellPaths.Resolve(requested, environment.Workdir);
    }

    public static ActionClassification Classify(string tool, string requested, bool isWrite, ToolEnvironment environment)
    {
        var path = Resolve(requested, environment);
        var capabilities = new HashSet<Capability> { Capability.Read };
        var reasons = new List<string>();
        var scope = environment.CreateScope(environment.Workdir);

        if (isWrite)
        {
            capabilities.Add(Capability.Write);
        }

        if (path.Kind != ResolvedPathKind.Local)
        {
            capabilities.Add(Capability.Unbounded);
            reasons.Add($"\"{requested}\" is not a local file path");
        }

        foreach (var match in SensitivePaths.FindMatches(requested))
        {
            capabilities.Add(Capability.Sensitive);
            reasons.Add($"the path refers to a credential store ({match})");
        }

        scope.ApplyPathCapabilities([new PathAccess(isWrite, path)], capabilities, reasons);

        return new ActionClassification
        {
            Tool = tool,
            Summary = $"{tool} {path.Display}",
            Units =
            [
                new ActionUnit
                {
                    Display = $"{tool} {path.Display}",
                    Key = path.Kind == ResolvedPathKind.Local ? PlatformServices.Current.Paths.GrantKey(path.Path!) : null,
                    Capabilities = capabilities,
                    Reasons = reasons,
                    Paths = [new PathAccess(isWrite, path)]
                }
            ],
            SharedCapabilities = new HashSet<Capability>(),
            SharedReasons = []
        };
    }

    public static string ReadText(string path, out bool isBinary)
    {
        return ReadText(path, out isBinary, out _);
    }

    public static string ReadText(string path, out bool isBinary, out bool hasByteOrderMark)
    {
        var bytes = File.ReadAllBytes(path);
        isBinary = Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8192)) >= 0;
        hasByteOrderMark = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

        if (isBinary)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), true);
        return reader.ReadToEnd();
    }

    public static string StableHash(string value)
    {
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
    }

    public static string MatchExistingLineEndings(string path, string content)
    {
        if (!File.Exists(path))
        {
            return content;
        }

        var existing = ReadText(path, out var isBinary);
        return !isBinary && LineEndings.IsCrlfDominant(existing) ? LineEndings.ToCrlf(content) : content;
    }
}

internal static class LineEndings
{
    public static bool IsCrlfDominant(string text)
    {
        var crlfCount = 0;
        var bareLineFeedCount = 0;

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n')
            {
                continue;
            }

            if (index > 0 && text[index - 1] == '\r')
            {
                crlfCount++;
            }
            else
            {
                bareLineFeedCount++;
            }
        }

        return crlfCount > bareLineFeedCount;
    }

    public static string ToCrlf(string text)
    {
        if (!text.Contains('\n'))
        {
            return text;
        }

        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
    }
}

/// <summary>
///     Reads a text file with line numbers, or shows an image file to a model that can see images.
/// </summary>
public sealed class ReadFileToolHandler(AgentTool tool, StringArgument pathArgument, IntegerArgument offsetArgument, IntegerArgument limitArgument, ImageSettings images)
    : ToolHandler<SubordinateContext>.Plain(tool), IGatedTool
{
    public const string ToolName = "read_file";

    public const long MaxImageBytes = 10_000_000;

    private const int DefaultLineLimit = 2000;
    private const int MaxLineLength = 2000;
    private const int MaxOutputCharacters = 100_000;
    private const string ImageLimitReachedMessage = "Maximum number of images in context reached.";
    private const string TextDescription = "Reads a text file and returns its lines prefixed with line numbers. Use offset and limit to page through large files.";
    private const string ImageDescription = "Reads a text file and returns its lines prefixed with line numbers, or shows you an image file (PNG, JPEG, GIF or WebP). Use offset and limit to page through large text files; they are ignored for images.";

    public static void Register(AgentToolRegistry<SubordinateContext> registry, ImageSettings images)
    {
        var readTool = new ToolBuilder(ToolName)
            .WithDescription(BuildDescription(images))
            .WithRequiredStringArgument("path", "The file path, relative to the session workdir or absolute.", out var pathArg)
            .WithIntegerArgument("offset", "The 1-based line number to start at. Default 1.", out var offsetArg)
            .WithIntegerArgument("limit", $"The number of lines to return. Default {DefaultLineLimit}.", out var limitArg)
            .Build();

        registry.RegisterTool(readTool, new ReadFileToolHandler(readTool, pathArg, offsetArg, limitArg, images));
    }

    private static string BuildDescription(ImageSettings images)
    {
        if (!images.Enabled)
        {
            return TextDescription;
        }

        if (images.MaxImages is not { } maxImages)
        {
            return ImageDescription;
        }

        return ImageDescription + $" At most {maxImages.ToString(CultureInfo.InvariantCulture)} images can be in the conversation at once. Once that many have been read, reading another image fails.";
    }

    public ActionClassification Classify(ArgumentExtractionResult args, ToolEnvironment environment)
    {
        return FileToolPaths.Classify(ToolName, pathArgument.GetValue(args), false, environment);
    }

    public string Fingerprint(ArgumentExtractionResult args, ToolEnvironment environment)
    {
        return $"{ToolName}\n{FileToolPaths.Resolve(pathArgument.GetValue(args), environment).Display}";
    }

    public CallDescription Describe(ArgumentExtractionResult args, ToolEnvironment environment)
    {
        var path = FileToolPaths.Resolve(pathArgument.GetValue(args), environment).Display;
        return new CallDescription { Summary = $"read {path}", Path = path };
    }

    public override Task<ToolExecutionResult> ExecuteAsync(AgentRunner<SubordinateContext> runner, ArgumentExtractionResult args, CancellationToken cancellationToken)
    {
        var environment = runner.ExecutionContext.Environment;
        var path = FileToolPaths.Resolve(pathArgument.GetValue(args), environment);

        if (path.Kind != ResolvedPathKind.Local)
        {
            return Task.FromResult(Error($"\"{path.Authored}\" is not a local file path."));
        }

        if (Directory.Exists(path.Path))
        {
            return Task.FromResult(Error($"{path.Path} is a directory. Use the shell tool to list it."));
        }

        if (!File.Exists(path.Path))
        {
            return Task.FromResult(Error($"{path.Path} does not exist."));
        }

        ImageFormat? format;

        try
        {
            format = ImageSnapshots.Detect(ReadHeader(path.Path!));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(Error($"{path.Path} could not be read: {ex.Message}"));
        }

        if (format != null)
        {
            if (!images.Enabled)
            {
                return Task.FromResult(Error($"{path.Path} is a {format.DisplayName} image, and the model of this session cannot view images."));
            }

            var length = new FileInfo(path.Path!).Length;

            if (length > MaxImageBytes)
            {
                return Task.FromResult(Error($"{path.Path} is {length.ToString(CultureInfo.InvariantCulture)} bytes; images larger than {MaxImageBytes.ToString(CultureInfo.InvariantCulture)} bytes cannot be shown."));
            }

            if (images.MaxImages is { } maxImages && environment.CountImagesInContext() >= maxImages)
            {
                return Task.FromResult(Error(ImageLimitReachedMessage));
            }

            byte[] bytes;

            try
            {
                bytes = File.ReadAllBytes(path.Path!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(Error($"{path.Path} could not be read: {ex.Message}"));
            }

            if (bytes.Length > MaxImageBytes)
            {
                return Task.FromResult(Error($"{path.Path} is {bytes.Length.ToString(CultureInfo.InvariantCulture)} bytes; images larger than {MaxImageBytes.ToString(CultureInfo.InvariantCulture)} bytes cannot be shown."));
            }

            var actualFormat = ImageSnapshots.Detect(bytes);

            if (actualFormat != null)
            {
                string reference;

                try
                {
                    reference = ImageSnapshots.Save(environment.ImagesDirectory, bytes, actualFormat);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    return Task.FromResult(Error($"{path.Path} could not be stored in the session directory: {ex.Message}"));
                }

                return Task.FromResult(Success($"{path.Path} is a {actualFormat.DisplayName} image ({bytes.Length.ToString(CultureInfo.InvariantCulture)} bytes). It is attached below.", [new ImageContent { Url = reference }]));
            }
        }

        string text;
        try
        {
            text = FileToolPaths.ReadText(path.Path!, out var isBinary);

            if (isBinary)
            {
                return Task.FromResult(Error(images.Enabled
                    ? $"{path.Path} looks like a binary file. Supported image formats are PNG, JPEG, GIF and WebP."
                    : $"{path.Path} looks like a binary file."));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(Error($"{path.Path} could not be read: {ex.Message}"));
        }

        var lines = text.Split('\n');
        var offset = offsetArgument.TryGetValue(args, out var requestedOffset) && requestedOffset > 0 ? requestedOffset : 1;
        var limit = limitArgument.TryGetValue(args, out var requestedLimit) && requestedLimit > 0 ? requestedLimit : DefaultLineLimit;
        var builder = new StringBuilder();
        var lastLine = Math.Min(lines.Length, offset - 1 + limit);

        if (offset > lines.Length)
        {
            return Task.FromResult(Success($"{path.Path} has {lines.Length} lines; offset {offset} is past the end."));
        }

        for (var index = offset - 1; index < lastLine; index++)
        {
            var line = lines[index].TrimEnd('\r');

            if (line.Length > MaxLineLength)
            {
                line = line[..MaxLineLength] + " [line truncated]";
            }

            builder.Append((index + 1).ToString(CultureInfo.InvariantCulture)).Append('\t').Append(line).Append('\n');

            if (builder.Length > MaxOutputCharacters)
            {
                builder.Append(CultureInfo.InvariantCulture, $"[output truncated at line {index + 1}; continue with offset {index + 2}]\n");
                return Task.FromResult(Success(builder.ToString()));
            }
        }

        if (lastLine < lines.Length)
        {
            builder.Append(CultureInfo.InvariantCulture, $"[{lines.Length - lastLine} more lines; continue with offset {lastLine + 1}]\n");
        }

        return Task.FromResult(Success(builder.ToString()));
    }

    private static byte[] ReadHeader(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var header = new byte[ImageSnapshots.HeaderLength];
        var read = stream.ReadAtLeast(header, header.Length, false);
        return read == header.Length ? header : header[..read];
    }
}

/// <summary>
///     Writes a whole file.
/// </summary>
public sealed class WriteFileToolHandler(AgentTool tool, StringArgument pathArgument, StringArgument contentArgument)
    : ToolHandler<SubordinateContext>.Plain(tool), IGatedTool
{
    public const string ToolName = "write_file";

    public static void Register(AgentToolRegistry<SubordinateContext> registry)
    {
        var writeTool = new ToolBuilder(ToolName)
            .WithDescription("Creates or overwrites a file with the given content. Parent directories are created as needed.")
            .WithRequiredStringArgument("path", "The file path, relative to the session workdir or absolute.", out var pathArg)
            .WithRequiredStringArgument("content", "The complete new content of the file.", out var contentArg)
            .Build();

        registry.RegisterTool(writeTool, new WriteFileToolHandler(writeTool, pathArg, contentArg));
    }

    public ActionClassification Classify(ArgumentExtractionResult args, ToolEnvironment environment)
    {
        return FileToolPaths.Classify(ToolName, pathArgument.GetValue(args), true, environment);
    }

    public string Fingerprint(ArgumentExtractionResult args, ToolEnvironment environment)
    {
        return $"{ToolName}\n{FileToolPaths.Resolve(pathArgument.GetValue(args), environment).Display}\n{FileToolPaths.StableHash(contentArgument.GetValue(args))}";
    }

    public CallDescription Describe(ArgumentExtractionResult args, ToolEnvironment environment)
    {
        var path = FileToolPaths.Resolve(pathArgument.GetValue(args), environment).Display;
        return new CallDescription { Summary = $"write {path} ({contentArgument.GetValue(args).Length} characters)", Path = path };
    }

    public override async Task<ToolExecutionResult> ExecuteAsync(AgentRunner<SubordinateContext> runner, ArgumentExtractionResult args, CancellationToken cancellationToken)
    {
        var environment = runner.ExecutionContext.Environment;
        var path = FileToolPaths.Resolve(pathArgument.GetValue(args), environment);

        if (path.Kind != ResolvedPathKind.Local)
        {
            return Error($"\"{path.Authored}\" is not a local file path.");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path.Path!)!);
            var content = FileToolPaths.MatchExistingLineEndings(path.Path!, contentArgument.GetValue(args));
            await File.WriteAllTextAsync(path.Path!, content, new UTF8Encoding(false), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error($"{path.Path} could not be written: {ex.Message}");
        }

        environment.FilesWritten.Add(path.Path!);
        return Success($"Wrote {path.Path}.");
    }
}

/// <summary>
///     Replaces exact text in a file.
/// </summary>
public sealed class EditFileToolHandler(AgentTool tool, StringArgument pathArgument, StringArgument oldArgument, StringArgument newArgument, BooleanArgument replaceAllArgument)
    : ToolHandler<SubordinateContext>.Plain(tool), IGatedTool
{
    public const string ToolName = "edit_file";

    public static void Register(AgentToolRegistry<SubordinateContext> registry)
    {
        var editTool = new ToolBuilder(ToolName)
            .WithDescription("Replaces exact text in a file. old_string must occur exactly once unless replace_all is true. Read the file first so the text matches exactly, including whitespace.")
            .WithRequiredStringArgument("path", "The file path, relative to the session workdir or absolute.", out var pathArg)
            .WithRequiredStringArgument("old_string", "The exact text to replace.", out var oldArg)
            .WithRequiredStringArgument("new_string", "The replacement text.", out var newArg)
            .WithBooleanArgument("replace_all", "Replace every occurrence instead of exactly one. Default false.", out var replaceAllArg)
            .Build();

        registry.RegisterTool(editTool, new EditFileToolHandler(editTool, pathArg, oldArg, newArg, replaceAllArg));
    }

    public ActionClassification Classify(ArgumentExtractionResult args, ToolEnvironment environment)
    {
        return FileToolPaths.Classify(ToolName, pathArgument.GetValue(args), true, environment);
    }

    public string Fingerprint(ArgumentExtractionResult args, ToolEnvironment environment)
    {
        return $"{ToolName}\n{FileToolPaths.Resolve(pathArgument.GetValue(args), environment).Display}\n{FileToolPaths.StableHash(oldArgument.GetValue(args))}\n{FileToolPaths.StableHash(newArgument.GetValue(args))}";
    }

    public CallDescription Describe(ArgumentExtractionResult args, ToolEnvironment environment)
    {
        var path = FileToolPaths.Resolve(pathArgument.GetValue(args), environment).Display;
        return new CallDescription { Summary = $"edit {path}", Path = path };
    }

    public override async Task<ToolExecutionResult> ExecuteAsync(AgentRunner<SubordinateContext> runner, ArgumentExtractionResult args, CancellationToken cancellationToken)
    {
        var environment = runner.ExecutionContext.Environment;
        var path = FileToolPaths.Resolve(pathArgument.GetValue(args), environment);
        var oldText = oldArgument.GetValue(args);
        var newText = newArgument.GetValue(args);
        var replaceAll = replaceAllArgument.TryGetValue(args, out var requested) && requested;

        if (path.Kind != ResolvedPathKind.Local || !File.Exists(path.Path))
        {
            return Error($"{path.Display} does not exist.");
        }

        if (oldText.Length == 0)
        {
            return Error("old_string is empty. Use write_file to create a file.");
        }

        try
        {
            var content = FileToolPaths.ReadText(path.Path!, out var isBinary, out var hasByteOrderMark);

            if (isBinary)
            {
                return Error($"{path.Path} looks like a binary file.");
            }

            var error = TryEdit(path.Path!, content, oldText, newText, replaceAll, out var updated, out var count);

            if (error is not null)
            {
                return Error(error);
            }

            await File.WriteAllTextAsync(path.Path!, updated, new UTF8Encoding(hasByteOrderMark), cancellationToken);
            environment.FilesWritten.Add(path.Path!);
            return Success($"Replaced {count} occurrence{(count == 1 ? string.Empty : "s")} in {path.Path}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error($"{path.Path} could not be edited: {ex.Message}");
        }
    }

    internal static string? TryEdit(string displayPath, string content, string oldText, string newText, bool replaceAll, out string updated, out int count)
    {
        updated = content;
        var matchedText = oldText;
        var writeCrlf = LineEndings.IsCrlfDominant(content);
        count = CountOccurrences(content, oldText);

        if (count == 0)
        {
            var crlfText = LineEndings.ToCrlf(oldText);

            if (!string.Equals(crlfText, oldText, StringComparison.Ordinal))
            {
                var crlfCount = CountOccurrences(content, crlfText);

                if (crlfCount > 0)
                {
                    matchedText = crlfText;
                    count = crlfCount;
                    writeCrlf = true;
                }
            }
        }

        if (count == 0)
        {
            return $"old_string was not found in {displayPath}. Read the file again and copy the text exactly.";
        }

        if (count > 1 && !replaceAll)
        {
            return $"old_string occurs {count} times in {displayPath}. Add surrounding context to make it unique, or set replace_all.";
        }

        var replacement = writeCrlf ? LineEndings.ToCrlf(newText) : newText;
        updated = content.Replace(matchedText, replacement, StringComparison.Ordinal);
        return null;
    }

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var index = 0;

        while ((index = content.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
