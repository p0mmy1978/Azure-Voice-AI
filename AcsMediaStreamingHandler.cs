using System.Net.WebSockets;
using System.Collections.Concurrent;
using Azure.Communication.CallAutomation;
using System.Text;
using CallAutomation.AzureAI.VoiceLive;
using CallAutomation.AzureAI.VoiceLive.Services.Interfaces;

public class AcsMediaStreamingHandler
{
    private WebSocket _webSocket;
    private CancellationTokenSource _cts;
    private MemoryStream _buffer;
    private AzureVoiceLiveService _aiServiceHandler;
    private IConfiguration _configuration;
    private ILogger<AzureVoiceLiveService> _logger;
    private string _callerId;
    private CallAutomationClient _callAutomationClient;
    private ConcurrentDictionary<string, string> _activeCallConnections;
    private readonly IServiceProvider _serviceProvider;

    public AcsMediaStreamingHandler(
        WebSocket webSocket,
        IConfiguration configuration,
        ILogger<AzureVoiceLiveService> logger,
        string callerId,
        CallAutomationClient callAutomationClient,
        ConcurrentDictionary<string, string> activeCallConnections,
        IServiceProvider serviceProvider)
    {
        _webSocket = webSocket;
        _configuration = configuration;
        _buffer = new MemoryStream();
        _cts = new CancellationTokenSource();
        _logger = logger;
        _callerId = callerId;
        _callAutomationClient = callAutomationClient;
        _activeCallConnections = activeCallConnections;
        _serviceProvider = serviceProvider;
        
        _logger.LogInformation($"🔗 WebSocket connection established with Caller ID: {_callerId}");
    }

    public IServiceProvider GetServiceProvider() => _serviceProvider;

    public async Task ProcessWebSocketAsync()
    {
        if (_webSocket == null)
        {
            return;
        }

        var sessionManager = _serviceProvider.GetRequiredService<ICallSessionManager>();
        
        // FIXED: Check if THIS SPECIFIC call has expired, not if we can accept NEW calls
        // The session was already started in Program.cs, so we just verify it's still valid
        if (sessionManager.IsCallExpired(_callerId))
        {
            _logger.LogWarning($"⏰ WebSocket connection attempted for expired session: {_callerId}");
            await CloseNormalWebSocketAsync();
            return;
        }

        // Log session information
        var remainingTime = sessionManager.GetRemainingTime(_callerId);
        var activeCallCount = sessionManager.GetActiveCallCount();
        _logger.LogInformation($"⏰ WebSocket connected with {remainingTime.TotalSeconds:F0}s remaining for: {_callerId}");
        _logger.LogInformation($"📊 Active calls: {activeCallCount}/2");
        _logger.LogWarning($"🚨 BILL SHOCK PREVENTION: Call will be force terminated if it exceeds 90s total duration");

        // Get all services upfront
        var staffLookupService = _serviceProvider.GetRequiredService<IStaffLookupService>();
        var emailService = _serviceProvider.GetRequiredService<IEmailService>();
        var callManagementService = _serviceProvider.GetRequiredService<ICallManagementService>();
        var functionCallProcessor = _serviceProvider.GetRequiredService<IFunctionCallProcessor>();
        var audioStreamProcessor = _serviceProvider.GetRequiredService<IAudioStreamProcessor>();
        var voiceSessionManager = _serviceProvider.GetRequiredService<IVoiceSessionManager>();
        var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();
        
        _logger.LogInformation("🔧 All services resolved from DI container");
        
        // Start email initialization asynchronously
        var emailInitTask = emailService.InitializeAsync();
        _logger.LogInformation("📧 Email service initialization started asynchronously");

        // Initialize call management
        callManagementService.Initialize(_callAutomationClient, _activeCallConnections);
        _logger.LogInformation("📞 Call management service initialized");

        // Create AI service handler
        _logger.LogInformation("🤖 Creating AzureVoiceLiveService...");
        _aiServiceHandler = new AzureVoiceLiveService(
            this, 
            _configuration, 
            _logger, 
            _callerId, 
            _callAutomationClient, 
            _activeCallConnections,
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
            _logger.LogInformation("🎧 WebSocket audio processing started");
            
            _ = Task.Run(async () =>
            {
                try
                {
                    await emailInitTask;
                    _logger.LogInformation("✅ Email service initialized successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Failed to initialize email service");
                }
            });
            
            await webSocketTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Exception in ProcessWebSocketAsync");
            Console.WriteLine($"Exception -> {ex}");
        }
        finally
        {
            _logger.LogInformation("🛑 Cleaning up AcsMediaStreamingHandler...");
            
            try
            {
                var callSessionManager = _serviceProvider.GetRequiredService<ICallSessionManager>();
                callSessionManager.EndCallSession(_callerId);
                _logger.LogInformation($"✅ Call session ended for: {_callerId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Error ending call session for: {_callerId}");
            }
            
            if (_aiServiceHandler != null)
            {
                await _aiServiceHandler.Close();
            }
            this.Close();
            _logger.LogInformation("✅ AcsMediaStreamingHandler cleanup completed");
        }
    }

    public async Task SendMessageAsync(string message)
    {
        if (_webSocket?.State == WebSocketState.Open)
        {
            try
            {
                var callSessionManager = _serviceProvider.GetRequiredService<ICallSessionManager>();
                if (callSessionManager.IsCallExpired(_callerId))
                {
                    _logger.LogWarning($"⏰ Refusing to send message - session expired for: {_callerId}");
                    return;
                }

                byte[] jsonBytes = Encoding.UTF8.GetBytes(message);
                await _webSocket.SendAsync(new ArraySegment<byte>(jsonBytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
                
                if (message.Contains("AudioData"))
                {
                    _logger.LogDebug($"📊 Audio data sent: {jsonBytes.Length} bytes");
                }
                else
                {
                    _logger.LogDebug($"📤 Message sent to ACS: {message.Length} chars");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to send message to ACS WebSocket");
            }
        }
        else
        {
            _logger.LogWarning($"⚠️ Cannot send message - WebSocket state: {_webSocket?.State}");
        }
    }

    public async Task CloseWebSocketAsync(WebSocketReceiveResult result)
    {
        try
        {
            if (_webSocket?.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure, 
                    result.CloseStatusDescription, CancellationToken.None);
                _logger.LogInformation("🛑 WebSocket closed with received close status");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error closing WebSocket with received status");
        }
    }

    public async Task CloseNormalWebSocketAsync()
    {
        try
        {
            if (_webSocket?.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stream completed", CancellationToken.None);
                _logger.LogInformation("🛑 WebSocket closed normally");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error closing WebSocket normally");
        }
    }

    public void Close()
    {
        try
        {
            _logger.LogInformation("🛑 Closing AcsMediaStreamingHandler resources...");
            
            _cts.Cancel();
            _cts.Dispose();
            _buffer.Dispose();
            
            _logger.LogInformation("✅ AcsMediaStreamingHandler resources closed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error closing AcsMediaStreamingHandler resources");
        }
    }

    private async Task WriteToAzureFoundryAIServiceInputStream(string data)
    {
        try
        {
            var callSessionManager = _serviceProvider.GetRequiredService<ICallSessionManager>();
            if (callSessionManager.IsCallExpired(_callerId))
            {
                _logger.LogWarning($"⏰ Dropping audio data - session expired for: {_callerId}");
                return;
            }

            var input = StreamingData.Parse(data);
            if (input is AudioData audioData)
            {
                if (!audioData.IsSilent)
                {
                    if (_aiServiceHandler != null)
                    {
                        await _aiServiceHandler.SendAudioToExternalAI(audioData.Data.ToArray());
                    }
                    
                    if (audioData.Data.Length > 0 && audioData.Data.Length % 100 == 0)
                    {
                        _logger.LogDebug($"🎤 Audio data sent to AI: {audioData.Data.Length} bytes");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error processing audio data for AI service");
        }
    }

    private async Task StartReceivingFromAcsMediaWebSocket()
    {
        if (_webSocket == null)
        {
            _logger.LogWarning("⚠️ WebSocket is null, cannot start receiving");
            return;
        }

        try
        {
            _logger.LogInformation("🎧 Starting to receive audio data from ACS WebSocket...");
            
            const int bufferSize = 4096;
            byte[] receiveBuffer = new byte[bufferSize];
            
            var callSessionManager = _serviceProvider.GetRequiredService<ICallSessionManager>();
            
            while (_webSocket.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (callSessionManager.IsCallExpired(_callerId))
                    {
                        _logger.LogWarning($"⏰ Session expired - stopping WebSocket processing for: {_callerId}");
                        break;
                    }

                    WebSocketReceiveResult receiveResult = await _webSocket.ReceiveAsync(
                        new ArraySegment<byte>(receiveBuffer), 
                        _cts.Token);

                    if (receiveResult.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("🛑 WebSocket close message received");
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
                                _logger.LogError(ex, "❌ Error processing audio data asynchronously");
                            }
                        });
                    }
                    else if (receiveResult.MessageType == WebSocketMessageType.Binary)
                    {
                        _logger.LogDebug("📦 Binary data received (not processed)");
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("🛑 WebSocket receiving cancelled");
                    break;
                }
                catch (WebSocketException wsEx) when (wsEx.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
                {
                    _logger.LogWarning("⚠️ WebSocket connection closed prematurely");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Error receiving data from WebSocket");
                    
                    if (_webSocket.State != WebSocketState.Open)
                    {
                        break;
                    }
                    
                    await Task.Delay(100, _cts.Token);
                }
            }
            
            _logger.LogInformation($"🛑 Stopped receiving from ACS WebSocket. Final state: {_webSocket.State}");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("🛑 StartReceivingFromAcsMediaWebSocket cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Fatal error in StartReceivingFromAcsMediaWebSocket");
        }
    }
}
