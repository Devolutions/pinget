namespace Devolutions.Pinget.PowerShell.Cmdlets.Common;

internal static class Constants
{
    public const uint CountLowerBound = 1;
    public const uint CountUpperBound = 1000;
    public const string GivenSet = "GivenSet";
    public const string FoundSet = "FoundSet";
    public const string DefaultSet = "DefaultSet";
    public const string OptionalSet = "OptionalSet";
    public const string IntegrityVersionSet = "IntegrityVersionSet";
    public const string IntegrityLatestSet = "IntegrityLatestSet";

    public static class PingetNouns
    {
        public const string PingetPackageManager = "PingetPackageManager";
        public const string Package = "PingetPackage";
        public const string Source = "PingetSource";
        public const string UserSetting = "PingetUserSetting";
        public const string Version = "PingetVersion";
        public const string Setting = "PingetSetting";
    }
}
