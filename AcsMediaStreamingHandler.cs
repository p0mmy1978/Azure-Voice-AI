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
        
        m_logger.LogInformation($"🔗 WebSocket connection established with Caller ID: {m_callerId}");
    }

    public IServiceProvider GetServiceProvider() => _serviceProvider;

    public async Task ProcessWebSocketAsync()
    {
        if (m_webSocket == null)
        {
            return;
        }

        var sessionManager = _serviceProvider.GetRequiredService<ICallSessionManager>();
        
        // FIXED: Check if THIS SPECIFIC call has expired, not if we can accept NEW calls
        // The session was already started in Program.cs, so we just verify it's still valid
        if (sessionManager.IsCallExpired(m_callerId))
        {
            m_logger.LogWarning($"⏰ WebSocket connection attempted for expired session: {m_callerId}");
            await CloseNormalWebSocketAsync();
            return;
        }

        // Log session information
        var remainingTime = sessionManager.GetRemainingTime(m_callerId);
        var activeCallCount = sessionManager.GetActiveCallCount();
        m_logger.LogInformation($"⏰ WebSocket connected with {remainingTime.TotalSeconds:F0}s remaining for: {m_callerId}");
        m_logger.LogInformation($"📊 Active calls: {activeCallCount}/2");
        m_logger.LogWarning($"🚨 BILL SHOCK PREVENTION: Call will be force terminated if it exceeds 90s total duration");

        // Get all services upfront
        var staffLookupService = _serviceProvider.GetRequiredService<IStaffLookupService>();
        var emailService = _serviceProvider.GetRequiredService<IEmailService>();
        var callManagementService = _serviceProvider.GetRequiredService<ICallManagementService>();
        var functionCallProcessor = _serviceProvider.GetRequiredService<IFunctionCallProcessor>();
        var audioStreamProcessor = _serviceProvider.GetRequiredService<IAudioStreamProcessor>();
        var voiceSessionManager = _serviceProvider.GetRequiredService<IVoiceSessionManager>();
        var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();
        
        m_logger.LogInformation("🔧 All services resolved from DI container");
        
        // Start email initialization asynchronously
        var emailInitTask = emailService.InitializeAsync();
        m_logger.LogInformation("📧 Email service initialization started asynchronously");

        // Initialize call management
        callManagementService.Initialize(m_callAutomationClient, m_activeCallConnections);
        m_logger.LogInformation("📞 Call management service initialized");

        // Create AI service handler
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
            var webSocketTask = StartReceivingFromAcsMediaWebSocket();
            m_logger.LogInformation("🎧 WebSocket audio processing started");
            
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
            
            try
            {
                var callSessionManager = _serviceProvider.GetRequiredService<ICallSessionManager>();
                callSessionManager.EndCallSession(m_callerId);
                m_logger.LogInformation($"✅ Call session ended for: {m_callerId}");
            }
            catch (Exception ex)
            {
                m_logger.LogError(ex, $"❌ Error ending call session for: {m_callerId}");
            }
            
            if (m_aiServiceHandler != null)
            {
                await m_aiServiceHandler.Close();
            }
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
                var callSessionManager = _serviceProvider.GetRequiredService<ICallSessionManager>();
                if (callSessionManager.IsCallExpired(m_callerId))
                {
                    m_logger.LogWarning($"⏰ Refusing to send message - session expired for: {m_callerId}");
                    return;
                }

                byte[] jsonBytes = Encoding.UTF8.GetBytes(message);
                await m_webSocket.SendAsync(new ArraySegment<byte>(jsonBytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
                
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
                await m_webSocket.CloseAsync(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure, 
                    result.CloseStatusDescription, CancellationToken.None);
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
            var callSessionManager = _serviceProvider.GetRequiredService<ICallSessionManager>();
            if (callSessionManager.IsCallExpired(m_callerId))
            {
                m_logger.LogWarning($"⏰ Dropping audio data - session expired for: {m_callerId}");
                return;
            }

            var input = StreamingData.Parse(data);
            if (input is AudioData audioData)
            {
                if (!audioData.IsSilent)
                {
                    if (m_aiServiceHandler != null)
                    {
                        await m_aiServiceHandler.SendAudioToExternalAI(audioData.Data.ToArray());
                    }
                    
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
            
            const int bufferSize = 4096;
            byte[] receiveBuffer = new byte[bufferSize];
            
            var callSessionManager = _serviceProvider.GetRequiredService<ICallSessionManager>();
            
            while (m_webSocket.State == WebSocketState.Open && !m_cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (callSessionManager.IsCallExpired(m_callerId))
                    {
                        m_logger.LogWarning($"⏰ Session expired - stopping WebSocket processing for: {m_callerId}");
                        break;
                    }

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
                    
                    if (m_webSocket.State != WebSocketState.Open)
                    {
                        break;
                    }
                    
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
