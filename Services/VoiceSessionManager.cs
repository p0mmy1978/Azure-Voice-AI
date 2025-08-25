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
                
                _webSocket.Options.SetRequestHeader("User-Agent", "AzureVoiceAI/1.0");
                _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                
                _logger.LogInformation($"🔗 Connecting to Azure Voice Live:");
                _logger.LogInformation($"   Endpoint: {endpoint}");
                _logger.LogInformation($"   Model: {model}");
                _logger.LogInformation($"   API Key: {apiKey.Substring(0, Math.Min(10, apiKey.Length))}...");
                
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await _webSocket.ConnectAsync(websocketUrl, cts.Token);
                
                _logger.LogInformation($"✅ Connected to Azure Voice Live successfully! State: {_webSocket.State}");
                
                await Task.Delay(1000);
                
                if (_webSocket.State != WebSocketState.Open)
                {
                    _logger.LogError($"❌ Connection closed immediately after connecting. Final state: {_webSocket.State}");
                    return false;
                }
                
                _logger.LogInformation("🎯 Connection stable after 1 second check");
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

                // Restored full session configuration with noise suppression and VAD
                var sessionObject = new
                {
                    type = "session.update",
                    session = new
                    {
                        instructions = config.Instructions,
                        
                        // RESTORED: Advanced Voice Activity Detection to handle background noise
                        turn_detection = new
                        {
                            type = "azure_semantic_vad",
                            threshold = config.VadThreshold,
                            prefix_padding_ms = config.PrefixPaddingMs,
                            silence_duration_ms = config.SilenceDurationMs,
                            remove_filler_words = config.RemoveFillerWords
                        },
                        
                        // RESTORED: Noise reduction for TV and background sounds
                        input_audio_noise_reduction = new { type = "azure_deep_noise_suppression" },
                        
                        // RESTORED: Echo cancellation for better audio quality
                        input_audio_echo_cancellation = new { type = "server_echo_cancellation" },
                        
                        // Basic voice configuration
                        voice = new
                        {
                            name = config.VoiceName,
                            type = "azure-standard",
                            temperature = config.VoiceTemperature
                        },

                        // Enhanced function tools
                        tools = new object[]
                        {
                            new {
                                type = "function",
                                name = "check_staff_exists",
                                description = "Check if a staff member is authorized to receive messages. This function now uses advanced fuzzy matching and may return a confirmation request if the name is not found exactly but a similar name exists.",
                                parameters = new {
                                    type = "object",
                                    properties = new {
                                        name = new { 
                                            type = "string", 
                                            description = "The name of the person to check. Will be fuzzy matched if not found exactly." 
                                        },
                                        department = new { 
                                            type = "string", 
                                            description = "The department the person works in (optional but helps with accuracy)" 
                                        }
                                    },
                                    required = new[] { "name" }
                                }
                            },
                            new {
                                type = "function", 
                                name = "confirm_staff_match",
                                description = "Confirm a fuzzy match suggestion when check_staff_exists returns a confirmation request. Only use this after asking the user to confirm.",
                                parameters = new {
                                    type = "object",
                                    properties = new {
                                        original_name = new {
                                            type = "string",
                                            description = "The original name the user said"
                                        },
                                        confirmed_name = new {
                                            type = "string", 
                                            description = "The name the user confirmed"
                                        },
                                        department = new {
                                            type = "string",
                                            description = "The department"
                                        }
                                    },
                                    required = new[] { "original_name", "confirmed_name", "department" }
                                }
                            },
                            new {
                                type = "function",
                                name = "send_message", 
                                description = "Send a message to a staff member after they have been verified as authorized.",
                                parameters = new {
                                    type = "object",
                                    properties = new {
                                        name = new { 
                                            type = "string", 
                                            description = "The exact name of the person to send the message to" 
                                        },
                                        message = new { 
                                            type = "string", 
                                            description = "The message content from the caller" 
                                        },
                                        department = new { 
                                            type = "string", 
                                            description = "The department the person works in (optional)" 
                                        }
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
                    } // FIXED: Added missing closing brace here
                }; // FIXED: Added missing semicolon here

                var sessionUpdate = JsonSerializer.Serialize(sessionObject, new JsonSerializerOptions { WriteIndented = true });
                _logger.LogInformation($"🔧 Updating AI session configuration with noise suppression");
                _logger.LogInformation($"🎯 Voice Settings: {config.VoiceName} (azure-standard), Temperature={config.VoiceTemperature}");
                _logger.LogInformation($"🔇 Noise Suppression: Azure Deep Noise Suppression enabled");
                _logger.LogInformation($"🎤 VAD Settings: Threshold={config.VadThreshold}, Silence={config.SilenceDurationMs}ms, Padding={config.PrefixPaddingMs}ms");
                _logger.LogDebug($"Session config: {sessionUpdate}");

                var success = await SendMessageAsync(sessionUpdate);
                if (success)
                {
                    _logger.LogInformation("✅ Session configuration with noise suppression sent successfully");
                }
                else
                {
                    _logger.LogError("❌ Failed to send session configuration");
                }
                
                return success;
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
                var success = await SendMessageAsync(message);
                if (success)
                {
                    _logger.LogInformation("✅ Initial response started successfully");
                }
                else
                {
                    _logger.LogError("❌ Failed to start initial response");
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
                    _logger.LogWarning("⚠️ Cannot send message - WebSocket not connected");
                    return false;
                }

                if (string.IsNullOrWhiteSpace(message))
                {
                    _logger.LogWarning("⚠️ Cannot send empty message");
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
                    _logger.LogWarning("⚠️ Cannot receive message - WebSocket not connected");
                    return null;
                }

                byte[] buffer = new byte[1024 * 16];
                var receiveBuffer = new ArraySegment<byte>(buffer);
                StringBuilder messageBuilder = new StringBuilder();

                WebSocketReceiveResult result;
                do
                {
                    result = await _webSocket!.ReceiveAsync(receiveBuffer, cancellationToken);
                    
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var textReceived = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        messageBuilder.Append(textReceived);
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogWarning("🔚 WebSocket close message received");
                        return null;
                    }
                    else if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        _logger.LogDebug("📦 Binary message received, skipping...");
                        continue;
                    }
                }
                while (!result.EndOfMessage);

                string receivedMessage = messageBuilder.ToString();
                
                if (string.IsNullOrWhiteSpace(receivedMessage))
                {
                    _logger.LogDebug("📭 Received empty message from WebSocket");
                    return null;
                }

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
                    
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session completed", cts.Token);
                    
                    _logger.LogInformation("✅ WebSocket connection closed successfully");
                }
                else if (_webSocket?.State == WebSocketState.Connecting)
                {
                    _logger.LogInformation("🔗 Aborting connecting WebSocket");
                    _webSocket.Abort();
                }
                else if (_webSocket != null)
                {
                    _logger.LogInformation($"🔗 WebSocket in state {_webSocket.State}, disposing...");
                }
                
                _webSocket?.Dispose();
                _webSocket = null;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error closing WebSocket connection");
                _webSocket?.Dispose();
                _webSocket = null;
                return false;
            }
        }
    }
}
