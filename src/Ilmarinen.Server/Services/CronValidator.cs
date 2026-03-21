using Cronos;

namespace Ilmarinen.Server.Services;

public static class CronValidator
{
    public static bool TryParse(string? expression, out CronExpression? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(expression)) return false;

        try
        {
            result = CronExpression.Parse(expression);
            return true;
        }
        catch (CronFormatException)
        {
            return false;
        }
    }
}
