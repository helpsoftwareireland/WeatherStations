namespace WeatherAggregation.Core;

public sealed class InputException(string code, string message, long lineNumber, int status = 400)
    : Exception(message)
{
    public string Code { get; } = code;
    public long LineNumber { get; } = lineNumber;
    public int Status { get; } = status;
}
