using Serilog;
using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;
using CallAutomation.AzureAI.VoiceLive;


// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/app-log.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();


var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();
// Register AcsMediaStreamingHandler for DI
// builder.Services.AddTransient<AcsMediaStreamingHandler>();

//Get ACS Connection String from appsettings.json
var acsConnectionString = builder.Configuration.GetValue<string>("AcsConnectionString");
ArgumentNullException.ThrowIfNullOrEmpty(acsConnectionString);

//Call Automation Client
var client = new CallAutomationClient(acsConnectionString);
var app = builder.Build();
var appBaseUrl = builder.Configuration["AppBaseUrl"]?.TrimEnd('/');

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
        var callbackUri = new Uri(new Uri(appBaseUrl), $"/api/callbacks/{Guid.NewGuid()}?callerId={callerId}");
        logger.LogInformation($"Callback Url: {callbackUri}");
        var websocketUri = appBaseUrl.Replace("https", "wss") + "/ws";
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

// api to handle call back events
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
    }

    return Results.Ok();
});

app.UseWebSockets();

app.Use(async (context, next) =>
{
    if (context.Request.Path == "/ws")
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            try
            {
                var webSocket = await context.WebSockets.AcceptWebSocketAsync();

                // Get logger from DI for this request
                var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
                var aiLogger = loggerFactory.CreateLogger<AzureVoiceLiveService>();
                var mediaService = new AcsMediaStreamingHandler(webSocket, builder.Configuration, aiLogger);

                // Set the single WebSocket connection
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


// Example: How to get an instance and use it (logs will go to logs/app-log.txt)
// Example: If you want to test AzureVoiceLiveService logging, construct AcsMediaStreamingHandler manually with a mock or test WebSocket if needed.
// app.Lifetime.ApplicationStarted.Register(() =>
// {
//     using var scope = app.Services.CreateScope();
//     var logger = app.Services.GetRequiredService<ILogger<AzureVoiceLiveService>>();
//     var fakeWebSocket = ... // Provide a mock/test WebSocket
//     var mediaStreaming = new AcsMediaStreamingHandler(fakeWebSocket, builder.Configuration, logger);
//     var azureVoiceLiveService = new AzureVoiceLiveService(mediaStreaming, builder.Configuration, logger);
//     azureVoiceLiveService.StartConversation();
// });

await app.RunAsync();
