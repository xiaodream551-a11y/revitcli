using Xunit;

namespace RevitCli.Tests;

/// <summary>
/// Tests in this collection run sequentially and never in parallel with each other,
/// because they modify process-global state (Environment.ExitCode, Console.Out).
/// </summary>
[CollectionDefinition("Sequential", DisableParallelization = true)]
public class SequentialCollection { }
