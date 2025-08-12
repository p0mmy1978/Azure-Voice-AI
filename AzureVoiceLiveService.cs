using System.Net.WebSockets;
using Azure.Communication.CallAutomation;
using System.Text;
using System.Text.Json;
using CallAutomation.AzureAI.VoiceLive.Models;
using CallAutomation.AzureAI.VoiceLive.Services.Interfaces;

namespace CallAutomation.AzureAI.VoiceLive
{
    public class AzureVoiceLiveService
    {
        private CancellationTokenSource m_cts;
        private AcsMediaStreamingHandler m_mediaStreaming;
        private string m_answerPromptSystemTemplate = "You are an AI assistant that helps people find information.";
        private ClientWebSocket m_azureVoiceLiveWebsocket = default!;
        private IConfiguration m_configuration;
        private readonly ILogger<AzureVoiceLiveService> _logger;
        private bool m_isEndingCall = false;
        private string m_callerId;
        private DateTime m_goodbyeStartTime;
        private bool m_goodbyeMessageStarted = false;
        private readonly IStaffLookupService _staffLookupService;
        private readonly IEmailService _emailService;
        private readonly ICallManagementService _callManagementService;

        public AzureVoiceLiveService(
            AcsMediaStreamingHandler mediaStreaming, 
            IConfiguration configuration, 
            ILogger<AzureVoiceLiveService> logger, 
            string callerId, 
            CallAutomationClient callAutomationClient, 
            Dictionary<string, string> activeCallConnections,
            IStaffLookupService staffLookupService,
            IEmailService emailService,
            ICallManagementService callManagementService)
        {
            m_mediaStreaming = mediaStreaming;
            m_cts = new CancellationTokenSource();
            m_configuration = configuration;
            _logger = logger;
            m_callerId = callerId;
            _staffLookupService = staffLookupService;
            _emailService = emailService;
            _callManagementService = callManagementService;
            
            _logger.LogInformation($"🎯 AzureVoiceLiveService initialized with Caller ID: {m_callerId}");
            
            // Initialize the call management service
            _callManagementService.Initialize(callAutomationClient, activeCallConnections);
            
            CreateAISessionAsync(configuration).GetAwaiter().GetResult();
        }

        private async Task CreateAISessionAsync(IConfiguration configuration)
        {
            var azureVoiceLiveApiKey = configuration.GetValue<string>("AzureVoiceLiveApiKey");
            var azureVoiceLiveEndpoint = configuration.GetValue<string>("AzureVoiceLiveEndpoint");
            var voiceLiveModel = configuration.GetValue<string>("VoiceLiveModel");
            var systemPrompt = configuration.GetValue<string>("SystemPrompt") ?? m_answerPromptSystemTemplate;

            var azureVoiceLiveWebsocketUrl = new Uri($"{azureVoiceLiveEndpoint.Replace("https", "wss")}/voice-agent/realtime?api-version=2025-05-01-preview&x-ms-client-request-id={Guid.NewGuid()}&model={voiceLiveModel}&api-key={azureVoiceLiveApiKey}");

            m_azureVoiceLiveWebsocket = new ClientWebSocket();
            _logger.LogInformation($"Connecting to {azureVoiceLiveWebsocketUrl}...");
            await m_azureVoiceLiveWebsocket.ConnectAsync(azureVoiceLiveWebsocketUrl, CancellationToken.None);
            _logger.LogInformation("Connected successfully!");

            StartConversation();
            await UpdateSessionAsync();
            await StartResponseAsync();
        }

        private async Task UpdateSessionAsync()
        {
            var jsonObject = new
            {
                type = "session.update",
                session = new
                {
                    instructions = string.Join(" ",
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
                        "Never end a conversation without calling the end_call function."
                    ),
                    turn_detection = new
                    {
                        type = "azure_semantic_vad",
                        threshold = 0.5,
                        prefix_padding_ms = 200,
                        silence_duration_ms = 200,
                        remove_filler_words = false
                    },
                    input_audio_noise_reduction = new { type = "azure_deep_noise_suppression" },
                    input_audio_echo_cancellation = new { type = "server_echo_cancellation" },
                    voice = new
                    {
                        name = "en-US-Emma:DragonHDLatestNeural",
                        type = "azure-standard",
                        temperature = 0.8
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

            var sessionUpdate = JsonSerializer.Serialize(jsonObject, new JsonSerializerOptions { WriteIndented = true });
            _logger.LogInformation($"SessionUpdate: {sessionUpdate}");
            _logger.LogInformation($"[DEBUG] Sending function response to AI: {sessionUpdate}");
            await SendMessageAsync(sessionUpdate, CancellationToken.None);
        }

        private async Task StartResponseAsync()
        {
            var jsonObject = new { type = "response.create" };
            var message = JsonSerializer.Serialize(jsonObject, new JsonSerializerOptions { WriteIndented = true });
            _logger.LogInformation($"[DEBUG] Sending function response to AI: {message}");
            await SendMessageAsync(message, CancellationToken.None);
        }

        async Task SendMessageAsync(string message, CancellationToken cancellationToken)
        {
            if (m_azureVoiceLiveWebsocket?.State != WebSocketState.Open) return;

            byte[] messageBytes = Encoding.UTF8.GetBytes(message);
            await m_azureVoiceLiveWebsocket.SendAsync(
                new ArraySegment<byte>(messageBytes),
                WebSocketMessageType.Text,
                true,
                cancellationToken);
        }

        async Task ReceiveMessagesAsync(CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[1024 * 8];

            try
            {
                while (m_azureVoiceLiveWebsocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var receiveBuffer = new ArraySegment<byte>(buffer);
                    StringBuilder messageBuilder = new StringBuilder();

                    WebSocketReceiveResult result;
                    do
                    {
                        result = await m_azureVoiceLiveWebsocket.ReceiveAsync(receiveBuffer, cancellationToken);
                        messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    while (!result.EndOfMessage);

                    string receivedMessage = messageBuilder.ToString();
                    _logger.LogInformation($"Received: {receivedMessage}");

                    var jsonDoc = JsonDocument.Parse(receivedMessage);
                    var root = jsonDoc.RootElement;

                    if (root.TryGetProperty("type", out var typeElement))
                    {
                        var messageType = typeElement.GetString();

                        if (messageType == "response.audio.delta")
                        {
                            var delta = root.GetProperty("delta").GetString();
                            var jsonString = OutStreamingData.GetAudioDataForOutbound(Convert.FromBase64String(delta));
                            await m_mediaStreaming.SendMessageAsync(jsonString);
                        }
                        else if (messageType == "input_audio_buffer.speech_started")
                        {
                            _logger.LogInformation("-- Voice activity detection started");
                            var jsonString = OutStreamingData.GetStopAudioForOutbound();
                            await m_mediaStreaming.SendMessageAsync(jsonString);
                        }
                        else if (messageType == "response.function_call_arguments.done")
                        {
                            _logger.LogInformation("🟢 Detected function_call_arguments.done");
                            var functionName = root.GetProperty("name").GetString();
                            var callId = root.GetProperty("call_id").GetString();
                            var args = root.GetProperty("arguments").ToString();
                            _logger.LogInformation($"🟠 Function name: {functionName}, Call ID: {callId}");
                            _logger.LogInformation($"🟠 Raw args: {args}");

                            if (functionName == "check_staff_exists")
                            {
                                await HandleCheckStaffExists(args, callId, cancellationToken);
                            }
                            else if (functionName == "send_message")
                            {
                                await HandleSendMessage(args, callId, cancellationToken);
                            }
                            else if (functionName == "end_call")
                            {
                                await HandleEndCall(callId, cancellationToken);
                            }
                        }
                        else if (messageType == "response.output_item.added")
                        {
                            if (m_isEndingCall && !m_goodbyeMessageStarted)
                            {
                                m_goodbyeMessageStarted = true;
                                m_goodbyeStartTime = DateTime.Now;
                                _logger.LogInformation("🎤 Goodbye message started - tracking timing");
                            }
                        }
                        else if (messageType == "response.done")
                        {
                            _logger.LogInformation("🔵 AI response completed");
                            if (m_isEndingCall)
                            {
                                _logger.LogInformation("🔚 Call ending detected - AI response completed");
                                
                                var additionalDelay = CalculateGoodbyeDelay();
                                _logger.LogInformation($"⏱️ Waiting {additionalDelay}ms for goodbye message to complete");
                                
                                await Task.Delay(additionalDelay);
                                
                                await HangUpAcsCall();
                                await Close();
                                return;
                            }
                        }
                        else if (messageType == "response.audio.done")
                        {
                            _logger.LogInformation("🔊 Audio response completed");
                            if (m_isEndingCall)
                            {
                                _logger.LogInformation("🔚 Call ending detected - Audio generation completed");
                                
                                var additionalDelay = CalculateGoodbyeDelay();
                                _logger.LogInformation($"⏱️ Waiting {additionalDelay}ms for audio playback to complete");
                                
                                await Task.Delay(additionalDelay);
                                
                                await HangUpAcsCall();
                                await Close();
                                return;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Receive error: {ex.Message}");
            }
        }

        private int CalculateGoodbyeDelay()
        {
            if (m_goodbyeMessageStarted)
            {
                var elapsedSinceGoodbye = DateTime.Now - m_goodbyeStartTime;
                var estimatedGoodbyeDuration = TimeSpan.FromSeconds(5);
                var remainingTime = estimatedGoodbyeDuration - elapsedSinceGoodbye;
                
                if (remainingTime.TotalMilliseconds > 0)
                {
                    return (int)remainingTime.TotalMilliseconds + 1500;
                }
                else
                {
                    return 1000;
                }
            }
            else
            {
                return 6000;
            }
        }

        private async Task HandleCheckStaffExists(string args, string callId, CancellationToken cancellationToken)
        {
            _logger.LogInformation($"🟢 check_staff_exists called with args: {args}");
            try
            {
                var parsed = JsonDocument.Parse(args);
                var name = parsed.RootElement.GetProperty("name").GetString();
                var department = parsed.RootElement.TryGetProperty("department", out var deptElement) ? 
                    deptElement.GetString() : null;
                
                // Use the new service instead of inline logic
                var result = await _staffLookupService.CheckStaffExistsAsync(name!, department);
                
                string functionResult = result.Status switch
                {
                    StaffLookupStatus.Authorized => "authorized",
                    StaffLookupStatus.NotAuthorized => "not_authorized", 
                    StaffLookupStatus.MultipleFound => "multiple_found",
                    StaffLookupStatus.NotFound => "not_authorized",
                    _ => "not_authorized"
                };

                // Send function response back to AI
                var functionResponse = new
                {
                    type = "conversation.item.create",
                    item = new
                    {
                        type = "function_call_output",
                        call_id = callId,
                        output = functionResult
                    }
                };

                var jsonResponse = JsonSerializer.Serialize(functionResponse, new JsonSerializerOptions { WriteIndented = true });
                _logger.LogInformation($"[DEBUG] Sending function response to AI: {jsonResponse}");
                await SendMessageAsync(jsonResponse, cancellationToken);

                // Trigger AI response
                var createResponse = new { type = "response.create" };
                var jsonCreateResponse = JsonSerializer.Serialize(createResponse, new JsonSerializerOptions { WriteIndented = true });
                await SendMessageAsync(jsonCreateResponse, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"🔴 Failed to process check_staff_exists");
            }
        }

        private async Task HandleSendMessage(string args, string callId, CancellationToken cancellationToken)
        {
            _logger.LogInformation($"🟢 send_message called with args: {args}");
            try
            {
                var parsed = JsonDocument.Parse(args);
                var name = parsed.RootElement.GetProperty("name").GetString();
                var message = parsed.RootElement.GetProperty("message").GetString();
                var department = parsed.RootElement.TryGetProperty("department", out var deptElement) ? 
                    deptElement.GetString() : null;

                _logger.LogInformation($"🟡 Parsed: name={name}, message={message}, department={department}");

                // Use the new service to get email
                var email = await _staffLookupService.GetStaffEmailAsync(name!, department);
                
                string functionResult;
                if (!string.IsNullOrWhiteSpace(email))
                {
                    _logger.LogInformation($"✅ Sending email to: {name}, email: {email}");
                    await SendEmailToUserAsync(name, email, message);
                    functionResult = "success";
                }
                else
                {
                    _logger.LogWarning($"❌ No valid email found for: {name}");
                    functionResult = "failed - staff not found or invalid email";
                }

                // Send function response back to AI
                var functionResponse = new
                {
                    type = "conversation.item.create",
                    item = new
                    {
                        type = "function_call_output",
                        call_id = callId,
                        output = functionResult
                    }
                };

                var jsonResponse = JsonSerializer.Serialize(functionResponse, new JsonSerializerOptions { WriteIndented = true });
                _logger.LogInformation($"[DEBUG] Sending function response to AI: {jsonResponse}");
                await SendMessageAsync(jsonResponse, cancellationToken);

                // Trigger AI response
                var createResponse = new { type = "response.create" };
                var jsonCreateResponse = JsonSerializer.Serialize(createResponse, new JsonSerializerOptions { WriteIndented = true });
                await SendMessageAsync(jsonCreateResponse, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"🔴 Failed to process send_message");
            }
        }

        private async Task HandleEndCall(string callId, CancellationToken cancellationToken)
        {
            _logger.LogInformation("🟣 end_call function triggered. Setting call ending flag and preparing goodbye message.");
            
            m_isEndingCall = true;
            
            var functionResponse = new
            {
                type = "conversation.item.create",
                item = new
                {
                    type = "function_call_output",
                    call_id = callId,
                    output = "call_ended_successfully"
                }
            };

            var jsonResponse = JsonSerializer.Serialize(functionResponse, new JsonSerializerOptions { WriteIndented = true });
            _logger.LogInformation($"[DEBUG] Sending function response to AI: {jsonResponse}");
            await SendMessageAsync(jsonResponse, cancellationToken);

            var createResponse = new { type = "response.create" };
            var jsonCreateResponse = JsonSerializer.Serialize(createResponse, new JsonSerializerOptions { WriteIndented = true });
            _logger.LogInformation($"[DEBUG] Triggering AI goodbye response: {jsonCreateResponse}");
            await SendMessageAsync(jsonCreateResponse, cancellationToken);
            
            _logger.LogInformation("🟣 End call function completed. Goodbye message will be spoken, then call will be hung up after appropriate delay.");
        }

        private async Task HangUpAcsCall()
        {
            var success = await _callManagementService.HangUpCallAsync(m_callerId);
            if (!success)
            {
                _logger.LogWarning($"⚠️ Failed to hang up call for caller: {m_callerId}");
            }
        }

        private async Task SendEmailToUserAsync(string name, string email, string message)
        {
            var success = await _emailService.SendMessageEmailAsync(name, email, message, m_callerId);
            if (!success)
            {
                _logger.LogWarning($"❌ Failed to send email to {name} at {email}");
            }
        }

        public void StartConversation()
        {
            _ = Task.Run(async () => await ReceiveMessagesAsync(m_cts.Token));
        }

        public async Task SendAudioToExternalAI(byte[] data)
        {
            var audioBytes = Convert.ToBase64String(data);
            var jsonObject = new
            {
                type = "input_audio_buffer.append",
                audio = audioBytes
            };

            var message = JsonSerializer.Serialize(jsonObject, new JsonSerializerOptions { WriteIndented = true });
            await SendMessageAsync(message, CancellationToken.None);
        }

        public async Task Close()
        {
            try
            {
                m_cts.Cancel();
                m_cts.Dispose();
                if (m_azureVoiceLiveWebsocket != null && m_azureVoiceLiveWebsocket.State == WebSocketState.Open)
                {
                    await m_azureVoiceLiveWebsocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Normal", CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error closing WebSocket: {ex.Message}");
            }
        }
    }
}
