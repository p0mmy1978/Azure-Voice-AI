using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CallAutomation.AzureAI.VoiceLive.Models;
using CallAutomation.AzureAI.VoiceLive.Services.Interfaces;
using CallAutomation.AzureAI.VoiceLive.Helpers;

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

                // CORRECTED: Azure Voice Live API endpoint format
                var websocketUrl = new Uri($"{endpoint.Replace("https", "wss")}/voice-agent/realtime?api-version=2025-05-01-preview&x-ms-client-request-id={Guid.NewGuid()}&model={model}&api-key={apiKey}");

                _webSocket = new ClientWebSocket();
                
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

                // Get time-based greetings - THIS IS THE KEY FIX
                var greeting = TimeOfDayHelper.GetGreeting();
                var farewell = TimeOfDayHelper.GetFarewell();
                var timeOfDay = TimeOfDayHelper.GetTimeOfDay();

                _logger.LogInformation($"🕐 Setting up session with: greeting='{greeting}', farewell='{farewell}', timeOfDay='{timeOfDay}'");

                // FIXED: Enhanced session configuration with ACTUAL farewell values embedded
                var sessionObject = new
                {
                    type = "session.update",
                    session = new
                    {
                        instructions = string.Join(" ",
                            "You are the after-hours voice assistant for poms.tech.",
                            $"Start with: '{greeting}! Welcome to poms.tech after hours message service, can I take a message for someone?'",
                            
                            // CRITICAL: Department preservation rules
                            "DEPARTMENT PRESERVATION RULES:",
                            "1. When check_staff_exists returns 'authorized' with a department specified, YOU MUST remember that department for the entire conversation with that person.",
                            "2. When calling send_message, ALWAYS include the department that was used in the successful check_staff_exists call.",
                            "3. NEVER call send_message without the department if a department was used during staff verification.",
                            "4. Track the department context throughout the conversation - do not lose it between function calls.",
                            
                            "DETAILED WORKFLOW:",
                            "Step 1: User asks to send message to [Name]",
                            "Step 2: Call check_staff_exists with name (and department if user provided it)",
                            "Step 3a: If result is 'authorized' - remember the department that was used and proceed to get message",
                            "Step 3b: If result shows multiple departments available, ask user to specify department",
                            "Step 4: If user specifies department, call check_staff_exists again with name AND department",
                            "Step 5: When 'authorized', remember the EXACT department that made it authorized",
                            "Step 6: Get message content from user",  
                            "Step 7: Call send_message with name, message, AND the department that was authorized in steps 3a or 5",
                            
                            // Enhanced name handling with confirmation flow
                            "NAME HANDLING RULES:",
                            "1. When a caller provides a name, ALWAYS use the check_staff_exists function first.",
                            "2. If check_staff_exists returns 'authorized', proceed with taking the message.",
                            "3. If check_staff_exists returns 'not_authorized', politely ask the caller to spell the name or provide the department.",
                            "4. If check_staff_exists returns a message starting with 'confirm:', parse it and ask for confirmation.",
                            "5. Only proceed with message taking after getting 'authorized' from either check_staff_exists or confirm_staff_match.",
                            
                            // Message handling
                            "MESSAGE HANDLING:",
                            "1. After staff verification is successful, ask: 'What message would you like me to send to [Name] in [Department]?'",
                            "2. Use send_message function with name, message, AND department.",
                            "3. After sending successfully, say 'I have sent your message to [Name] in [Department]. Is there anything else I can help you with?'",
                            
                            // FIXED: Call ending with ACTUAL time-of-day farewell embedded
                            "CALL ENDING - CRITICAL FAREWELL INSTRUCTIONS:",
                            $"1. When the caller says 'no', 'nothing else', 'that's all', 'goodbye', etc., you MUST say EXACTLY: 'Thanks for calling poms.tech, {farewell}!' and then use the end_call function.",
                            $"2. The current time-appropriate farewell is: '{farewell}' - use this EXACT phrase.",
                            $"3. Your mandatory goodbye message format: 'Thanks for calling poms.tech, {farewell}!'",
                            "4. CRITICAL: Always call end_call immediately after saying the goodbye message.",
                            $"5. FORBIDDEN: Never say 'goodbye' - always use the specific farewell: '{farewell}'",
                            $"6. DOUBLE CHECK: The farewell phrase is '{farewell}' - memorize this and use it exactly.",
                            
                            "CORRECT FAREWELL EXAMPLES (use these exact formats):",
                            $"✅ CORRECT: 'Thanks for calling poms.tech, {farewell}!'",
                            $"✅ CORRECT: 'Thank you for calling poms.tech, {farewell}!'", 
                            $"✅ CORRECT: 'Thanks for using poms.tech after hours service, {farewell}!'",
                            "❌ WRONG: 'Thanks for calling poms.tech, goodbye!'",
                            "❌ WRONG: 'Thanks for calling, bye!'",
                            "❌ WRONG: Any variation with 'goodbye', 'bye', 'farewell', or generic closings",
                            
                            $"MEMORY AID: Current farewell = '{farewell}'. Use this exact phrase every time.",
                            $"TIME CONTEXT: It is currently {timeOfDay} time, so '{farewell}' is the appropriate closing.",
                            
                            $"Remember: greeting='{greeting}', farewell='{farewell}', time={timeOfDay}.",
                            "The system helps find staff even with speech recognition errors, but always preserve department context between function calls.",
                            "DEPARTMENT CONTEXT IS CRITICAL - Never call send_message without the department if one was used during verification!"),
                        
                        // CORRECTED: Azure Voice Live VAD configuration
                        turn_detection = new
                        {
                            type = "azure_semantic_vad",
                            threshold = 0.4,
                            prefix_padding_ms = 150,
                            silence_duration_ms = 150,
                            remove_filler_words = true,
                            min_speech_duration_ms = 100,
                            max_silence_for_turn_ms = 800
                        },
                        
                        // CORRECTED: Azure Voice Live specific audio processing
                        input_audio_noise_reduction = new 
                        { 
                            type = "azure_deep_noise_suppression"
                        },
                        
                        input_audio_echo_cancellation = new 
                        { 
                            type = "server_echo_cancellation"
                        },
                        
                        // CORRECTED: Proper Azure TTS voice configuration
                        voice = new
                        {
                            name = "en-US-EmmaNeural",  // FIXED: Correct Azure Emma voice name
                            type = "azure-standard",
                            temperature = config.VoiceTemperature
                        },
                        
                        // Audio format settings
                        input_audio_format = "pcm16",
                        output_audio_format = "pcm16",
                        input_audio_sampling_rate = 24000,
                        
                        // ENHANCED: Function tools with department preservation emphasis
                        tools = new object[]
                        {
                            new {
                                type = "function",
                                name = "check_staff_exists",
                                description = "Check if a staff member is authorized to receive messages. Returns 'authorized', 'not_authorized', or lists available departments for duplicates. Remember the department used when this returns 'authorized'.",
                                parameters = new {
                                    type = "object",
                                    properties = new {
                                        name = new { 
                                            type = "string", 
                                            description = "The name of the person to check. Will be fuzzy matched if not found exactly." 
                                        },
                                        department = new { 
                                            type = "string", 
                                            description = "The department the person works in. REQUIRED when multiple people have the same name." 
                                        }
                                    },
                                    required = new[] { "name" }
                                }
                            },
                            new {
                                type = "function", 
                                name = "confirm_staff_match",
                                description = "Confirm a fuzzy match suggestion when check_staff_exists returns a confirmation request.",
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
                                description = "Send a message to a staff member after verification. CRITICAL: Must include the department if it was used during check_staff_exists verification.",
                                parameters = new {
                                    type = "object",
                                    properties = new {
                                        name = new { 
                                            type = "string", 
                                            description = "The exact name of the person to send the message to (must match the verified name)" 
                                        },
                                        message = new { 
                                            type = "string", 
                                            description = "The message content from the caller" 
                                        },
                                        department = new { 
                                            type = "string", 
                                            description = "The department the person works in. REQUIRED if a department was used during check_staff_exists verification. This ensures the message goes to the correct person when there are multiple staff with the same name." 
                                        }
                                    },
                                    required = new[] { "name", "message" }
                                }
                            },
                            new {
                                type = "function",
                                name = "end_call",
                                description = $"End the call gracefully. This should only be called AFTER saying the goodbye message: 'Thanks for calling poms.tech, {farewell}!'",
                                parameters = new {
                                    type = "object", 
                                    properties = new { },
                                    required = new string[] { }
                                }
                            }
                        }
                    }
                };

                var jsonOptions = new JsonSerializerOptions 
                { 
                    WriteIndented = false,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                };
                
                var sessionUpdate = JsonSerializer.Serialize(sessionObject, jsonOptions);
                
                _logger.LogInformation($"🔧 Updating Azure Voice Live session with EMBEDDED time-of-day farewell...");
                _logger.LogInformation($"🎯 Voice: en-US-EmmaNeural (azure-standard), Temp: {config.VoiceTemperature}");
                _logger.LogInformation($"🎤 VAD: azure_semantic_vad, threshold=0.4, timing=150ms");
                _logger.LogInformation($"🔇 Noise Suppression: azure_deep_noise_suppression enabled");
                _logger.LogInformation($"👋 Embedded farewell phrase: '{farewell}'");
                _logger.LogInformation($"📊 Config size: {sessionUpdate.Length} chars");

                var success = await SendMessageAsync(sessionUpdate);
                
                var updateDuration = DateTime.Now - updateStart;
                
                if (success)
                {
                    _logger.LogInformation($"✅ Azure Voice Live session configured successfully in {updateDuration.TotalMilliseconds:F0}ms with farewell: '{farewell}'");
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
