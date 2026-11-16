namespace IteraPatch.Tests;

using System;
using System.Collections.Generic;
using IteraPatch.Core.Models;
using IteraPatch.Core.Services;
using Xunit;

public class RegexTraceParserTests
{
    private readonly RegexTraceParser _parser = new();

    [Fact]
    public void StandardPytestAssertion_ExtractsFailureDetailsAndMatchesGitDelta()
    {
        var stdout = """
        ============================= test session starts ==============================
        collected 1 item

        tests/test_math.py F                                                     [100%]

        =================================== FAILURES ===================================
        ___________________________________ test_add ___________________________________

            def test_add():
        >       assert add(2, 2) == 5
        E       AssertionError: assert 4 == 5

        tests/test_math.py:12: AssertionError
        =========================== short test summary info ============================
        FAILED tests/test_math.py::test_add - AssertionError: assert 4 == 5
        ============================== 1 failed in 0.05s ===============================
        """;

        var gitDelta = new GitDeltaReport(
            FileDeltas: new[]
            {
                new GitFileDelta(
                    FilePath: "tests/test_math.py",
                    ChangeType: GitChangeType.Modified,
                    ChangedRanges: new[] { new LineRange(10, 15) },
                    PatchContent: "diff content"
                )
            },
            CombinedDiff: "diff --git..."
        );

        var result = _parser.Parse(stdout, string.Empty, gitDelta);

        Assert.True(result.HasFailure);
        Assert.Single(result.Failures);

        var failure = result.Failures[0];
        Assert.Equal("tests/test_math.py", failure.FailingFile);
        Assert.Equal(12, failure.FailingLine);
        Assert.Equal("AssertionError", failure.ErrorType);
        Assert.Equal("assert 4 == 5", failure.ErrorMessage);
        Assert.True(failure.IsWithinGitDelta);
        Assert.Contains("test_add", failure.StackTrace);
    }

    [Fact]
    public void StandardPytestAssertion_AssertKeywordFormat_ExtractsErrorMessage()
    {
        var stdout = """
        =================================== FAILURES ===================================
        ___________________________________ test_add ___________________________________

            def test_add():
        >       assert math_mod.add(2, 2) == 4
        E       assert 5 == 4
        E        +  where 5 = <function add at 0x709e74371900>(2, 2)
        E        +    where <function add at 0x709e74371900> = math_mod.add

        /workspace/tests/test_math.py:3: AssertionError
        ============================== 1 failed in 0.03s ===============================
        """;

        var gitDelta = new GitDeltaReport(
            FileDeltas: new[]
            {
                new GitFileDelta(
                    FilePath: "tests/test_math.py",
                    ChangeType: GitChangeType.Modified,
                    ChangedRanges: new[] { new LineRange(1, 10) },
                    PatchContent: "diff content"
                )
            },
            CombinedDiff: "diff --git..."
        );

        var result = _parser.Parse(stdout, null, gitDelta);

        Assert.True(result.HasFailure);
        Assert.Single(result.Failures);

        var failure = result.Failures[0];
        Assert.Equal("tests/test_math.py", failure.FailingFile);
        Assert.Equal(3, failure.FailingLine);
        Assert.Equal("AssertionError", failure.ErrorType);
        Assert.Equal("assert 5 == 4", failure.ErrorMessage);
        Assert.True(failure.IsWithinGitDelta);
    }

    [Fact]
    public void StandardPytestAssertion_OutsideGitDelta_ReturnsIsWithinGitDeltaFalse()
    {
        var stdout = """
        =================================== FAILURES ===================================
        ___________________________________ test_add ___________________________________

            def test_add():
        >       assert add(2, 2) == 5
        E       AssertionError: assert 4 == 5

        tests/test_math.py:12: AssertionError
        ============================== 1 failed in 0.05s ===============================
        """;

        // Git delta modifies lines 50..60, but failure is at line 12
        var gitDelta = new GitDeltaReport(
            FileDeltas: new[]
            {
                new GitFileDelta(
                    FilePath: "tests/test_math.py",
                    ChangeType: GitChangeType.Modified,
                    ChangedRanges: new[] { new LineRange(50, 60) },
                    PatchContent: "diff content"
                )
            },
            CombinedDiff: "diff --git..."
        );

        var result = _parser.Parse(stdout, string.Empty, gitDelta);

        Assert.True(result.HasFailure);
        Assert.Single(result.Failures);

        var failure = result.Failures[0];
        Assert.Equal("tests/test_math.py", failure.FailingFile);
        Assert.Equal(12, failure.FailingLine);
        Assert.Equal("AssertionError", failure.ErrorType);
        Assert.False(failure.IsWithinGitDelta);
    }

    [Fact]
    public void PythonException_ExtractsExceptionTypeAndMessage()
    {
        var stdout = """
        =================================== FAILURES ===================================
        __________________________________ test_divide _________________________________

            def test_divide():
        >       divide(10, 0)

        tests/test_calc.py:5: 
        _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ 

            def divide(a, b):
        >       return a / b
        E       ZeroDivisionError: division by zero

        src/calculator.py:15: ZeroDivisionError
        =========================== short test summary info ============================
        FAILED tests/test_calc.py::test_divide - ZeroDivisionError: division by zero
        ============================== 1 failed in 0.04s ===============================
        """;

        var gitDelta = new GitDeltaReport(
            FileDeltas: new[]
            {
                new GitFileDelta(
                    FilePath: "src/calculator.py",
                    ChangeType: GitChangeType.Modified,
                    ChangedRanges: new[] { new LineRange(10, 20) },
                    PatchContent: "diff content"
                )
            },
            CombinedDiff: "diff --git..."
        );

        var result = _parser.Parse(stdout, null, gitDelta);

        Assert.True(result.HasFailure);
        Assert.Single(result.Failures);

        var failure = result.Failures[0];
        Assert.Equal("src/calculator.py", failure.FailingFile);
        Assert.Equal(15, failure.FailingLine);
        Assert.Equal("ZeroDivisionError", failure.ErrorType);
        Assert.Equal("division by zero", failure.ErrorMessage);
        Assert.True(failure.IsWithinGitDelta);
    }

    [Fact]
    public void MultiFileTraceback_SelectsInnermostMatchingGitDeltaFrame_MiddleFrameMatched()
    {
        // 3-level stack trace: test_nested -> level1 -> level2
        var stdout = """
        =================================== FAILURES ===================================
        _________________________________ test_nested __________________________________

            def test_nested():
        >       call_l1()

        tests/test_levels.py:4: 
        _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ 
        src/level1.py:10: in call_l1
            return call_l2()
        _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ 

            def call_l2():
        >       raise KeyError("missing_key")
        E       KeyError: 'missing_key'

        src/level2.py:25: KeyError
        ============================== 1 failed in 0.05s ===============================
        """;

        // Git delta has uncommitted edits in src/level1.py at lines 8..15
        var gitDelta = new GitDeltaReport(
            FileDeltas: new[]
            {
                new GitFileDelta(
                    FilePath: "src/level1.py",
                    ChangeType: GitChangeType.Modified,
                    ChangedRanges: new[] { new LineRange(8, 15) },
                    PatchContent: "diff content"
                )
            },
            CombinedDiff: "diff --git..."
        );

        var result = _parser.Parse(stdout, null, gitDelta);

        Assert.True(result.HasFailure);
        Assert.Single(result.Failures);

        var failure = result.Failures[0];
        // Must isolate fault to src/level1.py:10 because it is the frame within the uncommitted Git delta
        Assert.Equal("src/level1.py", failure.FailingFile);
        Assert.Equal(10, failure.FailingLine);
        Assert.Equal("KeyError", failure.ErrorType);
        Assert.Equal("'missing_key'", failure.ErrorMessage);
        Assert.True(failure.IsWithinGitDelta);
    }

    [Fact]
    public void MultiFileTraceback_SelectsInnermostMatchingGitDeltaFrame_DeepestFrameMatched()
    {
        var stdout = """
        =================================== FAILURES ===================================
        _________________________________ test_nested __________________________________

            def test_nested():
        >       call_l1()

        tests/test_levels.py:4: 
        _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ 
        src/level1.py:10: in call_l1
            return call_l2()
        _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ 

            def call_l2():
        >       raise KeyError("missing_key")
        E       KeyError: 'missing_key'

        src/level2.py:25: KeyError
        ============================== 1 failed in 0.05s ===============================
        """;

        // Both level1 and level2 modified in Git delta; should select innermost matching frame (level2)
        var gitDelta = new GitDeltaReport(
            FileDeltas: new[]
            {
                new GitFileDelta(
                    FilePath: "src/level1.py",
                    ChangeType: GitChangeType.Modified,
                    ChangedRanges: new[] { new LineRange(1, 20) },
                    PatchContent: "diff"
                ),
                new GitFileDelta(
                    FilePath: "src/level2.py",
                    ChangeType: GitChangeType.Modified,
                    ChangedRanges: new[] { new LineRange(20, 30) },
                    PatchContent: "diff"
                )
            },
            CombinedDiff: "diff --git..."
        );

        var result = _parser.Parse(stdout, null, gitDelta);

        Assert.True(result.HasFailure);
        Assert.Single(result.Failures);

        var failure = result.Failures[0];
        Assert.Equal("src/level2.py", failure.FailingFile);
        Assert.Equal(25, failure.FailingLine);
        Assert.Equal("KeyError", failure.ErrorType);
        Assert.True(failure.IsWithinGitDelta);
    }

    [Fact]
    public void MultiFileTraceback_NoFrameInGitDelta_SelectsInnermostFrameWithIsWithinDeltaFalse()
    {
        var stdout = """
        =================================== FAILURES ===================================
        _________________________________ test_nested __________________________________

            def test_nested():
        >       call_l1()

        tests/test_levels.py:4: 
        _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ 
        src/level1.py:10: in call_l1
            return call_l2()
        _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ _ 

            def call_l2():
        >       raise KeyError("missing_key")
        E       KeyError: 'missing_key'

        src/level2.py:25: KeyError
        ============================== 1 failed in 0.05s ===============================
        """;

        // Git delta has modifications in unrelated file
        var gitDelta = new GitDeltaReport(
            FileDeltas: new[]
            {
                new GitFileDelta(
                    FilePath: "src/unrelated.py",
                    ChangeType: GitChangeType.Modified,
                    ChangedRanges: new[] { new LineRange(1, 10) },
                    PatchContent: "diff"
                )
            },
            CombinedDiff: "diff --git..."
        );

        var result = _parser.Parse(stdout, null, gitDelta);

        Assert.True(result.HasFailure);
        Assert.Single(result.Failures);

        var failure = result.Failures[0];
        Assert.Equal("src/level2.py", failure.FailingFile);
        Assert.Equal(25, failure.FailingLine);
        Assert.Equal("KeyError", failure.ErrorType);
        Assert.False(failure.IsWithinGitDelta);
    }

    [Fact]
    public void MultipleFailuresInSingleRun_ExtractsAllFailures()
    {
        var stdout = """
        =================================== FAILURES ===================================
        ___________________________________ test_add ___________________________________

            def test_add():
        >       assert add(2, 2) == 5
        E       AssertionError: assert 4 == 5

        tests/test_math.py:12: AssertionError
        ___________________________________ test_boom __________________________________

            def test_boom():
        >       raise ValueError("invalid value")
        E       ValueError: invalid value

        tests/test_boom.py:8: ValueError
        =========================== short test summary info ============================
        FAILED tests/test_math.py::test_add - AssertionError: assert 4 == 5
        FAILED tests/test_boom.py::test_boom - ValueError: invalid value
        ============================== 2 failed in 0.08s ===============================
        """;

        var gitDelta = new GitDeltaReport(
            FileDeltas: new[]
            {
                new GitFileDelta(
                    FilePath: "tests/test_math.py",
                    ChangeType: GitChangeType.Modified,
                    ChangedRanges: new[] { new LineRange(10, 15) },
                    PatchContent: "diff"
                )
            },
            CombinedDiff: "diff"
        );

        var result = _parser.Parse(stdout, null, gitDelta);

        Assert.True(result.HasFailure);
        Assert.Equal(2, result.Failures.Count);

        var f1 = result.Failures[0];
        Assert.Equal("tests/test_math.py", f1.FailingFile);
        Assert.Equal(12, f1.FailingLine);
        Assert.Equal("AssertionError", f1.ErrorType);
        Assert.Equal("assert 4 == 5", f1.ErrorMessage);
        Assert.True(f1.IsWithinGitDelta);

        var f2 = result.Failures[1];
        Assert.Equal("tests/test_boom.py", f2.FailingFile);
        Assert.Equal(8, f2.FailingLine);
        Assert.Equal("ValueError", f2.ErrorType);
        Assert.Equal("invalid value", f2.ErrorMessage);
        Assert.False(f2.IsWithinGitDelta);
    }

    [Fact]
    public void PytestCollectionError_SyntaxError_ExtractsDetails()
    {
        var stdout = """
        ==================================== ERRORS ====================================
        _______________________ ERROR collecting test_syntax.py ________________________
        /usr/lib/python3.11/ast.py:46: in parse
            return compile(source, filename, mode, flags,
        E     File "tests/test_syntax.py", line 1
        E       def test_syntax(
        E                      ^
        E   SyntaxError: '(' was never closed
        =========================== short test summary info ============================
        ERROR tests/test_syntax.py
        =============================== 1 error in 0.12s ===============================
        """;

        var gitDelta = new GitDeltaReport(
            FileDeltas: new[]
            {
                new GitFileDelta(
                    FilePath: "tests/test_syntax.py",
                    ChangeType: GitChangeType.Modified,
                    ChangedRanges: new[] { new LineRange(1, 5) },
                    PatchContent: "diff"
                )
            },
            CombinedDiff: "diff"
        );

        var result = _parser.Parse(stdout, null, gitDelta);

        Assert.True(result.HasFailure);
        Assert.Single(result.Failures);

        var failure = result.Failures[0];
        Assert.Equal("tests/test_syntax.py", failure.FailingFile);
        Assert.Equal(1, failure.FailingLine);
        Assert.Equal("SyntaxError", failure.ErrorType);
        Assert.Equal("'(' was never closed", failure.ErrorMessage);
        Assert.True(failure.IsWithinGitDelta);
    }

    [Fact]
    public void PytestShortTracebackFormat_TbShort_ExtractsFailureDetails()
    {
        var stdout = """
        =================================== FAILURES ===================================
        ___________________________________ test_add ___________________________________
        tests/test_math.py:12: in test_add
            assert add(2, 2) == 5
        E   AssertionError: assert 4 == 5
        =========================== short test summary info ============================
        FAILED tests/test_math.py::test_add - AssertionError: assert 4 == 5
        ============================== 1 failed in 0.02s ===============================
        """;

        var gitDelta = new GitDeltaReport(
            FileDeltas: new[]
            {
                new GitFileDelta(
                    FilePath: "tests/test_math.py",
                    ChangeType: GitChangeType.Modified,
                    ChangedRanges: new[] { new LineRange(10, 15) },
                    PatchContent: "diff"
                )
            },
            CombinedDiff: "diff"
        );

        var result = _parser.Parse(stdout, null, gitDelta);

        Assert.True(result.HasFailure);
        Assert.Single(result.Failures);

        var failure = result.Failures[0];
        Assert.Equal("tests/test_math.py", failure.FailingFile);
        Assert.Equal(12, failure.FailingLine);
        Assert.Equal("AssertionError", failure.ErrorType);
        Assert.Equal("assert 4 == 5", failure.ErrorMessage);
        Assert.True(failure.IsWithinGitDelta);
    }

    [Fact]
    public void PytestNativeTraceback_ExtractsDetails()
    {
        var stderr = """
        Traceback (most recent call last):
          File "tests/test_calc.py", line 10, in test_divide
            result = divide(10, 0)
          File "src/calculator.py", line 4, in divide
            return a / b
        ZeroDivisionError: division by zero
        """;

        var gitDelta = new GitDeltaReport(
            FileDeltas: new[]
            {
                new GitFileDelta(
                    FilePath: "src/calculator.py",
                    ChangeType: GitChangeType.Modified,
                    ChangedRanges: new[] { new LineRange(1, 10) },
                    PatchContent: "diff"
                )
            },
            CombinedDiff: "diff"
        );

        var result = _parser.Parse(null, stderr, gitDelta);

        Assert.True(result.HasFailure);
        Assert.Single(result.Failures);

        var failure = result.Failures[0];
        Assert.Equal("src/calculator.py", failure.FailingFile);
        Assert.Equal(4, failure.FailingLine);
        Assert.Equal("ZeroDivisionError", failure.ErrorType);
        Assert.Equal("division by zero", failure.ErrorMessage);
        Assert.True(failure.IsWithinGitDelta);
    }

    [Fact]
    public void PytestSummaryLineFallback_WhenNoDetailedBlocks()
    {
        var stdout = """
        =========================== short test summary info ============================
        FAILED tests/test_calc.py::test_div - ZeroDivisionError: division by zero
        ============================== 1 failed in 0.02s ===============================
        """;

        var gitDelta = new GitDeltaReport(
            FileDeltas: new[]
            {
                new GitFileDelta(
                    FilePath: "tests/test_calc.py",
                    ChangeType: GitChangeType.Modified,
                    ChangedRanges: new[] { new LineRange(1, 20) },
                    PatchContent: "diff"
                )
            },
            CombinedDiff: "diff"
        );

        var result = _parser.Parse(stdout, null, gitDelta);

        Assert.True(result.HasFailure);
        Assert.Single(result.Failures);

        var failure = result.Failures[0];
        Assert.Equal("tests/test_calc.py", failure.FailingFile);
        Assert.Equal("ZeroDivisionError", failure.ErrorType);
        Assert.Equal("division by zero", failure.ErrorMessage);
        Assert.True(failure.IsWithinGitDelta);
    }

    [Fact]
    public void PytestLineTraceback_ExtractsFailureDetails()
    {
        var stdout = """
        tests/test_math.py:12: AssertionError: assert 4 == 5
        """;

        var gitDelta = new GitDeltaReport(
            FileDeltas: new[]
            {
                new GitFileDelta(
                    FilePath: "tests/test_math.py",
                    ChangeType: GitChangeType.Modified,
                    ChangedRanges: new[] { new LineRange(10, 20) },
                    PatchContent: "diff"
                )
            },
            CombinedDiff: "diff"
        );

        var result = _parser.Parse(stdout, null, gitDelta);

        Assert.True(result.HasFailure);
        Assert.Single(result.Failures);

        var failure = result.Failures[0];
        Assert.Equal("tests/test_math.py", failure.FailingFile);
        Assert.Equal(12, failure.FailingLine);
        Assert.Equal("AssertionError", failure.ErrorType);
        Assert.Equal("assert 4 == 5", failure.ErrorMessage);
        Assert.True(failure.IsWithinGitDelta);
    }

    [Fact]
    public void PassingTests_ReturnsHasFailureFalseAndEmptyFailures()
    {
        var stdout = """
        ============================= test session starts ==============================
        collected 5 items

        tests/test_math.py .....                                                 [100%]

        ============================== 5 passed in 0.05s ===============================
        """;

        var gitDelta = new GitDeltaReport(
            FileDeltas: new[]
            {
                new GitFileDelta("src/math.py", GitChangeType.Modified, new[] { new LineRange(1, 10) }, "diff")
            },
            CombinedDiff: "diff"
        );

        var result = _parser.Parse(stdout, null, gitDelta);

        Assert.False(result.HasFailure);
        Assert.Empty(result.Failures);
        Assert.Equal(stdout, result.RawOutput);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "   \t\n")]
    public void EmptyOrWhitespaceOutput_ReturnsHasFailureFalse(string? stdout, string? stderr)
    {
        var result = _parser.Parse(stdout, stderr, null);

        Assert.False(result.HasFailure);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void AnsiEscapeCodesInOutput_SuccessfullyStrippedAndParsed()
    {
        var stdoutWithAnsi = "\u001b[1m============================= test session starts ==============================\u001b[0m\n" +
                             "=================================== FAILURES ===================================\n" +
                             "___________________________________ \u001b[31m\u001b[1mtest_add\u001b[0m ___________________________________\n\n" +
                             "    def test_add():\n" +
                             ">       assert add(2, 2) == 5\n" +
                             "\u001b[31mE       AssertionError: assert 4 == 5\u001b[0m\n\n" +
                             "tests/test_math.py:12: AssertionError\n" +
                             "============================== \u001b[31m1 failed\u001b[0m in 0.05s ===============================\n";

        var gitDelta = new GitDeltaReport(
            FileDeltas: new[]
            {
                new GitFileDelta(
                    FilePath: "tests/test_math.py",
                    ChangeType: GitChangeType.Modified,
                    ChangedRanges: new[] { new LineRange(10, 15) },
                    PatchContent: "diff"
                )
            },
            CombinedDiff: "diff"
        );

        var result = _parser.Parse(stdoutWithAnsi, null, gitDelta);

        Assert.True(result.HasFailure);
        Assert.Single(result.Failures);

        var failure = result.Failures[0];
        Assert.Equal("tests/test_math.py", failure.FailingFile);
        Assert.Equal(12, failure.FailingLine);
        Assert.Equal("AssertionError", failure.ErrorType);
        Assert.Equal("assert 4 == 5", failure.ErrorMessage);
        Assert.True(failure.IsWithinGitDelta);
    }

    [Fact]
    public void PathNormalization_HandlesDockerWorkspaceAndWindowsPaths()
    {
        var stdout = """
        =================================== FAILURES ===================================
        ___________________________________ test_work __________________________________

            def test_work():
        >       run()

        /workspace/src/deep/module.py:42: in run
            execute()
        E   RuntimeError: failed execution

        /workspace/src/deep/module.py:42: RuntimeError
        ============================== 1 failed in 0.05s ===============================
        """;

        var gitDelta = new GitDeltaReport(
            FileDeltas: new[]
            {
                new GitFileDelta(
                    FilePath: "src/deep/module.py",
                    ChangeType: GitChangeType.Modified,
                    ChangedRanges: new[] { new LineRange(40, 50) },
                    PatchContent: "diff"
                )
            },
            CombinedDiff: "diff"
        );

        var result = _parser.Parse(stdout, null, gitDelta);

        Assert.True(result.HasFailure);
        Assert.Single(result.Failures);

        var failure = result.Failures[0];
        Assert.Equal("src/deep/module.py", failure.FailingFile);
        Assert.Equal(42, failure.FailingLine);
        Assert.Equal("RuntimeError", failure.ErrorType);
        Assert.True(failure.IsWithinGitDelta);
    }

    [Fact]
    public void NullGitDelta_GracefullyHandledWithoutExceptions()
    {
        var stdout = """
        =================================== FAILURES ===================================
        ___________________________________ test_add ___________________________________
        tests/test_math.py:12: AssertionError: assert 4 == 5
        ============================== 1 failed in 0.05s ===============================
        """;

        var result = _parser.Parse(stdout, null, null);

        Assert.True(result.HasFailure);
        Assert.Single(result.Failures);
        Assert.False(result.Failures[0].IsWithinGitDelta);
    }

    [Fact]
    public void BothStdoutAndStderr_CombinesRawOutputAndExtractsFailureFromEither()
    {
        var stdout = "============================= test session starts ==============================\n";
        var stderr = """
        Traceback (most recent call last):
          File "tests/test_err.py", line 7, in test_err
            raise TypeError("bad type")
        TypeError: bad type
        """;

        var gitDelta = new GitDeltaReport(
            FileDeltas: new[]
            {
                new GitFileDelta("tests/test_err.py", GitChangeType.Modified, new[] { new LineRange(5, 10) }, "diff")
            },
            CombinedDiff: "diff"
        );

        var result = _parser.Parse(stdout, stderr, gitDelta);

        Assert.True(result.HasFailure);
        Assert.Single(result.Failures);
        Assert.Equal("tests/test_err.py", result.Failures[0].FailingFile);
        Assert.Equal(7, result.Failures[0].FailingLine);
        Assert.Equal("TypeError", result.Failures[0].ErrorType);
        Assert.Equal("bad type", result.Failures[0].ErrorMessage);
        Assert.True(result.Failures[0].IsWithinGitDelta);
        Assert.Contains(stdout.Trim(), result.RawOutput);
        Assert.Contains("TypeError: bad type", result.RawOutput);
    }
}
