using Azure.Communication.CallAutomation;
using CallAutomation.AzureAI.VoiceLive.Models;
using CallAutomation.AzureAI.VoiceLive.Services.Interfaces;
using CallAutomation.AzureAI.VoiceLive.Services.Voice;
using CallAutomation.AzureAI.VoiceLive.Helpers;

namespace CallAutomation.AzureAI.VoiceLive
{
    public class AzureVoiceLiveService
    {
        private readonly AcsMediaStreamingHandler _mediaStreaming;
        private readonly IConfiguration _configuration;
        private readonly ILogger<AzureVoiceLiveService> _logger;
        private readonly string _callerId;
        
        // Core services
        private readonly IVoiceSessionManager _voiceSessionManager;
        private readonly IAudioStreamProcessor _audioStreamProcessor;
        private readonly MessageProcessor _messageProcessor;
        private readonly CallFlowManager _callFlowManager;
        
        // Processing control
        private readonly CancellationTokenSource _cancellationTokenSource;
        
        // Session state management
        private volatile bool _sessionReady = false;
        private readonly object _sessionLock = new object();
        private readonly Queue<byte[]> _pendingAudioBuffer = new Queue<byte[]>();
        private const int MAX_PENDING_AUDIO_PACKETS = 100; // Limit buffered audio to prevent memory issues

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
            IVoiceSessionManager voiceSessionManager,
            ILoggerFactory loggerFactory)
        {
            _mediaStreaming = mediaStreaming;
            _configuration = configuration;
            _logger = logger;
            _callerId = callerId;
            _voiceSessionManager = voiceSessionManager;
            _audioStreamProcessor = audioStreamProcessor;
            _cancellationTokenSource = new CancellationTokenSource();
            
            _logger.LogInformation($"🎯 AzureVoiceLiveService initializing for: {_callerId}");
            
            // Initialize call management
            callManagementService.Initialize(callAutomationClient, activeCallConnections);
            
            // Create specialized processors with correctly typed loggers
            _messageProcessor = new MessageProcessor(
                functionCallProcessor, 
                audioStreamProcessor, 
                voiceSessionManager, 
                loggerFactory.CreateLogger<MessageProcessor>(), 
                callerId);
                
            _callFlowManager = new CallFlowManager(
                callManagementService,
                voiceSessionManager,
                loggerFactory.CreateLogger<CallFlowManager>(),
                callerId);
            
            // Start the AI session initialization
            _ = Task.Run(async () => await InitializeAISessionAsync());
        }

        private async Task InitializeAISessionAsync()
        {
            try
            {
                _logger.LogInformation("🚀 Starting AI session initialization...");
                
                // Get configuration
                var config = GetVoiceConfiguration();
                if (!ValidateConfiguration(config.ApiKey, config.Endpoint, config.Model))
                {
                    throw new InvalidOperationException("Invalid Azure Voice Live configuration");
                }

                // Connect to Azure Voice Live
                _logger.LogInformation("🔗 Connecting to Azure Voice Live...");
                var connected = await _voiceSessionManager.ConnectAsync(config.Endpoint!, config.ApiKey!, config.Model!);
                if (!connected)
                {
                    throw new InvalidOperationException("Failed to connect to Azure Voice Live");
                }

                // Configure session
                _logger.LogInformation("🔧 Configuring AI session...");
                var sessionConfig = CreateSessionConfig();
                var sessionUpdated = await _voiceSessionManager.UpdateSessionAsync(sessionConfig);
                if (!sessionUpdated)
                {
                    throw new InvalidOperationException("Failed to update Azure Voice Live session");
                }

                // Wait a moment for session to be fully established
                await Task.Delay(500);

                // Mark session as ready BEFORE starting conversation processing
                lock (_sessionLock)
                {
                    _sessionReady = true;
                    _logger.LogInformation("✅ Session marked as ready - audio processing enabled");
                }

                // Process any buffered audio data
                await ProcessPendingAudioAsync();

                // Start conversation processing
                _logger.LogInformation("💬 Starting conversation processing...");
                StartConversationProcessing();
                
                // Start initial response
                await Task.Delay(200); // Brief pause for stability
                var responseStarted = await _voiceSessionManager.StartResponseAsync();
                if (!responseStarted)
                {
                    _logger.LogWarning("⚠️ Failed to start initial response, but continuing...");
                }
                
                _logger.LogInformation("✅ AI session initialization completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ AI session initialization failed");
                throw;
            }
        }

        private async Task ProcessPendingAudioAsync()
        {
            lock (_sessionLock)
            {
                if (_pendingAudioBuffer.Count > 0)
                {
                    _logger.LogInformation($"🎤 Processing {_pendingAudioBuffer.Count} buffered audio packets");
                }
            }

            var processedCount = 0;
            while (true)
            {
                byte[]? audioData;
                lock (_sessionLock)
                {
                    if (_pendingAudioBuffer.Count == 0)
                        break;
                    audioData = _pendingAudioBuffer.Dequeue();
                }

                try
                {
                    await _audioStreamProcessor.SendAudioToExternalAIAsync(audioData, _voiceSessionManager.SendMessageAsync);
                    processedCount++;
                    
                    // Small delay to avoid overwhelming the API
                    if (processedCount % 10 == 0)
                    {
                        await Task.Delay(10);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Error processing buffered audio data");
                    break;
                }
            }

            if (processedCount > 0)
            {
                _logger.LogInformation($"✅ Processed {processedCount} buffered audio packets");
            }
        }

        private (string? ApiKey, string? Endpoint, string? Model) GetVoiceConfiguration()
        {
            return (
                _configuration.GetValue<string>("AzureVoiceLiveApiKey"),
                _configuration.GetValue<string>("AzureVoiceLiveEndpoint"),
                _configuration.GetValue<string>("VoiceLiveModel")
            );
        }

        private bool ValidateConfiguration(string? apiKey, string? endpoint, string? model)
        {
            _logger.LogInformation("🔍 Validating configuration...");
            
            var isValid = true;
            
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogError("❌ AzureVoiceLiveApiKey is missing");
                isValid = false;
            }
            else
            {
                _logger.LogInformation($"✅ API Key: {apiKey.Substring(0, Math.Min(8, apiKey.Length))}... ({apiKey.Length} chars)");
            }
            
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                _logger.LogError("❌ AzureVoiceLiveEndpoint is missing");
                isValid = false;
            }
            else if (!endpoint.StartsWith("https://"))
            {
                _logger.LogError("❌ AzureVoiceLiveEndpoint should start with https://");
                isValid = false;
            }
            else
            {
                _logger.LogInformation($"✅ Endpoint: {endpoint}");
            }
            
            if (string.IsNullOrWhiteSpace(model))
            {
                _logger.LogError("❌ VoiceLiveModel is missing");
                isValid = false;
            }
            else
            {
                _logger.LogInformation($"✅ Model: {model}");
            }
            
            return isValid;
        }

        private SessionConfig CreateSessionConfig()
        {
            var greeting = TimeOfDayHelper.GetGreeting();
            var farewell = TimeOfDayHelper.GetFarewell();
            var timeOfDay = TimeOfDayHelper.GetTimeOfDay();

            _logger.LogInformation($"🕐 Session config - Time: {timeOfDay}, Greeting: '{greeting}', Farewell: '{farewell}'");

            return new SessionConfig
            {
                VoiceTemperature = 0.8,
                Instructions = $"AI assistant for poms.tech after hours. Time: {timeOfDay}. Greeting: '{greeting}'. Farewell: '{farewell}'."
            };
        }

        public void StartConversationProcessing()
        {
            _ = Task.Run(async () => await ProcessMessagesAsync(_cancellationTokenSource.Token));
        }

        private async Task ProcessMessagesAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("🔄 Starting message processing loop...");
                
                while (_voiceSessionManager.IsConnected && !cancellationToken.IsCancellationRequested)
                {
                    var receivedMessage = await _voiceSessionManager.ReceiveMessageAsync(cancellationToken);
                    
                    if (string.IsNullOrWhiteSpace(receivedMessage))
                    {
                        continue;
                    }

                    // Process the message using MessageProcessor
                    var processed = await _messageProcessor.ProcessMessageAsync(receivedMessage, _mediaStreaming);
                    
                    // Check if call should end after processing
                    if (_messageProcessor.IsEndingCall && processed)
                    {
                        _logger.LogInformation("📞 Message processor indicates call should end");
                        await _callFlowManager.EndCallAsync();
                        break;
                    }
                }
                
                _logger.LogInformation("🔄 Message processing loop ended");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("🛑 Message processing cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error in message processing");
                
                // Try to reconnect if connection was lost
                if (!_voiceSessionManager.IsConnected)
                {
                    _logger.LogWarning("🔗 Connection lost, attempting graceful close");
                    await Close();
                }
            }
        }

        public async Task SendAudioToExternalAI(byte[] data)
        {
            try
            {
                // Check if session is ready
                lock (_sessionLock)
                {
                    if (!_sessionReady)
                    {
                        // Buffer audio data until session is ready
                        if (_pendingAudioBuffer.Count < MAX_PENDING_AUDIO_PACKETS)
                        {
                            _pendingAudioBuffer.Enqueue(data);
                            _logger.LogDebug($"🎤 Buffered audio packet (queue size: {_pendingAudioBuffer.Count})");
                        }
                        else
                        {
                            // Remove oldest packet to make room for new one
                            _pendingAudioBuffer.Dequeue();
                            _pendingAudioBuffer.Enqueue(data);
                            _logger.LogDebug($"🎤 Buffered audio packet (queue full, replaced oldest)");
                        }
                        return;
                    }
                }

                // Session is ready, process audio immediately
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
                _logger.LogInformation("🛑 Closing AzureVoiceLiveService...");
                
                // Mark session as not ready
                lock (_sessionLock)
                {
                    _sessionReady = false;
                    _pendingAudioBuffer.Clear(); // Clear any pending audio
                }
                
                _cancellationTokenSource.Cancel();
                
                // Use CallFlowManager for proper cleanup
                await _callFlowManager.EndCallAsync();
                
                _cancellationTokenSource.Dispose();
                
                _logger.LogInformation("✅ AzureVoiceLiveService closed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error closing AzureVoiceLiveService");
            }
        }

        /// <summary>
        /// Get service status for debugging
        /// </summary>
        public (bool IsConnected, bool IsCallActive, bool IsEndingCall, bool SessionReady, int BufferedPackets) GetServiceStatus()
        {
            lock (_sessionLock)
            {
                return (
                    _voiceSessionManager.IsConnected,
                    _callFlowManager.IsCallActive(),
                    _messageProcessor.IsEndingCall,
                    _sessionReady,
                    _pendingAudioBuffer.Count
                );
            }
        }
    }
}
