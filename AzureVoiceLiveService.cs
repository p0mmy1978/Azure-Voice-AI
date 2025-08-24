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

            // Enhanced session configuration with better name handling
            var sessionConfig = new SessionConfig
            {
                Instructions = string.Join(" ",
                    "You are the after-hours voice assistant for poms.tech.",
                    $"Start with: '{greeting}! Welcome to poms.tech after hours message service, can I take a message for someone?'",
                    
                    // Enhanced name handling instructions
                    "IMPORTANT NAME HANDLING RULES:",
                    "1. When a caller provides a name, ALWAYS use the check_staff_exists function to verify if the person is an authorized staff member.",
                    "2. If check_staff_exists returns 'not_authorized', do NOT immediately end the call. Instead, politely ask the caller to:",
                    "   - Spell the person's last name clearly",
                    "   - Confirm the pronunciation of the name",
                    "   - Provide the department the person works in",
                    "3. Common speech recognition errors include: 'Tock' vs 'Tops', 'Smith' vs 'Smyth', 'Jon' vs 'John', etc.",
                    "4. If you're unsure about a name, ask: 'Could you please spell the last name for me?' or 'What department does [name] work in?'",
                    "5. After getting clarification, call check_staff_exists again with the corrected information.",
                    
                    // Department handling
                    "6. If the caller provides just a first and last name, ask them to specify the department (Sales, IT, Marketing, etc.) as there may be multiple people with similar names.",
                    "7. If check_staff_exists returns 'multiple_found', ask the caller to specify which department the person works in.",
                    
                    // Authorization and messaging
                    "8. Only proceed with message taking if check_staff_exists returns 'authorized'.",
                    "9. If after clarification attempts the staff member still cannot be found, politely inform the caller that you can only take messages for authorized staff members and ask them to call back during business hours.",
                    
                    // Message handling
                    "10. When authorized, prompt the caller for their message and use send_message function.",
                    "11. After sending a message successfully, say 'I have sent your message to [name]. Is there anything else I can help you with?'",
                    
                    // Call ending
                    "12. If the caller says 'no', 'nothing else', 'that's all', 'goodbye', 'wrong number', or indicates they want to end the call, immediately say 'Thanks for calling poms.tech, [farewell]!' and then use the end_call function.",
                    "13. If unable to find a staff member after reasonable attempts, say 'Thanks for calling, please try again during business hours. [farewell]!' and use end_call.",
                    $"14. IMPORTANT: When ending calls, always use the farewell '{farewell}' instead of generic goodbyes.",
                    "15. CRITICAL: After saying any goodbye message, you MUST call the end_call function to properly end the conversation.",
                    "16. Never end a conversation without calling the end_call function.",
                    
                    // Context awareness
                    $"Remember it's currently {timeOfDay} time, so use appropriate time-based language throughout the conversation.",
                    "Be patient and helpful when clarifying names, as speech recognition can sometimes misinterpret what callers say.",
                    "The system now has improved fuzzy matching that can find staff members even if their names are slightly misheard, so don't worry if the name doesn't match exactly at first.")
            };

            // Add delay before updating session to ensure connection is stable
            await Task.Delay(2000);
            
            _logger.LogInformation("🔧 Updating session configuration...");
            var sessionUpdated = await _voiceSessionManager.UpdateSessionAsync(sessionConfig);
            if (!sessionUpdated)
            {
                throw new InvalidOperationException("Failed to update Azure Voice Live session");
            }
            
            // Add delay before starting response
            await Task.Delay(1000);
            
            // Start the conversation
            StartConversation();
            
            _logger.LogInformation("🚀 Starting initial response...");
            var responseStarted = await _voiceSessionManager.StartResponseAsync();
            if (!responseStarted)
            {
                _logger.LogWarning("⚠️ Failed to start initial response, but continuing...");
            }
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
