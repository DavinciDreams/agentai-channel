using Chirper.Grains;
using Chirper.Grains.Models;
using Microsoft.AspNetCore.Mvc;

namespace Chirper.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChirperController : ControllerBase
{
    private readonly IGrainFactory _client;
    private readonly ILogger<ChirperController> _logger;

    public ChirperController(IGrainFactory client, ILogger<ChirperController> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Get recent chirps received by a user (their timeline).
    /// </summary>
    [HttpGet("{username}/timeline")]
    public async Task<ActionResult<List<ChirperMessage>>> GetTimeline(string username, [FromQuery] int count = 20)
    {
        try
        {
            var grain = _client.GetGrain<IChirperAccount>(username);
            var messages = await grain.GetReceivedMessagesAsync(Math.Clamp(count, 1, 100), 0);
            return Ok(messages);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting timeline for {Username}", username);
            return StatusCode(500, "Error retrieving timeline");
        }
    }

    /// <summary>
    /// Get chirps published by a user.
    /// </summary>
    [HttpGet("{username}/chirps")]
    public async Task<ActionResult<List<ChirperMessage>>> GetUserChirps(string username, [FromQuery] int count = 20)
    {
        try
        {
            var grain = _client.GetGrain<IChirperAccount>(username);
            var messages = await grain.GetPublishedMessagesAsync(Math.Clamp(count, 1, 100), 0);
            return Ok(messages);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting chirps for {Username}", username);
            return StatusCode(500, "Error retrieving chirps");
        }
    }

    /// <summary>
    /// Publish a new chirp.
    /// </summary>
    [HttpPost("{username}/chirps")]
    public async Task<ActionResult> PublishChirp(string username, [FromBody] PublishChirpRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest("Message cannot be empty");
        }

        if (request.Message.Length > 280)
        {
            return BadRequest("Message cannot exceed 280 characters");
        }

        try
        {
            var grain = _client.GetGrain<IChirperAccount>(username);
            await grain.PublishMessageAsync(request.Message);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing chirp for {Username}", username);
            return StatusCode(500, "Error publishing chirp");
        }
    }

    /// <summary>
    /// Get list of users this user is following.
    /// </summary>
    [HttpGet("{username}/following")]
    public async Task<ActionResult<List<string>>> GetFollowing(string username)
    {
        try
        {
            var grain = _client.GetGrain<IChirperAccount>(username);
            var following = await grain.GetFollowingListAsync();
            return Ok(following);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting following list for {Username}", username);
            return StatusCode(500, "Error retrieving following list");
        }
    }

    /// <summary>
    /// Get list of users following this user.
    /// </summary>
    [HttpGet("{username}/followers")]
    public async Task<ActionResult<List<string>>> GetFollowers(string username)
    {
        try
        {
            var grain = _client.GetGrain<IChirperAccount>(username);
            var followers = await grain.GetFollowersListAsync();
            return Ok(followers);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting followers list for {Username}", username);
            return StatusCode(500, "Error retrieving followers list");
        }
    }

    /// <summary>
    /// Follow a user.
    /// </summary>
    [HttpPost("{username}/following/{targetUsername}")]
    public async Task<ActionResult> FollowUser(string username, string targetUsername)
    {
        if (username == targetUsername) return BadRequest("Cannot follow yourself");
        try
        {
            var grain = _client.GetGrain<IChirperAccount>(username);
            await grain.FollowUserIdAsync(targetUsername);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error following user {TargetUsername} for {Username}", targetUsername, username);
            return StatusCode(500, "Error following user");
        }
    }

    /// <summary>
    /// Unfollow a user.
    /// </summary>
    [HttpDelete("{username}/following/{targetUsername}")]
    public async Task<ActionResult> UnfollowUser(string username, string targetUsername)
    {
        try
        {
            var grain = _client.GetGrain<IChirperAccount>(username);
            await grain.UnfollowUserIdAsync(targetUsername);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unfollowing user {TargetUsername} for {Username}", targetUsername, username);
            return StatusCode(500, "Error unfollowing user");
        }
    }
}

public record PublishChirpRequest(string Message);
