using Serilog;
using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;
using CallAutomation.AzureAI.VoiceLive;
using CallAutomation.AzureAI.VoiceLive.Services.Interfaces;
using CallAutomation.AzureAI.VoiceLive.Services;
using CallAutomation.AzureAI.VoiceLive.Services.Staff.Matching.StringSimilarity;
using CallAutomation.AzureAI.VoiceLive.Services.Voice;

// Configure Serilog with file rotation and size limits
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/app-log-.txt",
        rollingInterval: RollingInterval.Day,           // Still roll daily as a backup
        fileSizeLimitBytes: 10 * 1024 * 1024,          // 10MB per file
        rollOnFileSizeLimit: true,                      // Create new file when size limit reached
        retainedFileCountLimit: 10,                     // Keep max 10 files (10 x 10MB = 100MB total)
        shared: true)                                   // Allow multiple processes to write
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

//Get ACS Connection String from appsettings.json
var acsConnectionString = builder.Configuration.GetValue<string>("AcsConnectionString");
ArgumentNullException.ThrowIfNullOrEmpty(acsConnectionString);

//Call Automation Client
var client = new CallAutomationClient(acsConnectionString);

// Register existing services for dependency injection
builder.Services.AddScoped<IStaffLookupService, StaffLookupService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<ICallManagementService, CallManagementService>();
builder.Services.AddScoped<IFunctionCallProcessor, FunctionCallProcessor>();
builder.Services.AddScoped<IAudioStreamProcessor, AudioStreamProcessor>();
builder.Services.AddScoped<IVoiceSessionManager, VoiceSessionManager>();

// Register string similarity services
builder.Services.AddScoped<CompositeSimilarityMatcher>();

// Register voice configuration services
builder.Services.AddScoped<SessionConfigBuilder>();

// Register staff lookup support services
builder.Services.AddScoped<CallAutomation.AzureAI.VoiceLive.Services.Staff.StaffCacheService>();
builder.Services.AddScoped<CallAutomation.AzureAI.VoiceLive.Services.Staff.TableQueryService>();
builder.Services.AddScoped<CallAutomation.AzureAI.VoiceLive.Services.Staff.FuzzyMatchingService>();

// DO NOT register MessageProcessor and CallFlowManager - they are manually created with specific callerId

var app = builder.Build();
var appBaseUrl = builder.Configuration["AppBaseUrl"]?.TrimEnd('/');

// Dictionary to track active call connections for hangup
var activeCallConnections = new Dictionary<string, string>(); // contextId -> callConnectionId

if (string.IsNullOrEmpty(appBaseUrl))
{
    appBaseUrl = $"https://{Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME")}";
    Log.Information($"App base URL: {appBaseUrl}");
}

app.MapGet("/", () => "Hello ACS CallAutomation!");

app.MapPost("/api/incomingCall", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        Console.WriteLine($"Incoming Call event received.");

        // Handle system events
        if (eventGridEvent.TryGetSystemEventData(out object eventData))
        {
            // Handle the subscription validation event.
            if (eventData is SubscriptionValidationEventData subscriptionValidationEventData)
            {
                var responseData = new SubscriptionValidationResponse
                {
                    ValidationResponse = subscriptionValidationEventData.ValidationCode
                };
                return Results.Ok(responseData);
            }
        }

        var jsonObject = Helper.GetJsonObject(eventGridEvent.Data);
        var callerId = Helper.GetCallerId(jsonObject);
        var incomingCallContext = Helper.GetIncomingCallContext(jsonObject);
        logger.LogInformation($"appBaseUrl: {appBaseUrl}");
        logger.LogInformation($"Caller ID: {callerId}");
        var callbackUri = new Uri(new Uri(appBaseUrl), $"/api/callbacks/{Guid.NewGuid()}?callerId={callerId}");
        logger.LogInformation($"Callback Url: {callbackUri}");
        
        // Include callerId in WebSocket URL
        var websocketUri = appBaseUrl.Replace("https", "wss") + $"/ws?callerId={callerId}";
        logger.LogInformation($"WebSocket Url: {websocketUri}");

        var mediaStreamingOptions = new MediaStreamingOptions(MediaStreamingAudioChannel.Mixed)
        {
            TransportUri = new Uri(websocketUri),
            MediaStreamingContent = MediaStreamingContent.Audio,
            StartMediaStreaming = true,
            EnableBidirectional = true,
            AudioFormat = AudioFormat.Pcm24KMono
        };

        var options = new AnswerCallOptions(incomingCallContext, callbackUri)
        {
            MediaStreamingOptions = mediaStreamingOptions,
        };

        AnswerCallResult answerCallResult = await client.AnswerCallAsync(options);
        logger.LogInformation($"Answered call for connection id: {answerCallResult.CallConnection.CallConnectionId}");
    }
    return Results.Ok();
});

// api to handle call back events - captures CallConnectionId
app.MapPost("/api/callbacks/{contextId}", async (
    [FromBody] CloudEvent[] cloudEvents,
    [FromRoute] string contextId,
    [Required] string callerId,
    ILogger<Program> logger) =>
{
    foreach (var cloudEvent in cloudEvents)
    {
        CallAutomationEventBase @event = CallAutomationEventParser.Parse(cloudEvent);
        logger.LogInformation($"Event received: {JsonConvert.SerializeObject(@event, Formatting.Indented)}");
        
        // Capture CallConnectionId for hangup later
        if (@event is CallConnected callConnectedEvent)
        {
            activeCallConnections[contextId] = callConnectedEvent.CallConnectionId;
            logger.LogInformation($"Call connected - storing CallConnectionId: {callConnectedEvent.CallConnectionId} for context: {contextId}");
        }
        else if (@event is CallDisconnected callDisconnectedEvent)
        {
            activeCallConnections.Remove(contextId);
            logger.LogInformation($"Call disconnected - removed CallConnectionId for context: {contextId}");
        }
    }

    return Results.Ok();
});

app.UseWebSockets();

// WebSocket handler with dependency injection support
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/ws")
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            try
            {
                var webSocket = await context.WebSockets.AcceptWebSocketAsync();

                // Extract callerId from query string
                var callerId = context.Request.Query["callerId"].FirstOrDefault() ?? "Unknown";
                Log.Information($"WebSocket connection established with Caller ID: {callerId}");

                // Get logger from DI for this request
                var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
                var aiLogger = loggerFactory.CreateLogger<AzureVoiceLiveService>();
                
                // Create a service scope for dependency injection
                using var scope = app.Services.CreateScope();
                var serviceProvider = scope.ServiceProvider;
                
                // Pass all required dependencies including service provider
                var mediaService = new AcsMediaStreamingHandler(
                    webSocket, 
                    builder.Configuration, 
                    aiLogger, 
                    callerId, 
                    client, 
                    activeCallConnections,
                    serviceProvider);

                // Process the WebSocket connection
                await mediaService.ProcessWebSocketAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Exception received");
            }
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
    }
    else
    {
        await next(context);
    }
});

await app.RunAsync();
