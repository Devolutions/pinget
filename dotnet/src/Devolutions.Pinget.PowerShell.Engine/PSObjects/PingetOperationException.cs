namespace Devolutions.Pinget.PowerShell.Engine.PSObjects;

internal sealed class PingetOperationException : Exception
{
    public PingetOperationException(string message, int hresult)
        : base(message)
    {
        HResult = hresult;
    }
}
