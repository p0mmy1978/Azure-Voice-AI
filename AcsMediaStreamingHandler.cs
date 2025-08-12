using System.Net.WebSockets;
using Azure.Communication.CallAutomation;
using System.Text;
using CallAutomation.AzureAI.VoiceLive;

public class AcsMediaStreamingHandler
{
    private WebSocket m_webSocket;
    private CancellationTokenSource m_cts;
    private MemoryStream m_buffer;
    private AzureVoiceLiveService m_aiServiceHandler;
    private IConfiguration m_configuration;
    private ILogger<AzureVoiceLiveService> m_logger;

    // Constructor to inject AzureAIFoundryClient
    public AcsMediaStreamingHandler(WebSocket webSocket, IConfiguration configuration, ILogger<AzureVoiceLiveService> logger)
    {
        m_webSocket = webSocket;
        m_configuration = configuration;
        m_buffer = new MemoryStream();
        m_cts = new CancellationTokenSource();
        m_logger = logger;
    }

    // Method to receive messages from WebSocket
    public async Task ProcessWebSocketAsync()
    {
        if (m_webSocket == null)
        {
            return;
        }

        // start forwarder to AI model
        m_aiServiceHandler = new AzureVoiceLiveService(this, m_configuration, m_logger);

        try
        {
            //m_aiServiceHandler.StartConversation();
            await StartReceivingFromAcsMediaWebSocket();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception -> {ex}");
        }
        finally
        {
            await m_aiServiceHandler.Close();
            this.Close();
        }
    }

    public async Task SendMessageAsync(string message)
    {
        if (m_webSocket?.State == WebSocketState.Open)
        {
            byte[] jsonBytes = Encoding.UTF8.GetBytes(message);

            // Send the PCM audio chunk over WebSocket
            await m_webSocket.SendAsync(new ArraySegment<byte>(jsonBytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
    }

    public async Task CloseWebSocketAsync(WebSocketReceiveResult result)
    {
        await m_webSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
    }

    public async Task CloseNormalWebSocketAsync()
    {
        await m_webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stream completed", CancellationToken.None);
    }

    public void Close()
    {
        m_cts.Cancel();
        m_cts.Dispose();
        m_buffer.Dispose();
    }

    private async Task WriteToAzureFoundryAIServiceInputStream(string data)
    {
        var input = StreamingData.Parse(data);
        if (input is AudioData audioData)
        {
            if (!audioData.IsSilent)
            {
              await m_aiServiceHandler.SendAudioToExternalAI(audioData.Data.ToArray());
            }
        }
    }

    // receive messages from WebSocket
    private async Task StartReceivingFromAcsMediaWebSocket()
    {
        if (m_webSocket == null)
        {
            return;
        }
        try
        {
            while (m_webSocket.State == WebSocketState.Open || m_webSocket.State == WebSocketState.Closed)
            {
                byte[] receiveBuffer = new byte[2048];
                WebSocketReceiveResult receiveResult = await m_webSocket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), m_cts.Token);

                if (receiveResult.MessageType != WebSocketMessageType.Close)
                {
                    string data = Encoding.UTF8.GetString(receiveBuffer).TrimEnd('\0');
                    await WriteToAzureFoundryAIServiceInputStream(data);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception -> {ex}");
        }
    }
}
