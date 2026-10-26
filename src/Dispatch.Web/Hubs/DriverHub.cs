using Dispatch.Web.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Dispatch.Web.Hubs;

/// <summary>The server only pushes "route changed" to phones; phones never call the hub.</summary>
[Authorize(Policy = DriverApi.Policy)]
public sealed class DriverHub : Hub;
