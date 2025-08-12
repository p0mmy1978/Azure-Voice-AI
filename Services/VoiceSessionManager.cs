using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CallAutomation.AzureAI.VoiceLive.Models;
using CallAutomation.AzureAI.VoiceLive.Services.Interfaces;

namespace CallAutomation.AzureAI.VoiceLive.Services
{
    public class VoiceSessionManager : IVoiceSessionManager
    {
        private readonly ILogger<VoiceSessionManager> _logger;
        private ClientWebSocket? _webSocket;

        public VoiceSessionManager(ILogger<VoiceSessionManager> logger)
        {
            _logger = logger;
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

                var websocketUrl = new Uri($"{endpoint.Replace("https", "wss")}/voice-agent/realtime?api-version=2025-05-01-preview&x-ms-client-request-id={Guid.NewGuid()}&model={model}&api-key={apiKey}");

                _webSocket = new ClientWebSocket();
                _logger.LogInformation($"🔗 Connecting to Azure Voice Live: {websocketUrl}");
                
                await _webSocket.ConnectAsync(websocketUrl, CancellationToken.None);
                
                _logger.LogInformation("✅ Connected to Azure Voice Live successfully!");
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

                var sessionObject = new
                {
                    type = "session.update",
                    session = new
                    {
                        instructions = config.Instructions,
                        turn_detection = new
                        {
                            type = "azure_semantic_vad",
                            threshold = config.VadThreshold,
                            prefix_padding_ms = config.PrefixPaddingMs,
                            silence_duration_ms = config.SilenceDurationMs,
                            remove_filler_words = config.RemoveFillerWords
                        },
                        input_audio_noise_reduction = new { type = "azure_deep_noise_suppression" },
                        input_audio_echo_cancellation = new { type = "server_echo_cancellation" },
                        voice = new
                        {
                            name = config.VoiceName,
                            type = "azure-standard",
                            temperature = config.VoiceTemperature
                        },
                        tools = new object[]
                        {
                            new {
                                type = "function",
                                name = "check_staff_exists",
                                description = "Check if a staff member is authorized to receive messages. Include department if known.",
                                parameters = new {
                                    type = "object",
                                    properties = new {
                                        name = new { type = "string", description = "The name of the person to check" },
                                        department = new { type = "string", description = "The department the person works in (optional)" }
                                    },
                                    required = new[] { "name" }
                                }
                            },
                            new {
                                type = "function",
                                name = "send_message",
                                description = "Send a message to a staff member.",
                                parameters = new {
                                    type = "object",
                                    properties = new {
                                        name = new { type = "string", description = "The name of the person to send the message to" },
                                        message = new { type = "string", description = "The message to send" },
                                        department = new { type = "string", description = "The department the person works in (optional)" }
                                    },
                                    required = new[] { "name", "message" }
                                }
                            },
                            new {
                                type = "function",
                                name = "end_call",
                                description = "End the call gracefully after saying goodbye.",
                                parameters = new {
                                    type = "object",
                                    properties = new { },
                                    required = new string[] { }
                                }
                            }
                        }
                    }
                };

                var sessionUpdate = JsonSerializer.Serialize(sessionObject, new JsonSerializerOptions { WriteIndented = true });
                _logger.LogInformation($"🔧 Updating AI session configuration");
                _logger.LogDebug($"Session config: {sessionUpdate}");

                return await SendMessageAsync(sessionUpdate);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to update session");
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

                var responseObject = new { type = "response.create" };
                var message = JsonSerializer.Serialize(responseObject, new JsonSerializerOptions { WriteIndented = true });
                
                _logger.LogInformation("🚀 Starting initial AI response");
                return await SendMessageAsync(message);
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
                    _logger.LogWarning("⚠️ Cannot send message - WebSocket not connected");
                    return false;
                }

                byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                await _webSocket!.SendAsync(
                    new ArraySegment<byte>(messageBytes),
                    WebSocketMessageType.Text,
                    true,
                    cancellationToken);

                _logger.LogDebug($"📤 Message sent to AI: {message.Length} chars");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to send message to AI");
                return false;
            }
        }

        public async Task<string?> ReceiveMessageAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (!IsConnected)
                {
                    return null;
                }

                byte[] buffer = new byte[1024 * 8];
                var receiveBuffer = new ArraySegment<byte>(buffer);
                StringBuilder messageBuilder = new StringBuilder();

                WebSocketReceiveResult result;
                do
                {
                    result = await _webSocket!.ReceiveAsync(receiveBuffer, cancellationToken);
                    messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                string receivedMessage = messageBuilder.ToString();
                _logger.LogDebug($"📥 Message received from AI: {receivedMessage.Length} chars");
                
                return receivedMessage;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to receive message from AI");
                return null;
            }
        }

        public async Task<bool> CloseAsync()
        {
            try
            {
                if (_webSocket?.State == WebSocketState.Open)
                {
                    _logger.LogInformation("🔗 Closing WebSocket connection");
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Normal", CancellationToken.None);
                    _logger.LogInformation("✅ WebSocket connection closed successfully");
                }
                
                _webSocket?.Dispose();
                _webSocket = null;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error closing WebSocket connection");
                return false;
            }
        }
    }
}
