namespace IteraPatch.Core.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using IteraPatch.Core.Interfaces;
using IteraPatch.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Extracts failing file paths, line numbers, error types, and stack traces
/// from pytest execution output using regular expressions, cross-referencing
/// against GitDeltaReport to isolate faults within uncommitted deltas.
/// </summary>
public sealed partial class RegexTraceParser : ITraceParser
{
    private static readonly Regex AnsiRegex = new(
        @"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])",
        RegexOptions.Compiled);

    private static readonly Regex PytestBlockHeaderRegex = new(
        @"^[ \t]*_{3,}[ \t]+(?<header>.+?)[ \t]+_{3,}[ \t]*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex PytestSectionSeparatorRegex = new(
        @"^[ \t]*={3,}[ \t]*(?:short test summary info|ERRORS|FAILURES|\d+\s+failed).*[ \t]*={3,}[ \t]*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex PytestFileFrameRegex = new(
        @"^[ \t]*(?<file>(?:[a-zA-Z]:[\\/])?[^\s:\r\n]+?\.(?:py|pyw)):\s*(?<line>\d+):(?:[ \t]*(?<tail>[^\r\n]*))?$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex PythonStandardFrameRegex = new(
        @"^[ \t]*(?:E\s+)?File\s+[""'](?<file>[^""'\r\n]+)[""'],\s+line\s+(?<line>\d+)(?:,[ \t]*in\s+(?<func>[^\r\n]*))?",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex ErrorLineRegex = new(
        @"^[ \t]*E[ \t]+(?<content>[^\r\n]+)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex CodeAssertRegex = new(
        @"^[ \t]*>[ \t]*(?<codeLine>assert[^\r\n]*)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex TracebackHeaderRegex = new(
        @"Traceback[ \t]+\(most recent call last\):",
        RegexOptions.Compiled);

    private static readonly Regex StandardExceptionLineRegex = new(
        @"^[ \t]*(?<type>[A-Za-z_][A-Za-z0-9_\.]*(?:Error|Exception|Exit|Interrupt)|AssertionError)(?::[ \t]*(?<msg>[^\r\n]*))?$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex PytestSummaryLineRegex = new(
        @"^[ \t]*(?:FAILED|ERROR)[ \t]+(?<target>[^\s\-]+)(?:[ \t]+-[ \t]+(?<summary>[^\r\n]*))?$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex PytestLineTracebackRegex = new(
        @"^[ \t]*(?<file>(?:[a-zA-Z]:[\\/])?[^\s:\r\n]+?\.(?:py|pyw)):\s*(?<line>\d+):\s*(?<type>[A-Za-z_][A-Za-z0-9_\.]*(?:Error|Exception|Exit|Interrupt)|AssertionError)(?::[ \t]*(?<msg>[^\r\n]*))?$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex PytestFailureExitSummaryRegex = new(
        @"^[ \t]*={3,}.*\b\d+\s+(?:failed|errors?).*={3,}[ \t]*$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private readonly ILogger<RegexTraceParser>? _logger;

    public RegexTraceParser(ILogger<RegexTraceParser>? logger = null)
    {
        _logger = logger;
    }

    public TraceParseResult Parse(string? stdout, string? stderr, GitDeltaReport? gitDelta)
    {
        var rawOutput = string.IsNullOrEmpty(stdout)
            ? (stderr ?? string.Empty)
            : string.IsNullOrEmpty(stderr)
                ? stdout
                : $"{stdout}\n{stderr}";

        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return new TraceParseResult(
                HasFailure: false,
                Failures: Array.Empty<ParsedFailure>(),
                RawOutput: rawOutput
            );
        }

        var cleanStdout = StripAnsi(stdout ?? string.Empty);
        var cleanStderr = StripAnsi(stderr ?? string.Empty);
        var combined = string.IsNullOrEmpty(cleanStdout)
            ? cleanStderr
            : string.IsNullOrEmpty(cleanStderr)
                ? cleanStdout
                : $"{cleanStdout}\n{cleanStderr}";

        var failures = new List<ParsedFailure>();
        var processedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Phase 1: Parse pytest failure / error blocks (separated by ____ test_name ____)
        ParsePytestBlocks(combined, gitDelta, failures, processedKeys);

        // Phase 2: Parse standalone Python tracebacks (Traceback (most recent call last):) if not in pytest blocks
        if (failures.Count == 0)
        {
            ParseStandaloneTracebacks(combined, gitDelta, failures, processedKeys);
        }

        // Phase 3: Parse one-line summary / failure lines (e.g. FAILED file.py::test - Error) if no failures found yet
        if (failures.Count == 0)
        {
            ParseSummaryLines(combined, gitDelta, failures, processedKeys);
        }

        // Phase 4: Parse line tracebacks (--tb=line) if no failures found yet
        if (failures.Count == 0)
        {
            ParseLineTracebacks(combined, gitDelta, failures, processedKeys);
        }

        // Phase 5: Defensive fallback if pytest exited with failed summary but no frame was parsed
        if (failures.Count == 0 && PytestFailureExitSummaryRegex.IsMatch(combined))
        {
            _logger?.LogWarning("Pytest reported failure exit summary but no structured frames were extracted.");
            failures.Add(new ParsedFailure(
                FailingFile: string.Empty,
                FailingLine: 0,
                ErrorType: "TestFailure",
                ErrorMessage: "Pytest reported test failure(s) in summary info.",
                StackTrace: combined.Trim(),
                IsWithinGitDelta: false
            ));
        }

        return new TraceParseResult(
            HasFailure: failures.Count > 0,
            Failures: failures,
            RawOutput: rawOutput
        );
    }

    private void ParsePytestBlocks(
        string text,
        GitDeltaReport? gitDelta,
        List<ParsedFailure> failures,
        HashSet<string> processedKeys)
    {
        var headerMatches = PytestBlockHeaderRegex.Matches(text);
        if (headerMatches.Count == 0)
        {
            return;
        }

        for (int i = 0; i < headerMatches.Count; i++)
        {
            var currentMatch = headerMatches[i];
            var testName = currentMatch.Groups["header"].Value.Trim();
            var startIndex = currentMatch.Index;

            int endIndex;
            if (i + 1 < headerMatches.Count)
            {
                endIndex = headerMatches[i + 1].Index;
            }
            else
            {
                var remainder = text.Substring(startIndex);
                var sepMatch = PytestSectionSeparatorRegex.Match(remainder);
                if (sepMatch.Success && sepMatch.Index > 0)
                {
                    endIndex = startIndex + sepMatch.Index;
                }
                else
                {
                    endIndex = text.Length;
                }
            }

            var blockLength = endIndex - startIndex;
            if (blockLength <= 0)
            {
                continue;
            }

            var blockText = text.Substring(startIndex, blockLength).Trim();
            if (string.IsNullOrWhiteSpace(blockText))
            {
                continue;
            }

            var failure = ProcessFailureBlock(testName, blockText, gitDelta);
            if (failure != null)
            {
                var key = $"{testName}|{failure.FailingFile}|{failure.FailingLine}|{failure.ErrorType}";
                if (processedKeys.Add(key))
                {
                    failures.Add(failure);
                }
            }
        }
    }

    private ParsedFailure? ProcessFailureBlock(string testName, string blockText, GitDeltaReport? gitDelta)
    {
        var frames = ExtractFramesFromText(blockText);
        var (errorType, errorMessage) = ExtractErrorDetailsFromBlock(blockText, frames);

        string failingFile;
        int failingLine;
        bool isWithinDelta;

        if (frames.Count > 0)
        {
            // Inspect frames from innermost (bottom) to outermost (top)
            // Select the innermost frame that lies within the Git delta if any
            TraceFrame? deltaMatchingFrame = null;
            string? matchedDeltaPath = null;

            for (int i = frames.Count - 1; i >= 0; i--)
            {
                var frame = frames[i];
                var delta = FindMatchingDelta(frame.FilePath, gitDelta);
                if (delta != null && IsLineWithinRanges(frame.LineNumber, delta.ChangedRanges))
                {
                    deltaMatchingFrame = frame;
                    matchedDeltaPath = delta.FilePath;
                    break;
                }
            }

            if (deltaMatchingFrame != null)
            {
                failingFile = matchedDeltaPath!;
                failingLine = deltaMatchingFrame.LineNumber;
                isWithinDelta = true;
            }
            else
            {
                // Fall back to the deepest/innermost frame of the stack trace
                var deepestFrame = frames[^1];
                var delta = FindMatchingDelta(deepestFrame.FilePath, gitDelta);
                failingFile = delta != null ? delta.FilePath : NormalizePath(deepestFrame.FilePath);
                failingLine = deepestFrame.LineNumber;
                isWithinDelta = false;
            }
        }
        else
        {
            // No frames extracted from block lines; attempt fallback from test name or header
            failingFile = ExtractFileFromTestName(testName);
            failingLine = 0;
            var delta = FindMatchingDelta(failingFile, gitDelta);
            if (delta != null)
            {
                failingFile = delta.FilePath;
            }
            isWithinDelta = delta != null && IsLineWithinRanges(failingLine, delta.ChangedRanges);
        }

        return new ParsedFailure(
            FailingFile: failingFile,
            FailingLine: failingLine,
            ErrorType: CleanErrorType(errorType),
            ErrorMessage: errorMessage,
            StackTrace: blockText,
            IsWithinGitDelta: isWithinDelta
        );
    }

    private void ParseStandaloneTracebacks(
        string text,
        GitDeltaReport? gitDelta,
        List<ParsedFailure> failures,
        HashSet<string> processedKeys)
    {
        var tracebackMatches = TracebackHeaderRegex.Matches(text);
        if (tracebackMatches.Count == 0)
        {
            return;
        }

        for (int i = 0; i < tracebackMatches.Count; i++)
        {
            var currentMatch = tracebackMatches[i];
            var startIndex = currentMatch.Index;
            int endIndex = (i + 1 < tracebackMatches.Count) ? tracebackMatches[i + 1].Index : text.Length;

            var tracebackSection = text.Substring(startIndex, endIndex - startIndex).Trim();
            if (string.IsNullOrWhiteSpace(tracebackSection))
            {
                continue;
            }

            var frames = ExtractFramesFromText(tracebackSection);
            if (frames.Count == 0)
            {
                continue;
            }

            var (errorType, errorMessage) = ExtractErrorDetailsFromTraceback(tracebackSection);

            string failingFile;
            int failingLine;
            bool isWithinDelta;

            TraceFrame? deltaMatchingFrame = null;
            string? matchedDeltaPath = null;

            for (int k = frames.Count - 1; k >= 0; k--)
            {
                var frame = frames[k];
                var delta = FindMatchingDelta(frame.FilePath, gitDelta);
                if (delta != null && IsLineWithinRanges(frame.LineNumber, delta.ChangedRanges))
                {
                    deltaMatchingFrame = frame;
                    matchedDeltaPath = delta.FilePath;
                    break;
                }
            }

            if (deltaMatchingFrame != null)
            {
                failingFile = matchedDeltaPath!;
                failingLine = deltaMatchingFrame.LineNumber;
                isWithinDelta = true;
            }
            else
            {
                var deepestFrame = frames[^1];
                var delta = FindMatchingDelta(deepestFrame.FilePath, gitDelta);
                failingFile = delta != null ? delta.FilePath : NormalizePath(deepestFrame.FilePath);
                failingLine = deepestFrame.LineNumber;
                isWithinDelta = false;
            }

            var key = $"traceback|{failingFile}|{failingLine}|{errorType}";
            if (processedKeys.Add(key))
            {
                failures.Add(new ParsedFailure(
                    FailingFile: failingFile,
                    FailingLine: failingLine,
                    ErrorType: CleanErrorType(errorType),
                    ErrorMessage: errorMessage,
                    StackTrace: tracebackSection,
                    IsWithinGitDelta: isWithinDelta
                ));
            }
        }
    }

    private void ParseSummaryLines(
        string text,
        GitDeltaReport? gitDelta,
        List<ParsedFailure> failures,
        HashSet<string> processedKeys)
    {
        var matches = PytestSummaryLineRegex.Matches(text);
        foreach (Match match in matches)
        {
            var target = match.Groups["target"].Value.Trim();
            var summary = match.Groups["summary"].Value.Trim();

            var filePath = target.Contains("::", StringComparison.Ordinal)
                ? target.Substring(0, target.IndexOf("::", StringComparison.Ordinal))
                : target;

            var (errorType, errorMessage) = ParseSummaryError(summary);

            var delta = FindMatchingDelta(filePath, gitDelta);
            var failingFile = delta != null ? delta.FilePath : NormalizePath(filePath);
            var failingLine = 1; // Default line when summary line does not contain line number
            var isWithinDelta = delta != null && IsLineWithinRanges(failingLine, delta.ChangedRanges);

            var key = $"{target}|{failingFile}|{errorType}";
            // Only add if not already covered by a detailed block
            bool alreadyCaptured = failures.Any(f =>
                string.Equals(f.FailingFile, failingFile, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(f.ErrorType, errorType, StringComparison.OrdinalIgnoreCase));

            if (!alreadyCaptured && processedKeys.Add(key))
            {
                failures.Add(new ParsedFailure(
                    FailingFile: failingFile,
                    FailingLine: failingLine,
                    ErrorType: CleanErrorType(errorType),
                    ErrorMessage: errorMessage,
                    StackTrace: match.Value.Trim(),
                    IsWithinGitDelta: isWithinDelta
                ));
            }
        }
    }

    private void ParseLineTracebacks(
        string text,
        GitDeltaReport? gitDelta,
        List<ParsedFailure> failures,
        HashSet<string> processedKeys)
    {
        var matches = PytestLineTracebackRegex.Matches(text);
        foreach (Match match in matches)
        {
            var rawFile = match.Groups["file"].Value.Trim();
            if (!int.TryParse(match.Groups["line"].Value, out var line))
            {
                continue;
            }
            var errorType = match.Groups["type"].Value.Trim();
            var errorMessage = match.Groups["msg"].Value.Trim();

            var delta = FindMatchingDelta(rawFile, gitDelta);
            var failingFile = delta != null ? delta.FilePath : NormalizePath(rawFile);
            var isWithinDelta = delta != null && IsLineWithinRanges(line, delta.ChangedRanges);

            var key = $"linetb|{failingFile}|{line}|{errorType}";
            if (processedKeys.Add(key))
            {
                failures.Add(new ParsedFailure(
                    FailingFile: failingFile,
                    FailingLine: line,
                    ErrorType: CleanErrorType(errorType),
                    ErrorMessage: errorMessage,
                    StackTrace: match.Value.Trim(),
                    IsWithinGitDelta: isWithinDelta
                ));
            }
        }
    }

    private static List<TraceFrame> ExtractFramesFromText(string text)
    {
        var frames = new List<TraceFrame>();

        // 1. Pytest format frames (file.py:line: tail)
        var pytestFrameMatches = PytestFileFrameRegex.Matches(text);
        foreach (Match match in pytestFrameMatches)
        {
            if (int.TryParse(match.Groups["line"].Value, out var line))
            {
                var file = match.Groups["file"].Value.Trim();
                var tail = match.Groups["tail"].Value.Trim();
                frames.Add(new TraceFrame(file, line, match.Index, tail));
            }
        }

        // 2. Python standard frames (File "file.py", line 123, in func)
        var stdFrameMatches = PythonStandardFrameRegex.Matches(text);
        foreach (Match match in stdFrameMatches)
        {
            if (int.TryParse(match.Groups["line"].Value, out var line))
            {
                var file = match.Groups["file"].Value.Trim();
                var func = match.Groups["func"].Value.Trim();
                frames.Add(new TraceFrame(file, line, match.Index, func));
            }
        }

        // Order frames chronologically from top to bottom of the traceback
        frames.Sort((a, b) => a.Position.CompareTo(b.Position));

        // Deduplicate adjacent identical frames
        var uniqueFrames = new List<TraceFrame>();
        foreach (var frame in frames)
        {
            if (uniqueFrames.Count == 0 ||
                !string.Equals(uniqueFrames[^1].FilePath, frame.FilePath, StringComparison.OrdinalIgnoreCase) ||
                uniqueFrames[^1].LineNumber != frame.LineNumber)
            {
                uniqueFrames.Add(frame);
            }
        }

        return uniqueFrames;
    }

    private static (string ErrorType, string ErrorMessage) ExtractErrorDetailsFromBlock(
        string blockText,
        IReadOnlyList<TraceFrame> frames)
    {
        string? errorType = null;
        string? errorMessage = null;

        // Check E error lines
        var eMatches = ErrorLineRegex.Matches(blockText);
        foreach (Match eMatch in eMatches)
        {
            var content = eMatch.Groups["content"].Value.Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            if (content.StartsWith("assert ", StringComparison.Ordinal) || content == "assert")
            {
                errorType = "AssertionError";
                errorMessage ??= content;
                break;
            }

            var colonIndex = content.IndexOf(':', StringComparison.Ordinal);
            if (colonIndex > 0)
            {
                var candidateType = content.Substring(0, colonIndex).Trim();
                var candidateMsg = content.Substring(colonIndex + 1).Trim();

                if (IsValidExceptionTypeName(candidateType))
                {
                    errorType = candidateType;
                    errorMessage = candidateMsg;
                    break;
                }
            }
            else if (IsValidExceptionTypeName(content))
            {
                errorType = content;
                errorMessage ??= string.Empty;
                break;
            }
        }

        // Fallback: check code assert line (> assert ...)
        if (string.IsNullOrEmpty(errorMessage))
        {
            var codeAssertMatch = CodeAssertRegex.Match(blockText);
            if (codeAssertMatch.Success)
            {
                errorMessage = codeAssertMatch.Groups["codeLine"].Value.Trim();
                errorType ??= "AssertionError";
            }
        }

        // Fallback: check tail of the last frame (e.g. tests/test_math.py:12: AssertionError)
        if (string.IsNullOrEmpty(errorType) && frames.Count > 0)
        {
            var tail = frames[^1].Tail;
            if (!string.IsNullOrWhiteSpace(tail))
            {
                var colonIndex = tail.IndexOf(':', StringComparison.Ordinal);
                if (colonIndex > 0)
                {
                    var candType = tail.Substring(0, colonIndex).Trim();
                    var candMsg = tail.Substring(colonIndex + 1).Trim();
                    if (IsValidExceptionTypeName(candType))
                    {
                        errorType = candType;
                        errorMessage ??= candMsg;
                    }
                }
                else if (IsValidExceptionTypeName(tail))
                {
                    errorType = tail;
                    errorMessage ??= string.Empty;
                }
            }
        }

        // Default to AssertionError for pytest test block failures
        errorType ??= "AssertionError";
        errorMessage ??= string.Empty;

        return (errorType, errorMessage);
    }

    private static (string ErrorType, string ErrorMessage) ExtractErrorDetailsFromTraceback(string tracebackSection)
    {
        var matches = StandardExceptionLineRegex.Matches(tracebackSection);
        if (matches.Count > 0)
        {
            var lastMatch = matches[^1];
            var type = lastMatch.Groups["type"].Value.Trim();
            var msg = lastMatch.Groups["msg"].Value.Trim();
            return (type, msg);
        }

        return ("Exception", string.Empty);
    }

    private static (string ErrorType, string ErrorMessage) ParseSummaryError(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return ("AssertionError", string.Empty);
        }

        if (summary.StartsWith("assert ", StringComparison.Ordinal) || summary == "assert")
        {
            return ("AssertionError", summary);
        }

        var colonIndex = summary.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex > 0)
        {
            var candidateType = summary.Substring(0, colonIndex).Trim();
            var candidateMsg = summary.Substring(colonIndex + 1).Trim();
            if (IsValidExceptionTypeName(candidateType))
            {
                return (candidateType, candidateMsg);
            }
        }
        else if (IsValidExceptionTypeName(summary))
        {
            return (summary, string.Empty);
        }

        return ("AssertionError", summary);
    }

    private static bool IsValidExceptionTypeName(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // Must be an identifier or dotted identifier
        var parts = text.Split('.');
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part) || !char.IsLetter(part[0]) && part[0] != '_')
            {
                return false;
            }

            if (!part.All(c => char.IsLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        // Exception types in Python typically end in Error, Exception, Exit, or are AssertionError
        return text.EndsWith("Error", StringComparison.Ordinal) ||
               text.EndsWith("Exception", StringComparison.Ordinal) ||
               text.EndsWith("Exit", StringComparison.Ordinal) ||
               text.EndsWith("Interrupt", StringComparison.Ordinal) ||
               text == "AssertionError" ||
               char.IsUpper(parts[^1][0]);
    }

    private static string CleanErrorType(string errorType)
    {
        if (string.IsNullOrWhiteSpace(errorType))
        {
            return "AssertionError";
        }

        var trimmed = errorType.Trim();
        if (trimmed.Contains('.', StringComparison.Ordinal))
        {
            return trimmed.Substring(trimmed.LastIndexOf('.') + 1);
        }

        return trimmed;
    }

    private static string ExtractFileFromTestName(string testName)
    {
        if (string.IsNullOrWhiteSpace(testName))
        {
            return string.Empty;
        }

        var words = testName.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
        {
            if (word.EndsWith(".py", StringComparison.OrdinalIgnoreCase) ||
                word.EndsWith(".pyw", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizePath(word);
            }
        }

        return string.Empty;
    }

    private static GitFileDelta? FindMatchingDelta(string tracePath, GitDeltaReport? gitDelta)
    {
        if (gitDelta == null || gitDelta.FileDeltas.Count == 0 || string.IsNullOrWhiteSpace(tracePath))
        {
            return null;
        }

        var normalizedTrace = NormalizePath(tracePath);

        // 1. Exact match (case-insensitive)
        foreach (var delta in gitDelta.FileDeltas)
        {
            var normalizedDelta = NormalizePath(delta.FilePath);
            if (string.Equals(normalizedTrace, normalizedDelta, StringComparison.OrdinalIgnoreCase))
            {
                return delta;
            }
        }

        // 2. Suffix match where tracePath ends with "/" + delta.FilePath
        // Select the longest matching delta path to avoid matching "math.py" when "src/math.py" exists
        GitFileDelta? bestMatch = null;
        int bestLength = -1;

        foreach (var delta in gitDelta.FileDeltas)
        {
            var normalizedDelta = NormalizePath(delta.FilePath);
            var suffix = "/" + normalizedDelta;
            if (normalizedTrace.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                if (normalizedDelta.Length > bestLength)
                {
                    bestMatch = delta;
                    bestLength = normalizedDelta.Length;
                }
            }
        }

        return bestMatch;
    }

    private static bool IsLineWithinRanges(int line, IReadOnlyList<LineRange> changedRanges)
    {
        if (line <= 0 || changedRanges == null || changedRanges.Count == 0)
        {
            return false;
        }

        foreach (var range in changedRanges)
        {
            if (line >= range.StartLine && line <= range.EndLine)
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Replace('\\', '/').Trim();

        // Strip leading ../ or ./
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized.Substring(2);
        }

        normalized = Regex.Replace(normalized, @"^(\.\.[/\\])+", string.Empty);

        // Strip common Docker working directory prefix
        if (normalized.StartsWith("/workspace/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring("/workspace/".Length);
        }

        return normalized;
    }

    private static string StripAnsi(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        return AnsiRegex.Replace(input, string.Empty);
    }

    private sealed record TraceFrame(string FilePath, int LineNumber, int Position, string? Tail);
}
