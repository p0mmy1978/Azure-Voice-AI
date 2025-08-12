using System.Text.Json;
using Azure.Communication.CallAutomation;
using CallAutomation.AzureAI.VoiceLive.Models;
using CallAutomation.AzureAI.VoiceLive.Services.Interfaces;

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

            // Connect to Azure Voice Live
            var connected = await _voiceSessionManager.ConnectAsync(azureVoiceLiveEndpoint!, azureVoiceLiveApiKey!, voiceLiveModel!);
            if (!connected)
            {
                throw new InvalidOperationException("Failed to connect to Azure Voice Live");
            }

            // Configure the session
            var sessionConfig = new SessionConfig
            {
                Instructions = string.Join(" ",
                    "You are the after-hours voice assistant for poms.tech.",
                    "Start with: 'Welcome to poms.tech after hours message service, can I take a message for someone?'",
                    "When a caller provides a name, always use the check_staff_exists function to verify if the person is an authorized staff member before proceeding.",
                    "If the caller provides just a first and last name (like 'John Smith'), ask them to specify the department (Sales, IT, etc.) as there may be multiple people with the same name.",
                    "If check_staff_exists returns 'authorized', prompt the caller for their message and use send_message.",
                    "If check_staff_exists returns 'not_authorized', politely inform the caller that you can only take messages for authorized staff members and ask them to call back during business hours, then say 'Thanks for calling, have a great day!' and immediately use the end_call function.",
                    "If check_staff_exists returns 'multiple_found', ask the caller to specify which department the person works in.",
                    "After sending a message successfully, say 'I have sent your message to [name]. Is there anything else I can help you with?'",
                    "If the caller says 'no', 'nothing else', 'that's all', 'goodbye', 'wrong number', or indicates they want to end the call, immediately say 'Thanks for calling poms.tech, have a great day!' and then use the end_call function.",
                    "IMPORTANT: After saying any goodbye message, you MUST call the end_call function to properly end the conversation.",
                    "Never end a conversation without calling the end_call function.")
            };

            await _voiceSessionManager.UpdateSessionAsync(sessionConfig);
            
            // Start the conversation
            StartConversation();
            await _voiceSessionManager.StartResponseAsync();
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
                    if (receivedMessage == null) continue;

                    _logger.LogInformation($"📥 Received: {receivedMessage}");

                    var jsonDoc = JsonDocument.Parse(receivedMessage);
                    var root = jsonDoc.RootElement;

                    if (root.TryGetProperty("type", out var typeElement))
                    {
                        var messageType = typeElement.GetString();

                        switch (messageType)
                        {
                            case "response.audio.delta":
                                var delta = root.GetProperty("delta").GetString();
                                await _audioStreamProcessor.ProcessAudioDeltaAsync(delta!, m_mediaStreaming);
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
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error processing messages");
            }
        }

        private async Task HandleFunctionCall(JsonElement root)
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
                _logger.LogInformation("🔚 Call ending requested");
            }
        }

        private void HandleOutputItem()
        {
            if (m_isEndingCall && !m_goodbyeMessageStarted)
            {
                m_goodbyeMessageStarted = true;
                m_goodbyeStartTime = DateTime.Now;
                _logger.LogInformation("🎤 Goodbye message started");
            }
        }

        private async Task HandleResponseCompletion()
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

        private int CalculateGoodbyeDelay()
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

        private async Task EndCall()
        {
            var success = await _callManagementService.HangUpCallAsync(m_callerId);
            if (!success)
            {
                _logger.LogWarning($"⚠️ Failed to hang up call for: {m_callerId}");
            }
            await Close();
        }

        public async Task SendAudioToExternalAI(byte[] data)
        {
            await _audioStreamProcessor.SendAudioToExternalAIAsync(data, _voiceSessionManager.SendMessageAsync);
        }

        public async Task Close()
        {
            try
            {
                m_cts.Cancel();
                m_cts.Dispose();
                await _voiceSessionManager.CloseAsync();
                _logger.LogInformation("🔚 AzureVoiceLiveService closed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error closing AzureVoiceLiveService");
            }
        }
    }
}
