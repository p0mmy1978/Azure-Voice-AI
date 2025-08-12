using System.Net.WebSockets;
using Azure.Communication.CallAutomation;
using System.Text;
using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Graph.Users.Item.SendMail;
using Azure.Identity;

namespace CallAutomation.AzureAI.VoiceLive
{
    public class AzureVoiceLiveService
    {
        private CancellationTokenSource m_cts;
        private AcsMediaStreamingHandler m_mediaStreaming;
        private string m_answerPromptSystemTemplate = "You are an AI assistant that helps people find information.";
        private ClientWebSocket m_azureVoiceLiveWebsocket = default!;
        private IConfiguration m_configuration;
        private GraphServiceClient m_graphClient = default!;
        private readonly ILogger<AzureVoiceLiveService> _logger;
        private bool m_isEndingCall = false;
        private string m_callerId;
        private CallAutomationClient m_callAutomationClient; // For hanging up calls
        private Dictionary<string, string> m_activeCallConnections; // Track active call connections
        private bool m_hasHungUp = false; // Prevent multiple hangups
        private DateTime m_goodbyeStartTime; // Track when goodbye message started
        private bool m_goodbyeMessageStarted = false; // Track if goodbye message has started

        // Constructor to accept CallAutomationClient and connection tracking
        public AzureVoiceLiveService(AcsMediaStreamingHandler mediaStreaming, IConfiguration configuration, ILogger<AzureVoiceLiveService> logger, string callerId, CallAutomationClient callAutomationClient, Dictionary<string, string> activeCallConnections)
        {
            m_mediaStreaming = mediaStreaming;
            m_cts = new CancellationTokenSource();
            m_configuration = configuration;
            _logger = logger;
            m_callerId = callerId;
            m_callAutomationClient = callAutomationClient; // Store the call automation client
            m_activeCallConnections = activeCallConnections; // Store active call connections
            
            _logger.LogInformation($"🎯 AzureVoiceLiveService initialized with Caller ID: {m_callerId}");
            
            InitGraphClient();
            CreateAISessionAsync(configuration).GetAwaiter().GetResult();
        }

        private void InitGraphClient()
        {
            var tenantId = m_configuration["GraphTenantId"];
            var clientId = m_configuration["GraphClientId"];
            var clientSecret = m_configuration["GraphClientSecret"];

            var clientSecretCredential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            m_graphClient = new GraphServiceClient(clientSecretCredential, new[] { "https://graph.microsoft.com/.default" });
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
            // Send initial greeting only once after session update
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
                            // NEW: Detect when goodbye message starts
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
                                
                                // Calculate how long to wait based on goodbye message timing
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
                                
                                // Calculate how long to wait for audio playback to finish
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

        // NEW: Calculate appropriate delay for goodbye message
        private int CalculateGoodbyeDelay()
        {
            if (m_goodbyeMessageStarted)
            {
                // Calculate elapsed time since goodbye started
                var elapsedSinceGoodbye = DateTime.Now - m_goodbyeStartTime;
                
                // Typical goodbye message: "Thanks for calling poms.tech, have a great day!"
                // Estimate: ~4 seconds to say this at normal pace + buffer for audio transmission/processing
                var estimatedGoodbyeDuration = TimeSpan.FromSeconds(5);
                
                var remainingTime = estimatedGoodbyeDuration - elapsedSinceGoodbye;
                
                if (remainingTime.TotalMilliseconds > 0)
                {
                    // Add extra buffer for audio transmission and processing delays
                    return (int)remainingTime.TotalMilliseconds + 1500;
                }
                else
                {
                    // If somehow we're already past the estimated time, add a small buffer
                    return 1000;
                }
            }
            else
            {
                // Fallback if we couldn't track timing - use longer delay
                return 6000; // 6 seconds should be plenty for the goodbye message
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
                
                var normalized = (name ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", "");
                
                var tableServiceUri = new Uri(m_configuration["StorageUri"]);
                var tableClient = new TableClient(
                    tableServiceUri,
                    m_configuration["TableName"],
                    new TableSharedKeyCredential(
                        m_configuration["StorageAccountName"],
                        m_configuration["StorageAccountKey"]));

                string functionResult;
                
                // If department is provided, try exact match first
                if (!string.IsNullOrWhiteSpace(department))
                {
                    var normalizedDept = department.Trim().ToLowerInvariant();
                    var exactRowKey = $"{normalized}_{normalizedDept}";
                    
                    _logger.LogInformation($"🔍 [check_staff_exists] Looking up with department: {exactRowKey}");
                    var exactResult = await tableClient.GetEntityIfExistsAsync<TableEntity>("staff", exactRowKey);
                    
                    if (exactResult.HasValue && exactResult.Value != null)
                    {
                        var emailObj = exactResult.Value.ContainsKey("email") ? exactResult.Value["email"] : null;
                        var email = emailObj?.ToString()?.Trim();
                        bool validEmail = !string.IsNullOrWhiteSpace(email) && email.Contains("@");
                        
                        if (validEmail)
                        {
                            _logger.LogInformation($"✅ Staff authorized: {name} in {department}, email: {email}");
                            functionResult = "authorized";
                        }
                        else
                        {
                            _logger.LogWarning($"❌ Staff found but invalid email: {name} in {department}");
                            functionResult = "not_authorized";
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"❌ Staff NOT found: {name} in {department}");
                        functionResult = "not_authorized";
                    }
                }
                else
                {
                    // No department provided - search for all matches
                    _logger.LogInformation($"🔍 [check_staff_exists] Searching for all matches of: {normalized}");
                    
                    // Query all entities that start with the normalized name
                    var query = tableClient.QueryAsync<TableEntity>(
                        filter: $"PartitionKey eq 'staff' and RowKey ge '{normalized}' and RowKey lt '{normalized}~'",
                        maxPerPage: 10);
                    
                    var matches = new List<TableEntity>();
                    await foreach (var entity in query)
                    {
                        // Check if RowKey starts with our normalized name
                        if (entity.RowKey.StartsWith(normalized))
                        {
                            matches.Add(entity);
                        }
                    }
                    
                    _logger.LogInformation($"🔍 Found {matches.Count} potential matches");
                    
                    if (matches.Count == 0)
                    {
                        _logger.LogWarning($"❌ No staff found matching: {name}");
                        functionResult = "not_authorized";
                    }
                    else if (matches.Count == 1)
                    {
                        var match = matches[0];
                        var emailObj = match.ContainsKey("email") ? match["email"] : null;
                        var email = emailObj?.ToString()?.Trim();
                        bool validEmail = !string.IsNullOrWhiteSpace(email) && email.Contains("@");
                        
                        if (validEmail)
                        {
                            _logger.LogInformation($"✅ Single match found and authorized: {name}");
                            functionResult = "authorized";
                        }
                        else
                        {
                            _logger.LogWarning($"❌ Single match found but invalid email: {name}");
                            functionResult = "not_authorized";
                        }
                    }
                    else
                    {
                        // Multiple matches found - need clarification
                        var departments = matches.Where(m => m.ContainsKey("Department"))
                            .Select(m => m["Department"]?.ToString())
                            .Where(d => !string.IsNullOrWhiteSpace(d))
                            .Distinct()
                            .ToList();
                        
                        _logger.LogInformation($"🟡 Multiple matches found for {name}. Departments: {string.Join(", ", departments)}");
                        functionResult = "multiple_found";
                    }
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

                // Get staff email using the same logic as check_staff_exists
                var tableServiceUri = new Uri(m_configuration["StorageUri"]);
                var tableClient = new TableClient(
                    tableServiceUri,
                    m_configuration["TableName"],
                    new TableSharedKeyCredential(
                        m_configuration["StorageAccountName"],
                        m_configuration["StorageAccountKey"]));
                
                var normalized = (name ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", "");
                string rowKey;
                TableEntity? staffEntity = null;
                
                // Use the same logic as check_staff_exists
                if (!string.IsNullOrWhiteSpace(department))
                {
                    var normalizedDept = department.Trim().ToLowerInvariant();
                    rowKey = $"{normalized}_{normalizedDept}";
                    _logger.LogInformation($"🔍 [send_message] Looking up with department: {rowKey}");
                    
                    var exactResult = await tableClient.GetEntityIfExistsAsync<TableEntity>("staff", rowKey);
                    if (exactResult.HasValue && exactResult.Value != null)
                    {
                        staffEntity = exactResult.Value;
                    }
                }
                else
                {
                    // If no department provided, try to find a unique match
                    _logger.LogInformation($"🔍 [send_message] No department provided, searching for matches of: {normalized}");
                    
                    var query = tableClient.QueryAsync<TableEntity>(
                        filter: $"PartitionKey eq 'staff' and RowKey ge '{normalized}' and RowKey lt '{normalized}~'",
                        maxPerPage: 10);
                    
                    var matches = new List<TableEntity>();
                    await foreach (var entity in query)
                    {
                        if (entity.RowKey.StartsWith(normalized))
                        {
                            matches.Add(entity);
                        }
                    }
                    
                    if (matches.Count == 1)
                    {
                        staffEntity = matches[0];
                        rowKey = matches[0].RowKey;
                        _logger.LogInformation($"🔍 [send_message] Single match found: {rowKey}");
                    }
                    else if (matches.Count > 1)
                    {
                        _logger.LogWarning($"🟡 [send_message] Multiple matches found for {name}, cannot proceed without department");
                        var functionResponse = new
                        {
                            type = "conversation.item.create",
                            item = new
                            {
                                type = "function_call_output",
                                call_id = callId,
                                output = "failed - multiple matches found, department required"
                            }
                        };
                        var jsonResponse = JsonSerializer.Serialize(functionResponse, new JsonSerializerOptions { WriteIndented = true });
                        _logger.LogInformation($"[DEBUG] Sending function response to AI: {jsonResponse}");
                        await SendMessageAsync(jsonResponse, cancellationToken);

                        // Trigger AI response
                        var createResponse = new { type = "response.create" };
                        var jsonCreateResponse = JsonSerializer.Serialize(createResponse, new JsonSerializerOptions { WriteIndented = true });
                        await SendMessageAsync(jsonCreateResponse, cancellationToken);
                        return;
                    }
                    else
                    {
                        _logger.LogWarning($"❌ [send_message] No matches found for {name}");
                        var functionResponse = new
                        {
                            type = "conversation.item.create",
                            item = new
                            {
                                type = "function_call_output",
                                call_id = callId,
                                output = "failed - staff not found"
                            }
                        };
                        var jsonResponse = JsonSerializer.Serialize(functionResponse, new JsonSerializerOptions { WriteIndented = true });
                        _logger.LogInformation($"[DEBUG] Sending function response to AI: {jsonResponse}");
                        await SendMessageAsync(jsonResponse, cancellationToken);

                        // Trigger AI response
                        var createResponse = new { type = "response.create" };
                        var jsonCreateResponse = JsonSerializer.Serialize(createResponse, new JsonSerializerOptions { WriteIndented = true });
                        await SendMessageAsync(jsonCreateResponse, cancellationToken);
                        return;
                    }
                }
                
                string functionResult;
                if (staffEntity != null && staffEntity.ContainsKey("email"))
                {
                    var email = staffEntity["email"]?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(email) && email.Contains("@"))
                    {
                        _logger.LogInformation($"✅ Sending email to: {name}, email: {email}");
                        await SendEmailToUserAsync(name, email, message);
                        functionResult = "success";
                    }
                    else
                    {
                        _logger.LogWarning($"❌ No valid email for: {name}");
                        functionResult = "failed - no valid email";
                    }
                }
                else
                {
                    _logger.LogWarning($"❌ Staff not found for message sending: {name}");
                    functionResult = "failed - staff not found";
                }

                // Send function response back to AI
                var finalResponse = new
                {
                    type = "conversation.item.create",
                    item = new
                    {
                        type = "function_call_output",
                        call_id = callId,
                        output = functionResult
                    }
                };

                var finalJsonResponse = JsonSerializer.Serialize(finalResponse, new JsonSerializerOptions { WriteIndented = true });
                _logger.LogInformation($"[DEBUG] Sending function response to AI: {finalJsonResponse}");
                await SendMessageAsync(finalJsonResponse, cancellationToken);

                // Trigger AI response
                var createResponse2 = new { type = "response.create" };
                var jsonCreateResponse2 = JsonSerializer.Serialize(createResponse2, new JsonSerializerOptions { WriteIndented = true });
                await SendMessageAsync(jsonCreateResponse2, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"🔴 Failed to process send_message");
            }
        }

        // FIXED: HandleEndCall - improved timing management
        private async Task HandleEndCall(string callId, CancellationToken cancellationToken)
        {
            _logger.LogInformation("🟣 end_call function triggered. Setting call ending flag and preparing goodbye message.");
            
            // Set the flag to indicate we're ending the call
            m_isEndingCall = true;
            
            // Send function response back to AI
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

            // Trigger final AI response (goodbye message)
            var createResponse = new { type = "response.create" };
            var jsonCreateResponse = JsonSerializer.Serialize(createResponse, new JsonSerializerOptions { WriteIndented = true });
            _logger.LogInformation($"[DEBUG] Triggering AI goodbye response: {jsonCreateResponse}");
            await SendMessageAsync(jsonCreateResponse, cancellationToken);
            
            _logger.LogInformation("🟣 End call function completed. Goodbye message will be spoken, then call will be hung up after appropriate delay.");
        }

        // Method to hang up the actual ACS call with guard to prevent multiple hangups
        private async Task HangUpAcsCall()
        {
            if (m_hasHungUp) return; // Prevent multiple hangups
            m_hasHungUp = true;
            
            try
            {
                // Find the call connection ID for this session
                var callConnectionId = m_activeCallConnections.Values.FirstOrDefault();
                
                if (!string.IsNullOrEmpty(callConnectionId))
                {
                    _logger.LogInformation($"📞 Hanging up ACS call with CallConnectionId: {callConnectionId}");
                    
                    var callConnection = m_callAutomationClient.GetCallConnection(callConnectionId);
                    await callConnection.HangUpAsync(forEveryone: true);
                    
                    _logger.LogInformation($"✅ Successfully hung up ACS call: {callConnectionId}");
                }
                else
                {
                    _logger.LogWarning($"⚠️ No CallConnectionId found to hang up");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to hang up ACS call");
            }
        }

        // Enhanced email format with caller ID and professional formatting
        private async Task SendEmailToUserAsync(string name, string email, string message)
        {
            try
            {
                _logger.LogInformation($"📬 Begin SendEmailToUserAsync for {name} <{message}> from caller: {m_callerId}");
                
                // Format the timestamp
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                
                // Clean and format caller number to E.164 format
                var formattedCallerNumber = FormatCallerNumber(m_callerId);
                
                // Create the formatted message body using the cleaned caller ID
                var formattedMessage = $@"You have received a new after-hours message:

Time: {timestamp}
Caller Number: {formattedCallerNumber}
Message: {message}";

                var mail = new Message
                {
                    Subject = $"New after-hours message from POMS.Tech",
                    Body = new ItemBody
                    {
                        ContentType = BodyType.Text,
                        Content = formattedMessage
                    },
                    ToRecipients = new List<Recipient>
                    {
                        new Recipient { EmailAddress = new EmailAddress { Address = email } }
                    }
                };

                var requestBody = new SendMailPostRequestBody
                {
                    Message = mail,
                    SaveToSentItems = true
                };

                _logger.LogInformation($"📤 Sending mail via Graph API to {email}...");
                await m_graphClient.Users[m_configuration["GraphSenderUPN"]].SendMail.PostAsync(requestBody);
                _logger.LogInformation($"✅ Message sent to {name} at {email} with caller ID: {formattedCallerNumber}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Failed to send email");
            }
        }

        // Helper method to format caller number to E.164 format
        private string FormatCallerNumber(string rawCallerNumber)
        {
            if (string.IsNullOrWhiteSpace(rawCallerNumber))
            {
                return "Unknown";
            }

            try
            {
                _logger.LogInformation($"🔍 Raw caller number received: '{rawCallerNumber}'");
                
                // Remove any non-digit characters except + 
                var digitsOnly = new string(rawCallerNumber.Where(c => char.IsDigit(c)).ToArray());
                
                _logger.LogInformation($"🔍 Digits extracted: '{digitsOnly}'");
                
                // If we have digits, format as E.164
                if (!string.IsNullOrEmpty(digitsOnly))
                {
                    // Look for Australian number pattern (starting with 61)
                    if (digitsOnly.StartsWith("461") && digitsOnly.Length >= 12)
                    {
                        // Remove the leading "4" prefix and format the Australian number
                        var cleanNumber = digitsOnly.Substring(1); // Remove the "4"
                        _logger.LogInformation($"🔍 Removed ACS prefix '4', clean number: '{cleanNumber}'");
                        return $"+{cleanNumber}";
                    }
                    else if (digitsOnly.StartsWith("61") && digitsOnly.Length >= 11)
                    {
                        // Already a proper Australian number
                        return $"+{digitsOnly}";
                    }
                    else if (digitsOnly.Length >= 10)
                    {
                        // Generic international number
                        return $"+{digitsOnly}";
                    }
                }
                
                // Fallback: return original if we can't parse it
                _logger.LogWarning($"Could not format caller number: '{rawCallerNumber}'");
                return rawCallerNumber;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to format caller number: {rawCallerNumber}");
                return rawCallerNumber;
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
