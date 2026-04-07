using System;
using Ilmarinen.Protocol.Requests;
using System;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Ilmarinen.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SubscribersController : ControllerBase
{
    private readonly SubscriberRepository _subscribers;
    private readonly NotificationRepository _notifications;

    public SubscribersController(SubscriberRepository subscribers, NotificationRepository notifications)
    {
        _subscribers = subscribers;
        _notifications = notifications;
    }

    [HttpPost]
    public async Task<ActionResult<SubscriberInfo>> Register([FromBody] SubscriberRegistration registration)
    {
        var info = await _subscribers.RegisterAsync(registration);
        return Ok(info);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<SubscriberInfo>> Get(string id)
    {
        if (!Guid.TryParse(id, out var parsed))
            return BadRequest("Invalid subscriber ID");

        var info = await _subscribers.GetAsync(parsed);
        return info != null ? Ok(info) : NotFound();
    }

    [HttpPost("{id}/heartbeat")]
    public async Task<ActionResult> Heartbeat(string id)
    {
        if (!Guid.TryParse(id, out var parsed))
            return BadRequest("Invalid subscriber ID");

        var success = await _subscribers.HeartbeatAsync(parsed);
        return success ? Ok() : NotFound();
    }

    [HttpPost("{id}/notifications")]
    public async Task<ActionResult<List<JobNotification>>> PullNotifications(string id, [FromQuery] int limit = 10)
    {
        if (!Guid.TryParse(id, out var parsed))
            return BadRequest("Invalid subscriber ID");

        var notifications = await _notifications.PullAsync(parsed, limit);
        return Ok(notifications);
    }

    [HttpPost("{id}/notifications/ack")]
    public async Task<ActionResult> Acknowledge(string id, [FromBody] List<Guid> notificationIds)
    {
        if (!Guid.TryParse(id, out _))
            return BadRequest("Invalid subscriber ID");

        await _notifications.AcknowledgeAsync(notificationIds);
        return Ok();
    }
}
