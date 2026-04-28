using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Devolutions.Pinget.ComInterop
{
    public sealed class NativePingetComLibrary : IDisposable
    {
        private const string NativeFileName = "pinget-com.dll";
        private const string LegacyNativeFileName = "pinget_com.dll";

        private readonly NativeMethods.NativeLibraryHandle _library;
        private readonly PingetCreatePackageManagerDelegate _createPackageManager;
        private bool _disposed;

        private NativePingetComLibrary(NativeMethods.NativeLibraryHandle library)
        {
            _library = library;
            _createPackageManager = NativeMethods.GetRequiredExport<PingetCreatePackageManagerDelegate>(
                _library,
                "PingetCreatePackageManager");
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PingetCreatePackageManagerDelegate(ref Guid riid, out IntPtr instance);

        public static NativePingetComLibrary LoadFromDefaultLocation()
        {
            return LoadFromBaseDirectory(GetAssemblyDirectory());
        }

        public static NativePingetComLibrary LoadFromBaseDirectory(string baseDirectory)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new PlatformNotSupportedException("The Pinget COM bridge is Windows-only.");
            }

            foreach (var candidate in GetCandidatePaths(baseDirectory))
            {
                if (File.Exists(candidate))
                {
                    return new NativePingetComLibrary(NativeMethods.LoadLibrary(candidate));
                }
            }

            throw new FileNotFoundException(
                $"Could not find '{NativeFileName}' relative to '{baseDirectory}'.",
                Path.Combine(baseDirectory, NativeFileName));
        }

        public PingetPackageManager CreatePackageManager()
        {
            ThrowIfDisposed();

            var iid = PingetPackageManager.NativeInterfaceId;
            var hr = _createPackageManager(ref iid, out var instance);
            HResult.ThrowIfFailed(hr);

            if (instance == IntPtr.Zero)
            {
                throw new COMException("Native Pinget COM library returned a null package manager pointer.", HResult.EPointer);
            }

            try
            {
                var native = (IPackageManagerNative)Marshal.GetTypedObjectForIUnknown(instance, typeof(IPackageManagerNative));
                return new PingetPackageManager(native);
            }
            finally
            {
                Marshal.Release(instance);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _library.Dispose();
            _disposed = true;
        }

        private static string GetAssemblyDirectory()
        {
            var location = typeof(NativePingetComLibrary).Assembly.Location;
            if (string.IsNullOrWhiteSpace(location))
            {
                location = Assembly.GetExecutingAssembly().Location;
            }

            return Path.GetDirectoryName(location) ?? AppDomain.CurrentDomain.BaseDirectory;
        }

        private static string[] GetCandidatePaths(string baseDirectory)
        {
            var architectureDirectory = GetWindowsRuntimeIdentifier();
            return new[]
            {
                Path.Combine(baseDirectory, "runtimes", architectureDirectory, "native", NativeFileName),
                Path.Combine(baseDirectory, "runtimes", architectureDirectory, "native", LegacyNativeFileName),
                Path.Combine(baseDirectory, NativeFileName),
                Path.Combine(baseDirectory, LegacyNativeFileName),
                Path.Combine(baseDirectory, GetArchitectureDirectoryName(), NativeFileName),
                Path.Combine(baseDirectory, GetArchitectureDirectoryName(), LegacyNativeFileName),
            };
        }

        private static string GetWindowsRuntimeIdentifier()
        {
            switch (RuntimeInformation.ProcessArchitecture)
            {
                case Architecture.X64:
                    return "win-x64";
                case Architecture.Arm64:
                    return "win-arm64";
                default:
                    throw new PlatformNotSupportedException(
                        $"The Pinget COM bridge is only packaged for x64 and ARM64 Windows processes; current process architecture is {RuntimeInformation.ProcessArchitecture}.");
            }
        }

        private static string GetArchitectureDirectoryName()
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(NativePingetComLibrary));
            }
        }
    }
}
