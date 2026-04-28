using System;
using System.Runtime.InteropServices;

namespace Devolutions.Pinget.ComInterop
{
    public sealed class PingetPackageManager : IDisposable
    {
        internal static readonly Guid NativeInterfaceId = new Guid("347FA969-B65E-4282-9E51-2F892E11A322");

        private IPackageManagerNative? _native;

        internal PingetPackageManager(IPackageManagerNative native)
        {
            _native = native ?? throw new ArgumentNullException(nameof(native));
        }

        public string GetVersion()
        {
            var native = GetNative();
            var hr = native.GetVersion(out var value);
            ThrowIfFailed(native, hr);
            return value;
        }

        public string GetDefaultAppRoot()
        {
            var native = GetNative();
            var hr = native.GetDefaultAppRoot(out var value);
            ThrowIfFailed(native, hr);
            return value;
        }

        public string ListSourcesJson()
        {
            var native = GetNative();
            var hr = native.ListSourcesJson(out var value);
            ThrowIfFailed(native, hr);
            return value;
        }

        public void AddSource(string name, string argument, string? sourceType, string? trustLevel, bool explicitSource, int priority)
        {
            var native = GetNative();
            var hr = native.AddSource(
                name,
                argument,
                sourceType ?? string.Empty,
                trustLevel ?? string.Empty,
                explicitSource ? 1 : 0,
                priority);
            ThrowIfFailed(native, hr);
        }

        public void RemoveSource(string name)
        {
            var native = GetNative();
            var hr = native.RemoveSource(name);
            ThrowIfFailed(native, hr);
        }

        public void ResetSource(string? name, bool all)
        {
            var native = GetNative();
            var hr = native.ResetSource(name ?? string.Empty, all ? 1 : 0);
            ThrowIfFailed(native, hr);
        }

        public void EditSourceJson(string requestJson)
        {
            var native = GetNative();
            var hr = native.EditSourceJson(requestJson);
            ThrowIfFailed(native, hr);
        }

        public string UpdateSourcesJson(string? sourceName) =>
            InvokeJson((IPackageManagerNative native, out string value) => native.UpdateSourcesJson(sourceName ?? string.Empty, out value));

        public string GetUserSettingsJson() =>
            InvokeJson((IPackageManagerNative native, out string value) => native.GetUserSettingsJson(out value));

        public string SetUserSettingsJson(string settingsJson, bool merge) =>
            InvokeJson((IPackageManagerNative native, out string value) => native.SetUserSettingsJson(settingsJson, merge ? 1 : 0, out value));

        public bool TestUserSettingsJson(string expectedJson, bool ignoreNotSet)
        {
            var native = GetNative();
            var hr = native.TestUserSettingsJson(expectedJson, ignoreNotSet ? 1 : 0, out var matched);
            ThrowIfFailed(native, hr);
            return matched != 0;
        }

        public string GetAdminSettingsJson() =>
            InvokeJson((IPackageManagerNative native, out string value) => native.GetAdminSettingsJson(out value));

        public void SetAdminSetting(string name, bool enabled)
        {
            var native = GetNative();
            var hr = native.SetAdminSetting(name, enabled ? 1 : 0);
            ThrowIfFailed(native, hr);
        }

        public void ResetAdminSetting(string? name, bool resetAll)
        {
            var native = GetNative();
            var hr = native.ResetAdminSetting(name ?? string.Empty, resetAll ? 1 : 0);
            ThrowIfFailed(native, hr);
        }

        public void EnsureSettingsFiles()
        {
            var native = GetNative();
            var hr = native.EnsureSettingsFiles();
            ThrowIfFailed(native, hr);
        }

        public string SearchJson(string queryJson) =>
            InvokeJson((IPackageManagerNative native, out string value) => native.SearchJson(queryJson, out value));

        public string SearchManifestsJson(string queryJson) =>
            InvokeJson((IPackageManagerNative native, out string value) => native.SearchManifestsJson(queryJson, out value));

        public string ListJson(string queryJson) =>
            InvokeJson((IPackageManagerNative native, out string value) => native.ListJson(queryJson, out value));

        public string SearchVersionsJson(string queryJson) =>
            InvokeJson((IPackageManagerNative native, out string value) => native.SearchVersionsJson(queryJson, out value));

        public string ShowVersionsJson(string queryJson) =>
            InvokeJson((IPackageManagerNative native, out string value) => native.ShowVersionsJson(queryJson, out value));

        public string ShowJson(string queryJson) =>
            InvokeJson((IPackageManagerNative native, out string value) => native.ShowJson(queryJson, out value));

        public string WarmCacheJson(string queryJson) =>
            InvokeJson((IPackageManagerNative native, out string value) => native.WarmCacheJson(queryJson, out value));

        public string ListPinsJson(string? sourceId) =>
            InvokeJson((IPackageManagerNative native, out string value) => native.ListPinsJson(sourceId ?? string.Empty, out value));

        public void AddPin(string packageId, string version, string sourceId, string pinType)
        {
            var native = GetNative();
            var hr = native.AddPin(packageId, version, sourceId, pinType);
            ThrowIfFailed(native, hr);
        }

        public bool RemovePin(string packageId, string? sourceId)
        {
            var native = GetNative();
            var hr = native.RemovePin(packageId, sourceId ?? string.Empty, out var removed);
            ThrowIfFailed(native, hr);
            return removed != 0;
        }

        public void ResetPins(string? sourceId)
        {
            var native = GetNative();
            var hr = native.ResetPins(sourceId ?? string.Empty);
            ThrowIfFailed(native, hr);
        }

        public string DownloadInstallerJson(string requestJson, string downloadDirectory) =>
            InvokeJson((IPackageManagerNative native, out string value) => native.DownloadInstallerJson(requestJson, downloadDirectory, out value));

        public string InstallJson(string requestJson) =>
            InvokeJson((IPackageManagerNative native, out string value) => native.InstallJson(requestJson, out value));

        public string UninstallJson(string requestJson) =>
            InvokeJson((IPackageManagerNative native, out string value) => native.UninstallJson(requestJson, out value));

        public string RepairJson(string requestJson) =>
            InvokeJson((IPackageManagerNative native, out string value) => native.RepairJson(requestJson, out value));

        public void Dispose()
        {
            var native = _native;
            if (native is null)
            {
                return;
            }

            Marshal.FinalReleaseComObject(native);
            _native = null;
        }

        private IPackageManagerNative GetNative()
        {
            return _native ?? throw new ObjectDisposedException(nameof(PingetPackageManager));
        }

        private string InvokeJson(JsonInvoker invoker)
        {
            var native = GetNative();
            var hr = invoker(native, out var value);
            ThrowIfFailed(native, hr);
            return value;
        }

        private delegate int JsonInvoker(IPackageManagerNative native, out string value);

        private static void ThrowIfFailed(IPackageManagerNative native, int hresult)
        {
            if (hresult >= 0)
            {
                return;
            }

            string? message = null;
            try
            {
                if (native.GetLastError(out var value) >= 0)
                {
                    message = value;
                }
            }
            catch (COMException)
            {
            }

            HResult.ThrowIfFailed(hresult, message);
        }
    }
}
