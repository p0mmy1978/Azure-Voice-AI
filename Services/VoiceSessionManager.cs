using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CallAutomation.AzureAI.VoiceLive.Models;
using CallAutomation.AzureAI.VoiceLive.Services.Interfaces;
using CallAutomation.AzureAI.VoiceLive.Services.Voice;

namespace CallAutomation.AzureAI.VoiceLive.Services
{
    public class VoiceSessionManager : IVoiceSessionManager
    {
        private readonly ILogger<VoiceSessionManager> _logger;
        private readonly SessionConfigBuilder _sessionConfigBuilder;
        private ClientWebSocket? _webSocket;

        public VoiceSessionManager(ILogger<VoiceSessionManager> logger, SessionConfigBuilder sessionConfigBuilder)
        {
            _logger = logger;
            _sessionConfigBuilder = sessionConfigBuilder;
        }

        public bool IsConnected => _webSocket?.State == WebSocketState.Open;

        public async Task<bool> ConnectAsync(string endpoint, string apiKey, string model)
        {
            try
            {
                if (IsConnected)
                {
                    _logger.LogWarning("🔗 WebSocket already connected");
                    return true;
                }

                // Azure Voice Live API endpoint format - API key in header for security
                var websocketUrl = new Uri($"{endpoint.Replace("https", "wss")}/voice-agent/realtime?api-version=2025-05-01-preview&x-ms-client-request-id={Guid.NewGuid()}&model={model}");

                _webSocket = new ClientWebSocket();

                // SECURITY FIX: Use header instead of URL query parameter for API key
                _webSocket.Options.SetRequestHeader("api-key", apiKey);
                _webSocket.Options.SetRequestHeader("User-Agent", "AzureVoiceAI/1.0");
                _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                _webSocket.Options.SetBuffer(16384, 16384);
                
                _logger.LogInformation($"🔗 Connecting to Azure Voice Live API:");
                _logger.LogInformation($"   Endpoint: {endpoint}");
                _logger.LogInformation($"   Model: {model}");
                _logger.LogInformation($"   API Key: {apiKey.Substring(0, Math.Min(10, apiKey.Length))}...");
                
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                
                var connectStart = DateTime.Now;
                await _webSocket.ConnectAsync(websocketUrl, cts.Token);
                var connectDuration = DateTime.Now - connectStart;
                
                _logger.LogInformation($"✅ Connected to Azure Voice Live successfully in {connectDuration.TotalMilliseconds:F0}ms! State: {_webSocket.State}");
                
                await Task.Delay(100);
                
                if (_webSocket.State != WebSocketState.Open)
                {
                    _logger.LogError($"❌ Connection closed immediately after connecting. Final state: {_webSocket.State}");
                    return false;
                }
                
                _logger.LogInformation("🎯 Connection stable and ready for communication");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to connect to Azure Voice Live");
                return false;
            }
        }

        public async Task<bool> UpdateSessionAsync(SessionConfig config)
        {
            try
            {
                if (!IsConnected)
                {
                    _logger.LogError("❌ Cannot update session - WebSocket not connected");
                    return false;
                }

                var updateStart = DateTime.Now;

                _logger.LogInformation("🔧 Building Azure Voice Live session configuration...");
                
                // Use the SessionConfigBuilder to create the configuration
                var sessionObject = _sessionConfigBuilder.BuildSessionUpdateObject(config.VoiceTemperature);
                
                var jsonOptions = new JsonSerializerOptions 
                { 
                    WriteIndented = false,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                };
                
                var sessionUpdate = JsonSerializer.Serialize(sessionObject, jsonOptions);
                
                _logger.LogInformation($"🔧 Updating Azure Voice Live session...");
                _logger.LogInformation($"📊 Configuration: {_sessionConfigBuilder.GetConfigurationSummary()}");
                _logger.LogInformation($"📊 Config size: {sessionUpdate.Length} chars");

                var success = await SendMessageAsync(sessionUpdate);
                
                var updateDuration = DateTime.Now - updateStart;
                
                if (success)
                {
                    _logger.LogInformation($"✅ Azure Voice Live session configured successfully in {updateDuration.TotalMilliseconds:F0}ms");
                }
                else
                {
                    _logger.LogError($"❌ Failed to configure Azure Voice Live session after {updateDuration.TotalMilliseconds:F0}ms");
                }
                
                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to update Azure Voice Live session");
                return false;
            }
        }

        public async Task<bool> StartResponseAsync()
        {
            try
            {
                if (!IsConnected)
                {
                    _logger.LogError("❌ Cannot start response - WebSocket not connected");
                    return false;
                }

                var startTime = DateTime.Now;
                
                var responseObject = new { type = "response.create" };
                var message = JsonSerializer.Serialize(responseObject);
                
                _logger.LogInformation("🚀 Starting initial AI response...");
                
                var success = await SendMessageAsync(message);
                var duration = DateTime.Now - startTime;
                
                if (success)
                {
                    _logger.LogInformation($"✅ Initial response started successfully in {duration.TotalMilliseconds:F0}ms");
                }
                else
                {
                    _logger.LogError($"❌ Failed to start initial response after {duration.TotalMilliseconds:F0}ms");
                }
                
                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to start response");
                return false;
            }
        }

        public async Task<bool> SendMessageAsync(string message, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!IsConnected)
                {
                    _logger.LogDebug("⚠️ Cannot send message - WebSocket not connected");
                    return false;
                }

                if (string.IsNullOrWhiteSpace(message))
                {
                    _logger.LogWarning("⚠️ Cannot send empty message");
                    return false;
                }

                byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                
                await _webSocket!.SendAsync(
                    new ArraySegment<byte>(messageBytes),
                    WebSocketMessageType.Text,
                    true,
                    timeoutCts.Token);

                if (message.Contains("input_audio_buffer.append"))
                {
                    _logger.LogDebug($"🎤 Audio data sent: {messageBytes.Length} bytes");
                }
                else if (message.Length > 100)
                {
                    _logger.LogDebug($"📤 Message sent to Azure Voice Live: {messageBytes.Length} bytes");
                }
                else
                {
                    _logger.LogDebug($"📤 Command sent: {message}");
                }
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to send message to Azure Voice Live");
                return false;
            }
        }

        public async Task<string?> ReceiveMessageAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (!IsConnected)
                {
                    _logger.LogDebug("⚠️ Cannot receive message - WebSocket not connected");
                    return null;
                }

                const int bufferSize = 32768;
                byte[] buffer = new byte[bufferSize];
                var receiveBuffer = new ArraySegment<byte>(buffer);
                StringBuilder messageBuilder = new StringBuilder();

                WebSocketReceiveResult result;
                do
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
                    
                    result = await _webSocket!.ReceiveAsync(receiveBuffer, timeoutCts.Token);
                    
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var textReceived = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        messageBuilder.Append(textReceived);
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("🔚 WebSocket close message received from Azure Voice Live");
                        return null;
                    }
                    else if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        _logger.LogDebug("📦 Binary message received from Azure Voice Live, skipping...");
                        continue;
                    }
                }
                while (!result.EndOfMessage);

                string receivedMessage = messageBuilder.ToString();
                
                if (string.IsNullOrWhiteSpace(receivedMessage))
                {
                    _logger.LogDebug("📭 Received empty message from Azure Voice Live WebSocket");
                    return null;
                }

                if (receivedMessage.Contains("response.audio.delta"))
                {
                    _logger.LogDebug($"🔊 Audio response received: {receivedMessage.Length} chars");
                }
                else if (receivedMessage.Contains("error"))
                {
                    _logger.LogWarning($"⚠️ Error message received: {receivedMessage.Length} chars");
                }
                else
                {
                    _logger.LogDebug($"📥 Message received from Azure Voice Live: {receivedMessage.Length} chars");
                }
                
                return receivedMessage;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to receive message from Azure Voice Live");
                return null;
            }
        }

        public async Task<bool> CloseAsync()
        {
            try
            {
                if (_webSocket?.State == WebSocketState.Open)
                {
                    _logger.LogInformation("🔗 Closing Azure Voice Live WebSocket connection...");
                    
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session completed", cts.Token);
                    
                    _logger.LogInformation("✅ Azure Voice Live WebSocket connection closed successfully");
                }
                else if (_webSocket?.State == WebSocketState.Connecting)
                {
                    _logger.LogInformation("🔗 Aborting connecting Azure Voice Live WebSocket");
                    _webSocket.Abort();
                }
                else if (_webSocket != null)
                {
                    _logger.LogInformation($"🔗 Azure Voice Live WebSocket in state {_webSocket.State}, disposing...");
                }
                
                _webSocket?.Dispose();
                _webSocket = null;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error closing Azure Voice Live WebSocket connection");
                _webSocket?.Dispose();
                _webSocket = null;
                return false;
            }
        }
    }
}
