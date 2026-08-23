namespace SamsungController.Automation.Navigation;

public sealed class MenuDefinitionParseException : Exception
{
    public MenuDefinitionParseException(string message)
        : base(message)
    {
    }

    public MenuDefinitionParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
