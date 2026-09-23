using System;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.Projectionist.Services.Handoff;

/// <summary>Keep-alive and clean-up of a pre-started transcode, by PlaySessionId only.</summary>
internal interface IHandoffTranscodeControl
{
    /// <summary>True when the server currently tracks a job with this PlaySessionId.</summary>
    bool Exists(string playSessionId);

    void Ping(string playSessionId);

    Task Kill(string deviceId, string playSessionId);
}

/// <summary>
/// <see cref="ITranscodeManager"/> (MediaBrowser.Controller, 10.11) adapter.
/// PingTranscodingJob(psid, null) re-arms the 60 s HLS kill timer of every job with that
/// PlaySessionId without touching IsUserPaused. KillTranscodingJobs(deviceId, psid, deleteFiles)
/// matches on PlaySessionId alone when one is given; with an EMPTY PlaySessionId it would kill
/// every job of the device, so an empty id is refused here.
/// </summary>
internal sealed class HandoffTranscodeControl : IHandoffTranscodeControl
{
    private readonly ITranscodeManager _transcodeManager;

    public HandoffTranscodeControl(ITranscodeManager transcodeManager)
    {
        _transcodeManager = transcodeManager;
    }

    public bool Exists(string playSessionId)
        => !string.IsNullOrWhiteSpace(playSessionId) && _transcodeManager.GetTranscodingJob(playSessionId) is not null;

    public void Ping(string playSessionId)
    {
        if (!string.IsNullOrWhiteSpace(playSessionId))
        {
            _transcodeManager.PingTranscodingJob(playSessionId, null);
        }
    }

    public Task Kill(string deviceId, string playSessionId)
    {
        if (string.IsNullOrWhiteSpace(playSessionId))
        {
            return Task.CompletedTask;
        }

        return _transcodeManager.KillTranscodingJobs(deviceId ?? string.Empty, playSessionId, _ => true);
    }
}
