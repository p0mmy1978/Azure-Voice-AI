using System.Net.WebSockets;
using Azure.Communication.CallAutomation;
using System.Text;
using CallAutomation.AzureAI.VoiceLive;
using CallAutomation.AzureAI.VoiceLive.Services.Interfaces;

public class AcsMediaStreamingHandler
{
    private WebSocket m_webSocket;
    private CancellationTokenSource m_cts;
    private MemoryStream m_buffer;
    private AzureVoiceLiveService m_aiServiceHandler;
    private IConfiguration m_configuration;
    private ILogger<AzureVoiceLiveService> m_logger;
    private string m_callerId;
    private CallAutomationClient m_callAutomationClient;
    private Dictionary<string, string> m_activeCallConnections;
    private readonly IServiceProvider _serviceProvider;

    public AcsMediaStreamingHandler(
        WebSocket webSocket, 
        IConfiguration configuration, 
        ILogger<AzureVoiceLiveService> logger, 
        string callerId, 
        CallAutomationClient callAutomationClient, 
        Dictionary<string, string> activeCallConnections,
        IServiceProvider serviceProvider)
    {
        m_webSocket = webSocket;
        m_configuration = configuration;
        m_buffer = new MemoryStream();
        m_cts = new CancellationTokenSource();
        m_logger = logger;
        m_callerId = callerId;
        m_callAutomationClient = callAutomationClient;
        m_activeCallConnections = activeCallConnections;
        _serviceProvider = serviceProvider;
        
        m_logger.LogInformation($"🔗 AcsMediaStreamingHandler initialized with Caller ID: {m_callerId}");
    }

    public async Task ProcessWebSocketAsync()
    {
        if (m_webSocket == null)
        {
            return;
        }

        // OPTIMIZED: Get all services upfront to avoid delays during processing
        var staffLookupService = _serviceProvider.GetRequiredService<IStaffLookupService>();
        var emailService = _serviceProvider.GetRequiredService<IEmailService>();
        var callManagementService = _serviceProvider.GetRequiredService<ICallManagementService>();
        var functionCallProcessor = _serviceProvider.GetRequiredService<IFunctionCallProcessor>();
        var audioStreamProcessor = _serviceProvider.GetRequiredService<IAudioStreamProcessor>();
        var voiceSessionManager = _serviceProvider.GetRequiredService<IVoiceSessionManager>();
        var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();
        
        m_logger.LogInformation("🔧 All services resolved from DI container");
        
        // OPTIMIZED: Start email initialization asynchronously (don't wait for it)
        var emailInitTask = emailService.InitializeAsync();
        m_logger.LogInformation("📧 Email service initialization started asynchronously");

        // OPTIMIZED: Initialize call management immediately
        callManagementService.Initialize(m_callAutomationClient, m_activeCallConnections);
        m_logger.LogInformation("📞 Call management service initialized");

        // OPTIMIZED: Create AI service handler immediately (don't wait for email init)
        m_logger.LogInformation("🤖 Creating AzureVoiceLiveService...");
        m_aiServiceHandler = new AzureVoiceLiveService(
            this, 
            m_configuration, 
            m_logger, 
            m_callerId, 
            m_callAutomationClient, 
            m_activeCallConnections,
            staffLookupService,
            emailService,
            callManagementService,
            functionCallProcessor,
            audioStreamProcessor,
            voiceSessionManager,
            loggerFactory);

        try
        {
            // OPTIMIZED: Start WebSocket processing and email init in parallel
            var webSocketTask = StartReceivingFromAcsMediaWebSocket();
            m_logger.LogInformation("🎧 WebSocket audio processing started");
            
            // Ensure email service is ready (but don't block the main flow)
            _ = Task.Run(async () =>
            {
                try
                {
                    await emailInitTask;
                    m_logger.LogInformation("✅ Email service initialized successfully");
                }
                catch (Exception ex)
                {
                    m_logger.LogError(ex, "❌ Failed to initialize email service");
                }
            });
            
            // Wait for the WebSocket processing to complete
            await webSocketTask;
        }
        catch (Exception ex)
        {
            m_logger.LogError(ex, "❌ Exception in ProcessWebSocketAsync");
            Console.WriteLine($"Exception -> {ex}");
        }
        finally
        {
            m_logger.LogInformation("🛑 Cleaning up AcsMediaStreamingHandler...");
            await m_aiServiceHandler.Close();
            this.Close();
            m_logger.LogInformation("✅ AcsMediaStreamingHandler cleanup completed");
        }
    }

    public async Task SendMessageAsync(string message)
    {
        if (m_webSocket?.State == WebSocketState.Open)
        {
            try
            {
                byte[] jsonBytes = Encoding.UTF8.GetBytes(message);

                // Send the PCM audio chunk over WebSocket
                await m_webSocket.SendAsync(new ArraySegment<byte>(jsonBytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
                
                // OPTIMIZED: Only log debug messages for audio data to reduce log spam
                if (message.Contains("AudioData"))
                {
                    m_logger.LogDebug($"📊 Audio data sent: {jsonBytes.Length} bytes");
                }
                else
                {
                    m_logger.LogDebug($"📤 Message sent to ACS: {message.Length} chars");
                }
            }
            catch (Exception ex)
            {
                m_logger.LogError(ex, "❌ Failed to send message to ACS WebSocket");
            }
        }
        else
        {
            m_logger.LogWarning($"⚠️ Cannot send message - WebSocket state: {m_webSocket?.State}");
        }
    }

    public async Task CloseWebSocketAsync(WebSocketReceiveResult result)
    {
        try
        {
            if (m_webSocket?.State == WebSocketState.Open)
            {
                await m_webSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
                m_logger.LogInformation("🛑 WebSocket closed with received close status");
            }
        }
        catch (Exception ex)
        {
            m_logger.LogError(ex, "❌ Error closing WebSocket with received status");
        }
    }

    public async Task CloseNormalWebSocketAsync()
    {
        try
        {
            if (m_webSocket?.State == WebSocketState.Open)
            {
                await m_webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stream completed", CancellationToken.None);
                m_logger.LogInformation("🛑 WebSocket closed normally");
            }
        }
        catch (Exception ex)
        {
            m_logger.LogError(ex, "❌ Error closing WebSocket normally");
        }
    }

    public void Close()
    {
        try
        {
            m_logger.LogInformation("🛑 Closing AcsMediaStreamingHandler resources...");
            
            m_cts.Cancel();
            m_cts.Dispose();
            m_buffer.Dispose();
            
            m_logger.LogInformation("✅ AcsMediaStreamingHandler resources closed");
        }
        catch (Exception ex)
        {
            m_logger.LogError(ex, "❌ Error closing AcsMediaStreamingHandler resources");
        }
    }

    private async Task WriteToAzureFoundryAIServiceInputStream(string data)
    {
        try
        {
            var input = StreamingData.Parse(data);
            if (input is AudioData audioData)
            {
                if (!audioData.IsSilent)
                {
                    // OPTIMIZED: Process audio data without blocking
                    await m_aiServiceHandler.SendAudioToExternalAI(audioData.Data.ToArray());
                    
                    // Only log every 100th audio packet to reduce log spam
                    if (audioData.Data.Length > 0 && audioData.Data.Length % 100 == 0)
                    {
                        m_logger.LogDebug($"🎤 Audio data sent to AI: {audioData.Data.Length} bytes");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            m_logger.LogError(ex, "❌ Error processing audio data for AI service");
        }
    }

    // OPTIMIZED: Receive messages from WebSocket with better error handling and performance
    private async Task StartReceivingFromAcsMediaWebSocket()
    {
        if (m_webSocket == null)
        {
            m_logger.LogWarning("⚠️ WebSocket is null, cannot start receiving");
            return;
        }

        try
        {
            m_logger.LogInformation("🎧 Starting to receive audio data from ACS WebSocket...");
            
            // OPTIMIZED: Use larger buffer for better performance
            const int bufferSize = 4096; // Increased from 2048
            byte[] receiveBuffer = new byte[bufferSize];
            
            while (m_webSocket.State == WebSocketState.Open && !m_cts.Token.IsCancellationRequested)
            {
                try
                {
                    WebSocketReceiveResult receiveResult = await m_webSocket.ReceiveAsync(
                        new ArraySegment<byte>(receiveBuffer), 
                        m_cts.Token);

                    if (receiveResult.MessageType == WebSocketMessageType.Close)
                    {
                        m_logger.LogInformation("🛑 WebSocket close message received");
                        break;
                    }
                    
                    if (receiveResult.MessageType == WebSocketMessageType.Text)
                    {
                        string data = Encoding.UTF8.GetString(receiveBuffer, 0, receiveResult.Count);
                        
                        // OPTIMIZED: Process audio data asynchronously without awaiting
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await WriteToAzureFoundryAIServiceInputStream(data);
                            }
                            catch (Exception ex)
                            {
                                m_logger.LogError(ex, "❌ Error processing audio data asynchronously");
                            }
                        });
                    }
                    else if (receiveResult.MessageType == WebSocketMessageType.Binary)
                    {
                        m_logger.LogDebug("📦 Binary data received (not processed)");
                    }
                }
                catch (OperationCanceledException)
                {
                    m_logger.LogInformation("🛑 WebSocket receiving cancelled");
                    break;
                }
                catch (WebSocketException wsEx) when (wsEx.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
                {
                    m_logger.LogWarning("⚠️ WebSocket connection closed prematurely");
                    break;
                }
                catch (Exception ex)
                {
                    m_logger.LogError(ex, "❌ Error receiving data from WebSocket");
                    
                    // If we get repeated errors, break the loop to avoid infinite error spam
                    if (m_webSocket.State != WebSocketState.Open)
                    {
                        break;
                    }
                    
                    // Small delay before retrying to avoid tight error loops
                    await Task.Delay(100, m_cts.Token);
                }
            }
            
            m_logger.LogInformation($"🛑 Stopped receiving from ACS WebSocket. Final state: {m_webSocket.State}");
        }
        catch (OperationCanceledException)
        {
            m_logger.LogInformation("🛑 StartReceivingFromAcsMediaWebSocket cancelled");
        }
        catch (Exception ex)
        {
            m_logger.LogError(ex, "❌ Fatal error in StartReceivingFromAcsMediaWebSocket");
        }
    }
}
