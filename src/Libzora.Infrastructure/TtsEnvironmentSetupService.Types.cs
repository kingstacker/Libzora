using System.ComponentModel;
using System.Diagnostics;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Python command record and disposal.
/// </summary>
public sealed partial class TtsEnvironmentSetupService : ITtsEnvironmentSetup
{
    private sealed record PythonCommand(
        string Executable,
        IReadOnlyList<string> PrefixArguments,
        string? InterpreterPath = null);

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool TimedOut);
}
