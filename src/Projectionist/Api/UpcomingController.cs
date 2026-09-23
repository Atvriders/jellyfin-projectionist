using System;
using System.Text.Json;
using Jellyfin.Plugin.Projectionist.Services.Handoff;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Projectionist.Api;

/// <summary>
/// GET /Plugins/Projectionist/Upcoming?currentItemId=... - what this user+device will play
/// after its prerolls, and how (see <see cref="UpcomingFeatureInfo"/>). 204 when nothing is
/// pending, or when currentItemId is not one of the pending intros.
/// </summary>
[ApiController]
[Route("Plugins/Projectionist")]
public sealed class UpcomingController : ControllerBase
{
    // Serialized here rather than through the server's formatters so the wire format is exactly
    // the contract's (PascalCase pinned by JsonPropertyName, nulls included) whatever the caller's
    // Accept header asks of Jellyfin's JSON options.
    private static readonly JsonSerializerOptions WireOptions = new();

    private readonly IFeatureHandoffRegistry _registry;

    public UpcomingController(IFeatureHandoffRegistry registry)
    {
        _registry = registry;
    }

    [HttpGet("Upcoming")]
    [Authorize]
    public ActionResult GetUpcoming([FromQuery] string? currentItemId)
    {
        Guid? current = null;
        if (!string.IsNullOrWhiteSpace(currentItemId))
        {
            if (!HandoffRequestParser.TryParseId(currentItemId.Trim(), out var id))
            {
                return BadRequest();
            }

            current = id;
        }

        // Same claims Jellyfin.Api's ClaimsPrincipalExtensions read (InternalClaimTypes, 10.11).
        var userClaim = User.FindFirst(FeatureHandoffRegistry.ClaimUserId)?.Value;
        var deviceId = User.FindFirst(FeatureHandoffRegistry.ClaimDeviceId)?.Value;
        if (!Guid.TryParse(userClaim, out var userId) || string.IsNullOrEmpty(deviceId))
        {
            return NoContent();
        }

        Response.Headers.CacheControl = "no-store";
        var info = _registry.GetUpcoming(userId, deviceId, current);
        if (info is null)
        {
            return NoContent();
        }

        return Content(JsonSerializer.Serialize(info, WireOptions), "application/json");
    }
}
