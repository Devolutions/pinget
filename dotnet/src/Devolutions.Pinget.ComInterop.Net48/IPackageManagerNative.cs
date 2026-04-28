using System.Runtime.InteropServices;

namespace Devolutions.Pinget.ComInterop
{
    [ComImport]
    [Guid("347FA969-B65E-4282-9E51-2F892E11A322")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPackageManagerNative
    {
        [PreserveSig]
        int GetVersion([MarshalAs(UnmanagedType.BStr)] out string value);

        [PreserveSig]
        int GetDefaultAppRoot([MarshalAs(UnmanagedType.BStr)] out string value);

        [PreserveSig]
        int ListSourcesJson([MarshalAs(UnmanagedType.BStr)] out string value);

        [PreserveSig]
        int AddSource(
            [MarshalAs(UnmanagedType.BStr)] string name,
            [MarshalAs(UnmanagedType.BStr)] string argument,
            [MarshalAs(UnmanagedType.BStr)] string sourceType,
            [MarshalAs(UnmanagedType.BStr)] string trustLevel,
            int explicitSource,
            int priority);

        [PreserveSig]
        int RemoveSource([MarshalAs(UnmanagedType.BStr)] string name);

        [PreserveSig]
        int ResetSource([MarshalAs(UnmanagedType.BStr)] string name, int all);

        [PreserveSig]
        int EditSourceJson([MarshalAs(UnmanagedType.BStr)] string requestJson);

        [PreserveSig]
        int UpdateSourcesJson(
            [MarshalAs(UnmanagedType.BStr)] string sourceName,
            [MarshalAs(UnmanagedType.BStr)] out string value);

        [PreserveSig]
        int GetUserSettingsJson([MarshalAs(UnmanagedType.BStr)] out string value);

        [PreserveSig]
        int SetUserSettingsJson(
            [MarshalAs(UnmanagedType.BStr)] string settingsJson,
            int merge,
            [MarshalAs(UnmanagedType.BStr)] out string value);

        [PreserveSig]
        int TestUserSettingsJson(
            [MarshalAs(UnmanagedType.BStr)] string expectedJson,
            int ignoreNotSet,
            out int matched);

        [PreserveSig]
        int GetAdminSettingsJson([MarshalAs(UnmanagedType.BStr)] out string value);

        [PreserveSig]
        int SetAdminSetting(
            [MarshalAs(UnmanagedType.BStr)] string name,
            int enabled);

        [PreserveSig]
        int ResetAdminSetting(
            [MarshalAs(UnmanagedType.BStr)] string name,
            int resetAll);

        [PreserveSig]
        int EnsureSettingsFiles();

        [PreserveSig]
        int SearchJson(
            [MarshalAs(UnmanagedType.BStr)] string queryJson,
            [MarshalAs(UnmanagedType.BStr)] out string value);

        [PreserveSig]
        int SearchManifestsJson(
            [MarshalAs(UnmanagedType.BStr)] string queryJson,
            [MarshalAs(UnmanagedType.BStr)] out string value);

        [PreserveSig]
        int ListJson(
            [MarshalAs(UnmanagedType.BStr)] string queryJson,
            [MarshalAs(UnmanagedType.BStr)] out string value);

        [PreserveSig]
        int SearchVersionsJson(
            [MarshalAs(UnmanagedType.BStr)] string queryJson,
            [MarshalAs(UnmanagedType.BStr)] out string value);

        [PreserveSig]
        int ShowVersionsJson(
            [MarshalAs(UnmanagedType.BStr)] string queryJson,
            [MarshalAs(UnmanagedType.BStr)] out string value);

        [PreserveSig]
        int ShowJson(
            [MarshalAs(UnmanagedType.BStr)] string queryJson,
            [MarshalAs(UnmanagedType.BStr)] out string value);

        [PreserveSig]
        int WarmCacheJson(
            [MarshalAs(UnmanagedType.BStr)] string queryJson,
            [MarshalAs(UnmanagedType.BStr)] out string value);

        [PreserveSig]
        int ListPinsJson(
            [MarshalAs(UnmanagedType.BStr)] string sourceId,
            [MarshalAs(UnmanagedType.BStr)] out string value);

        [PreserveSig]
        int AddPin(
            [MarshalAs(UnmanagedType.BStr)] string packageId,
            [MarshalAs(UnmanagedType.BStr)] string version,
            [MarshalAs(UnmanagedType.BStr)] string sourceId,
            [MarshalAs(UnmanagedType.BStr)] string pinType);

        [PreserveSig]
        int RemovePin(
            [MarshalAs(UnmanagedType.BStr)] string packageId,
            [MarshalAs(UnmanagedType.BStr)] string sourceId,
            out int removed);

        [PreserveSig]
        int ResetPins([MarshalAs(UnmanagedType.BStr)] string sourceId);

        [PreserveSig]
        int DownloadInstallerJson(
            [MarshalAs(UnmanagedType.BStr)] string requestJson,
            [MarshalAs(UnmanagedType.BStr)] string downloadDirectory,
            [MarshalAs(UnmanagedType.BStr)] out string value);

        [PreserveSig]
        int InstallJson(
            [MarshalAs(UnmanagedType.BStr)] string requestJson,
            [MarshalAs(UnmanagedType.BStr)] out string value);

        [PreserveSig]
        int UninstallJson(
            [MarshalAs(UnmanagedType.BStr)] string requestJson,
            [MarshalAs(UnmanagedType.BStr)] out string value);

        [PreserveSig]
        int RepairJson(
            [MarshalAs(UnmanagedType.BStr)] string requestJson,
            [MarshalAs(UnmanagedType.BStr)] out string value);

        [PreserveSig]
        int GetLastError([MarshalAs(UnmanagedType.BStr)] out string value);
    }
}
