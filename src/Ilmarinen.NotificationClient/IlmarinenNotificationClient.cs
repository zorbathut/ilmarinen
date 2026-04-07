using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.NotificationClient;

public class IlmarinenNotificationClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly bool _disposeHttpClient;
    private readonly JsonSerializerOptions _jsonOptions;

    public Guid? SubscriberId { get; private set; }

    public IlmarinenNotificationClient(string baseUrl, HttpClient? httpClient = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');

        if (httpClient == null)
        {
            _httpClient = new HttpClient();
            _disposeHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _disposeHttpClient = false;
        }

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };
    }

    public async Task<SubscriberInfo> RegisterAsync(string name, int heartbeatTimeoutMinutes = 5)
    {
        var request = new SubscriberRegistration
        {
            Name = name,
            HeartbeatTimeoutMinutes = heartbeatTimeoutMinutes
        };

        var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/subscribers", request, _jsonOptions);
        response.EnsureSuccessStatusCode();

        var info = await response.Content.ReadFromJsonAsync<SubscriberInfo>(_jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize subscriber response");

        SubscriberId = info.Id;
        return info;
    }

    public async Task<bool> LoadSubscriberAsync(Guid subscriberId)
    {
        var response = await _httpClient.GetAsync($"{_baseUrl}/api/subscribers/{subscriberId}");
        if (!response.IsSuccessStatusCode) return false;

        var info = await response.Content.ReadFromJsonAsync<SubscriberInfo>(_jsonOptions);
        if (info == null) return false;

        SubscriberId = info.Id;
        return true;
    }

    public async Task<bool> HeartbeatAsync()
    {
        if (SubscriberId == null)
            throw new InvalidOperationException("Must register or load a subscriber before sending heartbeat");

        var response = await _httpClient.PostAsync($"{_baseUrl}/api/subscribers/{SubscriberId}/heartbeat", null);
        return response.IsSuccessStatusCode;
    }

    public async Task<List<JobNotification>> PullNotificationsAsync(int limit = 10)
    {
        if (SubscriberId == null)
            throw new InvalidOperationException("Must register or load a subscriber before pulling notifications");

        var response = await _httpClient.PostAsync(
            $"{_baseUrl}/api/subscribers/{SubscriberId}/notifications?limit={limit}", null);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<List<JobNotification>>(_jsonOptions)
            ?? new List<JobNotification>();
    }

    public async Task AcknowledgeAsync(IReadOnlyList<Guid> notificationIds)
    {
        if (SubscriberId == null)
            throw new InvalidOperationException("Must register or load a subscriber before acknowledging");

        var response = await _httpClient.PostAsJsonAsync(
            $"{_baseUrl}/api/subscribers/{SubscriberId}/notifications/ack", notificationIds, _jsonOptions);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        if (_disposeHttpClient)
            _httpClient.Dispose();
    }
}
