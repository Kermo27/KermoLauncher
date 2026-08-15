namespace GameLauncher.Core.Models;

public sealed class InsufficientDiskSpaceException : InvalidOperationException
{
    public long RequiredBytes { get; }
    public long AvailableBytes { get; }

    public InsufficientDiskSpaceException(long requiredBytes, long availableBytes)
        : base($"Not enough disk space: need {requiredBytes} bytes, {availableBytes} available.")
    {
        RequiredBytes = requiredBytes;
        AvailableBytes = availableBytes;
    }
}
