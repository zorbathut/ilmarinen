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
    public async Task<ActionResult<SubscriberInfo>> Get(Guid id)
    {
        var info = await _subscribers.GetAsync(id);
        return info != null ? Ok(info) : NotFound();
    }

    [HttpPost("{id}/heartbeat")]
    public async Task<ActionResult> Heartbeat(Guid id)
    {
        var success = await _subscribers.HeartbeatAsync(id);
        return success ? Ok() : NotFound();
    }

    [HttpPost("{id}/notifications")]
    public async Task<ActionResult<List<JobNotification>>> PullNotifications(Guid id, [FromQuery] int limit = 10)
    {
        var notifications = await _notifications.PullAsync(id, limit);
        return Ok(notifications);
    }

    [HttpPost("{id}/notifications/ack")]
    public async Task<ActionResult> Acknowledge(Guid id, [FromBody] List<Guid> notificationIds)
    {
        await _notifications.AcknowledgeAsync(notificationIds);
        return Ok();
    }
}
