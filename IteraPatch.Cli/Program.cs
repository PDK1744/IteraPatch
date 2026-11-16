namespace IteraPatch.Cli;

using System;
using System.CommandLine;
using System.Threading.Tasks;
using IteraPatch.Cli.Commands;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = BuildCommandLine();
        var adjustedArgs = NormalizeArgs(args);
        var parseResult = rootCommand.Parse(adjustedArgs);

        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors)
            {
                Console.Error.WriteLine(error.Message);
            }
            return 2;
        }

        return await parseResult.InvokeAsync(new InvocationConfiguration());
    }

    public static RootCommand BuildCommandLine(RunCommand? runCommand = null)
    {
        var rootCommand = new RootCommand("IteraPatch - Git-native verification orchestrator");
        rootCommand.Add(runCommand ?? new RunCommand());
        return rootCommand;
    }

    public static string[] NormalizeArgs(string[]? args)
    {
        if (args == null || args.Length == 0)
        {
            return ["--help"];
        }

        // If invoked directly with run options (e.g. `iterapatch -r .`), prepend `run`
        if (args.Length > 0 && args[0].StartsWith('-') &&
            args[0] != "--help" && args[0] != "-h" && args[0] != "-?" && args[0] != "--version")
        {
            var newArgs = new string[args.Length + 1];
            newArgs[0] = "run";
            Array.Copy(args, 0, newArgs, 1, args.Length);
            return newArgs;
        }

        return args;
    }
}
