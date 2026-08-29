using SamsungController.Core.Protocol;

namespace SamsungController.Automation.Macros;

public abstract record MacroStep;

public sealed record KeyStep(
    string Key,
    RemoteKeyAction Action = RemoteKeyAction.Click,
    int Repeat = 1,
    TimeSpan? Delay = null) : MacroStep;

public sealed record DelayStep(TimeSpan Duration) : MacroStep;

public sealed record CallMacroStep(
    string MacroName,
    int Repeat = 1) : MacroStep;

public sealed record MenuStep(string TargetNodeId) : MacroStep;
