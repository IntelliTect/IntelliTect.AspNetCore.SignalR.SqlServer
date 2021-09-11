using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace DemoServer
{
    public class ChatHub : Hub
    {
        private readonly ILogger<ChatHub> _logger;

        public ChatHub(ILogger<ChatHub> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        private string groupId = Guid.Empty.ToString();

        public async Task SendMessage(string name, string message)
        {
            _logger.LogInformation($"{nameof(SendMessage)} called. ConnectionId:{Context.ConnectionId}, Name:{name}, Message:{message}");
            await Clients.Group(groupId).SendAsync("BroadcastMessage", name, message);
        }

        public override async Task OnConnectedAsync()
        {
            _logger.LogInformation($"{nameof(OnConnectedAsync)} called.");

            await base.OnConnectedAsync();
            await Groups.AddToGroupAsync(Context.ConnectionId, Guid.Empty.ToString());
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            _logger.LogInformation(exception, $"{nameof(OnDisconnectedAsync)} called.");

            await base.OnDisconnectedAsync(exception);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, Guid.Empty.ToString());
        }
    }
}