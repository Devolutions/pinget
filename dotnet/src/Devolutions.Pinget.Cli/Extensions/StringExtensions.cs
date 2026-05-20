namespace Devolutions.Pinget.Cli.Extensions;

internal static class StringExtensions
{
    extension(string value)
    {
        public bool BooleanSetting =>
        value.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "on" or "yes" or "enabled" => true,
            "false" or "0" or "off" or "no" or "disabled" => false,
            _ => throw new InvalidOperationException($"Unsupported admin setting value: {value}")
        };
    }
}
