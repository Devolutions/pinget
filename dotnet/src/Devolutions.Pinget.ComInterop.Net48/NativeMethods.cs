using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Devolutions.Pinget.ComInterop
{
    internal static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr hModule);

        public static NativeLibraryHandle LoadLibrary(string path)
        {
            var handle = LoadLibraryW(path);
            if (handle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to load native Pinget COM library '{path}'.");
            }

            return new NativeLibraryHandle(handle);
        }

        public static TDelegate GetRequiredExport<TDelegate>(NativeLibraryHandle library, string name)
            where TDelegate : class
        {
            var address = GetProcAddress(library.DangerousGetHandle(), name);
            if (address == IntPtr.Zero)
            {
                throw new MissingMethodException($"Native Pinget COM library does not export '{name}'.");
            }

            return (TDelegate)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(TDelegate));
        }

        internal sealed class NativeLibraryHandle : SafeHandle
        {
            public NativeLibraryHandle()
                : base(IntPtr.Zero, true)
            {
            }

            public NativeLibraryHandle(IntPtr handle)
                : this()
            {
                SetHandle(handle);
            }

            public override bool IsInvalid => handle == IntPtr.Zero;

            protected override bool ReleaseHandle()
            {
                return FreeLibrary(handle);
            }
        }
    }
}
