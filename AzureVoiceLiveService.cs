using System.Text.Json;
using Azure.Communication.CallAutomation;
using CallAutomation.AzureAI.VoiceLive.Models;
using CallAutomation.AzureAI.VoiceLive.Services.Interfaces;
using CallAutomation.AzureAI.VoiceLive.Helpers;

namespace CallAutomation.AzureAI.VoiceLive
{
    public class AzureVoiceLiveService
    {
        private CancellationTokenSource m_cts;
        private AcsMediaStreamingHandler m_mediaStreaming;
        private readonly IConfiguration m_configuration;
        private readonly ILogger<AzureVoiceLiveService> _logger;
        private bool m_isEndingCall = false;
        private readonly string m_callerId;
        private DateTime m_goodbyeStartTime;
        private bool m_goodbyeMessageStarted = false;
        
        // Services
        private readonly IVoiceSessionManager _voiceSessionManager;
        private readonly ICallManagementService _callManagementService;
        private readonly IFunctionCallProcessor _functionCallProcessor;
        private readonly IAudioStreamProcessor _audioStreamProcessor;

        public AzureVoiceLiveService(
            AcsMediaStreamingHandler mediaStreaming, 
            IConfiguration configuration, 
            ILogger<AzureVoiceLiveService> logger, 
            string callerId, 
            CallAutomationClient callAutomationClient, 
            Dictionary<string, string> activeCallConnections,
            IStaffLookupService staffLookupService,
            IEmailService emailService,
            ICallManagementService callManagementService,
            IFunctionCallProcessor functionCallProcessor,
            IAudioStreamProcessor audioStreamProcessor,
            IVoiceSessionManager voiceSessionManager)
        {
            m_mediaStreaming = mediaStreaming;
            m_cts = new CancellationTokenSource();
            m_configuration = configuration;
            _logger = logger;
            m_callerId = callerId;
            _callManagementService = callManagementService;
            _functionCallProcessor = functionCallProcessor;
            _audioStreamProcessor = audioStreamProcessor;
            _voiceSessionManager = voiceSessionManager;
            
            _logger.LogInformation($"🎯 AzureVoiceLiveService initialized with Caller ID: {m_callerId}");
            
            // Initialize services
            _callManagementService.Initialize(callAutomationClient, activeCallConnections);
            
            // Start the AI session
            InitializeAISessionAsync().GetAwaiter().GetResult();
        }

        private async Task InitializeAISessionAsync()
        {
            var azureVoiceLiveApiKey = m_configuration.GetValue<string>("AzureVoiceLiveApiKey");
            var azureVoiceLiveEndpoint = m_configuration.GetValue<string>("AzureVoiceLiveEndpoint");
            var voiceLiveModel = m_configuration.GetValue<string>("VoiceLiveModel");
            var systemPrompt = m_configuration.GetValue<string>("SystemPrompt") ?? "You are an AI assistant that helps people find information.";

            // Validate configuration
            if (!ValidateConfiguration(azureVoiceLiveApiKey, azureVoiceLiveEndpoint, voiceLiveModel))
            {
                throw new InvalidOperationException("Invalid Azure Voice Live configuration");
            }

            // Connect to Azure Voice Live
            var connected = await _voiceSessionManager.ConnectAsync(azureVoiceLiveEndpoint!, azureVoiceLiveApiKey!, voiceLiveModel!);
            if (!connected)
            {
                throw new InvalidOperationException("Failed to connect to Azure Voice Live");
            }

            // Get time-based greetings
            var greeting = TimeOfDayHelper.GetGreeting();
            var farewell = TimeOfDayHelper.GetFarewell();
            var timeOfDay = TimeOfDayHelper.GetTimeOfDay();

            _logger.LogInformation($"🕐 Current time of day: {timeOfDay}, using greeting: '{greeting}', farewell: '{farewell}'");

            // Enhanced session configuration with better name handling and confirmation flow
            var sessionConfig = new SessionConfig
            {
                Instructions = string.Join(" ",
                    "You are the after-hours voice assistant for poms.tech.",
                    $"Start with: '{greeting}! Welcome to poms.tech after hours message service, can I take a message for someone?'",
                    
                    // Enhanced name handling with confirmation flow
                    "IMPORTANT NAME HANDLING RULES:",
                    "1. When a caller provides a name, ALWAYS use the check_staff_exists function first.",
                    "2. If check_staff_exists returns 'authorized', proceed with taking the message.",
                    "3. If check_staff_exists returns 'not_authorized', politely ask the caller to:",
                    "   - Spell the person's last name clearly",
                    "   - Confirm the pronunciation",
                    "   - Provide the department the person works in",
                    "   Then call check_staff_exists again with the clarified information.",
                    
                    // NEW: Handle confirmation requests from fuzzy matching
                    "4. If check_staff_exists returns a message starting with 'confirm:', this means the system found a similar name but needs confirmation.",
                    "   - Parse the response format: 'confirm:originalName:suggestedName:department:confidence'",
                    "   - Say: 'I couldn't find [originalName] exactly, but I did find [suggestedName] in [department]. Did you mean [suggestedName]?'",
                    "   - Wait for the caller's response (yes/no).",
                    "   - If they say YES: call confirm_staff_match with the original name, suggested name, and department.",
                    "   - If they say NO: ask them to spell the name or try again.",
                    
                    "5. Only proceed with message taking after getting 'authorized' from either check_staff_exists or confirm_staff_match.",
                    "6. If multiple attempts fail, politely say the person couldn't be found and ask them to call during business hours.",
                    
                    // Message handling (unchanged)
                    "7. When authorized, prompt for the message and use send_message function.",
                    "8. After sending successfully, say 'I have sent your message to [name]. Is there anything else I can help you with?'",
                    
                    // Call ending (unchanged)
                    "9. If the caller says 'no', 'nothing else', 'that's all', 'goodbye', etc., say 'Thanks for calling poms.tech, [farewell]!' and use end_call.",
                    "10. CRITICAL: Always call end_call after any goodbye message.",
                    
                    // Examples of the new confirmation flow
                    "EXAMPLE CONFIRMATION FLOW:",
                    "User: 'I need to leave a message for Terry Tock'",
                    "You: [call check_staff_exists with name='Terry Tock']",
                    "System returns: 'confirm:Terry Tock:Terry Tops:IT:0.78'",
                    "You: 'I couldn't find Terry Tock exactly, but I found Terry Tops in IT. Did you mean Terry Tops?'",
                    "User: 'Yes, that's right'",
                    "You: [call confirm_staff_match with original_name='Terry Tock', confirmed_name='Terry Tops', department='IT']",
                    "System returns: 'authorized'",
                    "You: 'Great! What message would you like me to send to Terry Tops?'",
                    
                    $"Remember it's currently {timeOfDay} time. Be patient with name clarifications as speech recognition can misinterpret names.",
                    "The fuzzy matching system helps find staff even with speech recognition errors, but always confirm unclear matches with the caller.")
            };

            // OPTIMIZED: Reduced delay from 2000ms to 200ms - just enough for connection stability
            await Task.Delay(200);
            
            _logger.LogInformation("🔧 Updating session configuration...");
            
            // OPTIMIZED: Run session update and conversation start in parallel
            var sessionUpdateTask = _voiceSessionManager.UpdateSessionAsync(sessionConfig);
            
            // Start conversation processing immediately (don't wait for session update)
            StartConversation();
            
            // Wait for session update to complete
            var sessionUpdated = await sessionUpdateTask;
            if (!sessionUpdated)
            {
                throw new InvalidOperationException("Failed to update Azure Voice Live session");
            }
            
            // OPTIMIZED: Reduced delay from 1000ms to 100ms - just enough for session to be ready
            await Task.Delay(100);
            
            _logger.LogInformation("🚀 Starting initial response...");
            
            // OPTIMIZED: Don't wait for response start - let it happen asynchronously
            var responseStarted = _voiceSessionManager.StartResponseAsync();
            if (!await responseStarted)
            {
                _logger.LogWarning("⚠️ Failed to start initial response, but continuing...");
            }
            
            _logger.LogInformation("✅ AI Session initialization completed quickly");
        }

        private bool ValidateConfiguration(string? apiKey, string? endpoint, string? model)
        {
            _logger.LogInformation("🔍 Validating Azure Voice Live configuration...");
            
            var isValid = true;
            
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogError("❌ AzureVoiceLiveApiKey is missing or empty");
                isValid = false;
            }
            else if (apiKey.Length < 32)
            {
                _logger.LogWarning("⚠️ AzureVoiceLiveApiKey seems too short (expected 32+ characters)");
            }
            else
            {
                _logger.LogInformation($"✅ API Key: {apiKey.Substring(0, 8)}... ({apiKey.Length} chars)");
            }
            
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                _logger.LogError("❌ AzureVoiceLiveEndpoint is missing or empty");
                isValid = false;
            }
            else if (!endpoint.StartsWith("https://"))
            {
                _logger.LogError("❌ AzureVoiceLiveEndpoint should start with https://");
                isValid = false;
            }
            else if (!endpoint.Contains("cognitiveservices.azure.com"))
            {
                _logger.LogWarning("⚠️ AzureVoiceLiveEndpoint doesn't look like a standard Azure endpoint");
            }
            else
            {
                _logger.LogInformation($"✅ Endpoint: {endpoint}");
            }
            
            if (string.IsNullOrWhiteSpace(model))
            {
                _logger.LogError("❌ VoiceLiveModel is missing or empty");
                isValid = false;
            }
            else if (!model.Contains("gpt-4") && !model.Contains("realtime"))
            {
                _logger.LogWarning($"⚠️ VoiceLiveModel '{model}' doesn't look like a realtime model");
            }
            else
            {
                _logger.LogInformation($"✅ Model: {model}");
            }
            
            // Check for common configuration issues
            if (apiKey != null && endpoint != null)
            {
                var endpointHost = new Uri(endpoint).Host;
                if (apiKey.Contains(endpointHost.Split('.')[0]))
                {
                    _logger.LogWarning("⚠️ API Key appears to contain endpoint information - check your configuration");
                }
            }
            
            if (!isValid)
            {
                _logger.LogError("❌ Configuration validation failed. Please check your appsettings.json:");
                _logger.LogError("   Required: AzureVoiceLiveApiKey, AzureVoiceLiveEndpoint, VoiceLiveModel");
                _logger.LogError("   Example endpoint: https://your-resource.cognitiveservices.azure.com/");
                _logger.LogError("   Example model: gpt-4o-mini-realtime-preview");
            }
            else
            {
                _logger.LogInformation("✅ Configuration validation passed");
            }
            
            return isValid;
        }

        public void StartConversation()
        {
            _ = Task.Run(async () => await ProcessMessagesAsync(m_cts.Token));
        }

        private async Task ProcessMessagesAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (_voiceSessionManager.IsConnected && !cancellationToken.IsCancellationRequested)
                {
                    var receivedMessage = await _voiceSessionManager.ReceiveMessageAsync(cancellationToken);
                    
                    // Add null/empty check and logging
                    if (string.IsNullOrWhiteSpace(receivedMessage))
                    {
                        _logger.LogDebug("📭 Received empty message, continuing...");
                        continue;
                    }

                    _logger.LogInformation($"📥 Received: {receivedMessage}");

                    // Add JSON validation before parsing
                    JsonDocument? jsonDoc = null;
                    try
                    {
                        jsonDoc = JsonDocument.Parse(receivedMessage);
                    }
                    catch (JsonException jsonEx)
                    {
                        _logger.LogError(jsonEx, $"❌ Invalid JSON received: '{receivedMessage}'. Skipping message.");
                        continue;
                    }

                    var root = jsonDoc.RootElement;

                    if (root.TryGetProperty("type", out var typeElement))
                    {
                        var messageType = typeElement.GetString();
                        
                        _logger.LogDebug($"🔄 Processing message type: {messageType}");

                        switch (messageType)
                        {
                            case "response.audio.delta":
                                if (root.TryGetProperty("delta", out var deltaProperty))
                                {
                                    var delta = deltaProperty.GetString();
                                    if (!string.IsNullOrEmpty(delta))
                                    {
                                        await _audioStreamProcessor.ProcessAudioDeltaAsync(delta, m_mediaStreaming);
                                    }
                                }
                                break;

                            case "input_audio_buffer.speech_started":
                                await _audioStreamProcessor.HandleVoiceActivityAsync(true, m_mediaStreaming);
                                break;

                            case "response.function_call_arguments.done":
                                await HandleFunctionCall(root);
                                break;

                            case "response.output_item.added":
                                HandleOutputItem();
                                break;

                            case "response.done":
                            case "response.audio.done":
                                await HandleResponseCompletion();
                                break;

                            case "error":
                                if (root.TryGetProperty("error", out var errorProperty))
                                {
                                    _logger.LogError($"❌ Azure Voice Live error: {errorProperty}");
                                }
                                break;

                            case "session.created":
                            case "session.updated":
                            case "response.created":
                            case "conversation.item.created":
                            case "input_audio_buffer.speech_stopped":
                            case "input_audio_buffer.committed":
                                // These are status messages, log but don't process
                                _logger.LogDebug($"ℹ️ Status message: {messageType}");
                                break;

                            default:
                                _logger.LogDebug($"🔄 Unhandled message type: {messageType}");
                                break;
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"⚠️ Message missing 'type' property: {receivedMessage}");
                    }

                    // Dispose the JSON document
                    jsonDoc?.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("🔚 ProcessMessagesAsync cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error processing messages");
                
                // Try to reconnect if connection was lost
                if (!_voiceSessionManager.IsConnected)
                {
                    _logger.LogWarning("🔗 WebSocket disconnected, attempting to close gracefully");
                    await Close();
                }
            }
        }

        private async Task HandleFunctionCall(JsonElement root)
        {
            try
            {
                var functionName = root.GetProperty("name").GetString();
                var callId = root.GetProperty("call_id").GetString();
                var args = root.GetProperty("arguments").ToString();
                
                _logger.LogInformation($"🔧 Function call: {functionName}, Call ID: {callId}");

                var functionResult = await _functionCallProcessor.ProcessFunctionCallAsync(functionName!, args, callId!, m_callerId);
                await _functionCallProcessor.SendFunctionResponseAsync(callId!, functionResult.Output, _voiceSessionManager.SendMessageAsync);
                
                if (functionResult.ShouldEndCall)
                {
                    m_isEndingCall = true;
                    var farewell = TimeOfDayHelper.GetFarewell();
                    _logger.LogInformation($"🔚 Call ending requested - will use farewell: '{farewell}'");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error handling function call");
            }
        }

        private void HandleOutputItem()
        {
            try
            {
                if (m_isEndingCall && !m_goodbyeMessageStarted)
                {
                    m_goodbyeMessageStarted = true;
                    m_goodbyeStartTime = DateTime.Now;
                    var farewell = TimeOfDayHelper.GetFarewell();
                    _logger.LogInformation($"🎤 Goodbye message started with time-appropriate farewell: '{farewell}'");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error handling output item");
            }
        }

        private async Task HandleResponseCompletion()
        {
            try
            {
                if (m_isEndingCall)
                {
                    _logger.LogInformation("🔚 AI response completed - ending call");
                    
                    var delay = CalculateGoodbyeDelay();
                    _logger.LogInformation($"⏱️ Waiting {delay}ms for goodbye to complete");
                    
                    await Task.Delay(delay);
                    await EndCall();
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error handling response completion");
            }
        }

        private int CalculateGoodbyeDelay()
        {
            try
            {
                if (m_goodbyeMessageStarted)
                {
                    var elapsed = DateTime.Now - m_goodbyeStartTime;
                    var estimatedDuration = TimeSpan.FromSeconds(5);
                    var remaining = estimatedDuration - elapsed;
                    
                    return remaining.TotalMilliseconds > 0 
                        ? (int)remaining.TotalMilliseconds + 1500 
                        : 1000;
                }
                return 6000;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error calculating goodbye delay");
                return 3000; // Default fallback
            }
        }

        private async Task EndCall()
        {
            try
            {
                var success = await _callManagementService.HangUpCallAsync(m_callerId);
                if (!success)
                {
                    _logger.LogWarning($"⚠️ Failed to hang up call for: {m_callerId}");
                }
                await Close();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error ending call");
                await Close(); // Ensure cleanup happens
            }
        }

        public async Task SendAudioToExternalAI(byte[] data)
        {
            try
            {
                await _audioStreamProcessor.SendAudioToExternalAIAsync(data, _voiceSessionManager.SendMessageAsync);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error sending audio to external AI");
            }
        }

        public async Task Close()
        {
            try
            {
                _logger.LogInformation("🔚 Closing AzureVoiceLiveService...");
                
                m_cts.Cancel();
                m_cts.Dispose();
                await _voiceSessionManager.CloseAsync();
                
                _logger.LogInformation("✅ AzureVoiceLiveService closed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error closing AzureVoiceLiveService");
            }
        }
    }
}
