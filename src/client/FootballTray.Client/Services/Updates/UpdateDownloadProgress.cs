using System;

namespace FootballTray.Client.Services.Updates;

/// <summary>
/// Progress snapshot for an update package download.
/// </summary>
public sealed record UpdateDownloadProgress
{
    public long BytesReceived { get; init; }
    public long TotalExpectedBytes { get; init; }
    public int Percent { get; init; }

    public static UpdateDownloadProgress Create(long bytesReceived, long totalExpectedBytes)
    {
        int percent = 0;
        if (totalExpectedBytes > 0)
        {
            double ratio = (double)bytesReceived / totalExpectedBytes;
            percent = (int)Math.Clamp(Math.Round(ratio * 100.0), 0, 100);
        }

        return new UpdateDownloadProgress
        {
            BytesReceived = bytesReceived,
            TotalExpectedBytes = totalExpectedBytes,
            Percent = percent
        };
    }
}
