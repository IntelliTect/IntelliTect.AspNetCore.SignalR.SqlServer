using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace DemoServer
{
    public class UserIdProvider : IUserIdProvider
    {
        public virtual string GetUserId(HubConnectionContext connection)
        {
            return "staticUserid";
        }
    }

    public class ChatHubA : Hub
    {
        private readonly ILogger<ChatHubA> _logger;

        public ChatHubA(ILogger<ChatHubA> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        private readonly string groupId = Guid.Empty.ToString();

        public async Task SendMessage(string name, string message)
        {
          //  _logger.LogInformation($"{nameof(SendMessage)} called. ConnectionId:{Context.ConnectionId}, Name:{name}, Message:{message}");
            await Clients.Group(groupId).SendAsync("BroadcastMessage", name, message);
        }

        public override async Task OnConnectedAsync()
        {
            _logger.LogInformation($"{nameof(OnConnectedAsync)} called.");

            await base.OnConnectedAsync();
            await Groups.AddToGroupAsync(Context.ConnectionId, Guid.Empty.ToString());
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            _logger.LogInformation(exception, $"{nameof(OnDisconnectedAsync)} called.");

            await base.OnDisconnectedAsync(exception);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, Guid.Empty.ToString());
        }
    }

    public class ChatHubB : Hub
    {
        private readonly ILogger<ChatHubB> _logger;

        public ChatHubB(ILogger<ChatHubB> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        private readonly string groupId = Guid.Empty.ToString();

        public async Task SendMessage(string name, string message)
        {
          //  _logger.LogInformation($"{nameof(SendMessage)} called. ConnectionId:{Context.ConnectionId}, Name:{name}, Message:{message}");
            await Clients.Group(groupId).SendAsync("BroadcastMessage", name, "group");
            await Clients.Caller.SendAsync("BroadcastMessage", name, "caller");
            await Clients.User(Context.UserIdentifier!).SendAsync("BroadcastMessage", name, "user");
            await Clients.All.SendAsync("BroadcastMessage", name, "all");
            await Clients.AllExcept(Context.ConnectionId).SendAsync("BroadcastMessage", name, "allExcept");
        }

        public override async Task OnConnectedAsync()
        {
            _logger.LogInformation($"{nameof(OnConnectedAsync)} called.");

            await base.OnConnectedAsync();
            await Groups.AddToGroupAsync(Context.ConnectionId, Guid.Empty.ToString());
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            _logger.LogInformation(exception, $"{nameof(OnDisconnectedAsync)} called.");

            await base.OnDisconnectedAsync(exception);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, Guid.Empty.ToString());
        }
    }
}