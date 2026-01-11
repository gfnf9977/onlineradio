using Microsoft.AspNetCore.Mvc;
using OnlineRadioStation.Domain;
using System.Net.WebSockets;

public class LiveDjController : Controller
{
    private readonly LiveStreamService _liveService;

    public LiveDjController(LiveStreamService liveService)
    {
        _liveService = liveService;
    }

    [Route("/ws/stream")]
    public async Task Get()
    {
        if (HttpContext.WebSockets.IsWebSocketRequest)
        {
            using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
            
            // Починаємо ефір при підключенні
            _liveService.StartBroadcast();
            
            var buffer = new byte[1024 * 4];
            
            try 
            {
                while (webSocket.State == WebSocketState.Open)
                {
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by DJ", CancellationToken.None);
                    }
                    else
                    {
                        // Пишемо байти прямо в FFmpeg
                        await _liveService.WriteAudioDataAsync(buffer, result.Count);
                    }
                }
            }
            finally
            {
                _liveService.StopBroadcast();
            }
        }
        else
        {
            HttpContext.Response.StatusCode = 400;
        }
    }
}