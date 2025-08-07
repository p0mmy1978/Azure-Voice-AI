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

        public AzureVoiceLiveService(AcsMediaStreamingHandler mediaStreaming, IConfiguration configuration)
        {
            m_mediaStreaming = mediaStreaming;
            m_cts = new CancellationTokenSource();
            m_configuration = configuration;
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
            Console.WriteLine($"Connecting to {azureVoiceLiveWebsocketUrl}...");
            await m_azureVoiceLiveWebsocket.ConnectAsync(azureVoiceLiveWebsocketUrl, CancellationToken.None);
            Console.WriteLine("Connected successfully!");

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
                        "Listen for the person's name, then the message.",
                        "Use the send_message function to send the message to the correct person."),
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
                    tools = new[]
                    {
                        new {
                            type = "function",
                            name = "send_message",
                            description = "Send a message to a staff member.",
                            parameters = new {
                                type = "object",
                                properties = new {
                                    name = new { type = "string", description = "The name of the person to send the message to" },
                                    message = new { type = "string", description = "The message to send" }
                                },
                                required = new[] { "name", "message" }
                            }
                        }
                    }
                }
            };

            var sessionUpdate = JsonSerializer.Serialize(jsonObject, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine($"SessionUpdate: {sessionUpdate}");
            await SendMessageAsync(sessionUpdate, CancellationToken.None);
        }

        private async Task StartResponseAsync()
        {
            var jsonObject = new { type = "response.create" };
            var message = JsonSerializer.Serialize(jsonObject, new JsonSerializerOptions { WriteIndented = true });
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
                    Console.WriteLine($"Received: {receivedMessage}");

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
                            Console.WriteLine("-- Voice activity detection started");
                            var jsonString = OutStreamingData.GetStopAudioForOutbound();
                            await m_mediaStreaming.SendMessageAsync(jsonString);
                        }
                        else if (messageType == "response.function_call" || messageType == "response.function_call_arguments.done")
                        {
                            Console.WriteLine("🔷 Detected function_call type response");
                            var functionName = root.GetProperty("name").GetString();
                            var args = root.GetProperty("arguments").ToString();
                            Console.WriteLine($"🔶 Function name: {functionName}");
                            Console.WriteLine($"🔶 Raw args: {args}");

                            if (functionName == "send_message")
                            {
                                Console.WriteLine($"🟢 Raw function args: {args}");

                                try
                                {
                                    var parsed = JsonDocument.Parse(args);
                                    var name = parsed.RootElement.GetProperty("name").GetString();
                                    var message = parsed.RootElement.GetProperty("message").GetString();

                                    Console.WriteLine($"🟡 Parsed: name={name}, message={message}");
                                    await SendEmailToUserAsync(name, message);
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"🔴 Failed to parse function args: {ex}");
                                }
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

        private async Task SendEmailToUserAsync(string name, string message)
        {
            try
            {
                Console.WriteLine("📬 Begin SendEmailToUserAsync...");

                var tableServiceUri = new Uri(m_configuration["StorageUri"]);
                Console.WriteLine($"🔎 Storage URI: {tableServiceUri}");

                var tableClient = new TableClient(
                    tableServiceUri,
                    m_configuration["TableName"],
                    new TableSharedKeyCredential(
                        m_configuration["StorageAccountName"],
                        m_configuration["StorageAccountKey"])
                );

                var rowKey = name.ToLower().Replace(" ", "");
                Console.WriteLine($"🔍 Looking up Azure Table Storage with RowKey: {rowKey}");

                var entity = await tableClient.GetEntityAsync<TableEntity>("staff", rowKey);
                var email = entity.Value["email"].ToString();
                Console.WriteLine($"📧 Email address resolved: {email}");

                var mail = new Message
                {
                    Subject = $"After-hours message for {name}",
                    Body = new ItemBody
                    {
                        ContentType = BodyType.Text,
                        Content = message
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

                Console.WriteLine("📤 Sending mail via Graph API...");
                await m_graphClient.Users[m_configuration["GraphSenderUPN"]].SendMail.PostAsync(requestBody);
                Console.WriteLine($"✅ Message sent to {name} at {email}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to send email: {ex}");
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
            m_cts.Cancel();
            m_cts.Dispose();
            if (m_azureVoiceLiveWebsocket != null)
            {
                await m_azureVoiceLiveWebsocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Normal", CancellationToken.None);
            }
        }
    }
}
