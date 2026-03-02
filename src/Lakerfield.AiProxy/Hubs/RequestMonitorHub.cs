using Lakerfield.AiProxy.Models;
using Microsoft.AspNetCore.SignalR;

namespace Lakerfield.AiProxy.Hubs;

/// <summary>
/// SignalR hub for broadcasting real-time request events to connected dashboard clients.
/// </summary>
public class RequestMonitorHub : Hub
{
    public async Task SendRequestReceived(RequestLogEntry entry) =>
        await Clients.All.SendAsync("RequestReceived", entry);

    public async Task SendRequestForwarded(string requestId, string instanceName) =>
        await Clients.All.SendAsync("RequestForwarded", new { requestId, instanceName });

    public async Task SendRequestCompleted(RequestLogEntry entry) =>
        await Clients.All.SendAsync("RequestCompleted", entry);

    public async Task SendRequestFailed(string requestId, string error) =>
        await Clients.All.SendAsync("RequestFailed", new { requestId, error });
}

/// <summary>
/// Service for broadcasting events from outside the hub context.
/// </summary>
public class RequestMonitorService
{
    private readonly IHubContext<RequestMonitorHub> _hubContext;

    public RequestMonitorService(IHubContext<RequestMonitorHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task BroadcastRequestReceived(RequestLogEntry entry) =>
        _hubContext.Clients.All.SendAsync("RequestReceived", entry);

    public Task BroadcastRequestForwarded(string requestId, string instanceName) =>
        _hubContext.Clients.All.SendAsync("RequestForwarded", new { requestId, instanceName });

    public Task BroadcastRequestCompleted(RequestLogEntry entry) =>
        _hubContext.Clients.All.SendAsync("RequestCompleted", entry);

    public Task BroadcastRequestFailed(string requestId, string error) =>
        _hubContext.Clients.All.SendAsync("RequestFailed", new { requestId, error });

    public Task BroadcastInstanceStatus(IEnumerable<object> instances) =>
        _hubContext.Clients.All.SendAsync("InstanceStatus", instances);
}
