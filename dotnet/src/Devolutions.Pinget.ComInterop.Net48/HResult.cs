using System.Runtime.InteropServices;

namespace Devolutions.Pinget.ComInterop
{
    internal static class HResult
    {
        public const int EPointer = unchecked((int)0x80004003);

        public static void ThrowIfFailed(int hresult)
        {
            if (hresult < 0)
            {
                Marshal.ThrowExceptionForHR(hresult);
            }
        }

        public static void ThrowIfFailed(int hresult, string? message)
        {
            if (hresult < 0)
            {
                if (!string.IsNullOrWhiteSpace(message))
                {
                    throw new COMException(message, hresult);
                }

                Marshal.ThrowExceptionForHR(hresult);
            }
        }
    }
}
