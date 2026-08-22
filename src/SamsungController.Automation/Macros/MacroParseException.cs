namespace SamsungController.Automation.Macros;

public sealed class MacroParseException : Exception
{
    public MacroParseException(string message)
        : base(message)
    {
    }

    public MacroParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
