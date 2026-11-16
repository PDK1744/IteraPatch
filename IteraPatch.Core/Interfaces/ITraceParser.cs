namespace IteraPatch.Core.Interfaces;

using IteraPatch.Core.Models;

public interface ITraceParser
{
    TraceParseResult Parse(string stdout, string stderr, GitDeltaReport gitDelta);
}
