using Serilog;
using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;
using System.Net.WebSockets; // NEW: Added missing using directive
using CallAutomation.AzureAI.VoiceLive;
using CallAutomation.AzureAI.VoiceLive.Services.Interfaces;
using CallAutomation.AzureAI.VoiceLive.Services;
using CallAutomation.AzureAI.VoiceLive.Services.Staff.Matching.StringSimilarity;
using CallAutomation.AzureAI.VoiceLive.Services.Voice;

// Configure Serilog with file rotation and size limits
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/app-log-.txt",
        rollingInterval: RollingInterval.Day,           
        fileSizeLimitBytes: 10 * 1024 * 1024,          
        rollOnFileSizeLimit: true,                      
        retainedFileCountLimit: 10,                     
        shared: true)                                   
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

// NEW: Register call session management service (Singleton for shared state)
builder.Services.AddSingleton<ICallSessionManager, CallSessionManager>();

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
    ILogger<Program> logger,
    ICallSessionManager callSessionManager) =>
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
        
        logger.LogInformation($"📞 Incoming call from: {callerId}");
        
        // NEW: Check if we can accept this call (max 2 concurrent)
        if (!callSessionManager.CanAcceptNewCall())
        {
            logger.LogWarning($"🚫 Call rejected - maximum concurrent calls (2) reached. Caller: {callerId}");
            logger.LogWarning($"📊 Current active calls: {callSessionManager.GetActiveCallCount()}/2");
            
            // Return rejection response - call will not be answered
            return Results.Ok(new { 
                status = "rejected", 
                reason = "max_calls_reached",
                message = "Maximum concurrent calls (2) exceeded",
                activeCallCount = callSessionManager.GetActiveCallCount()
            });
        }
        
        logger.LogInformation($"✅ Call accepted - current active calls: {callSessionManager.GetActiveCallCount()}/2");
        logger.LogInformation($"appBaseUrl: {appBaseUrl}");
        
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

        try
        {
            AnswerCallResult answerCallResult = await client.AnswerCallAsync(options);
            logger.LogInformation($"✅ Answered call for connection id: {answerCallResult.CallConnection.CallConnectionId}");
            
            // NEW: Track this call session with 90-second timeout
            callSessionManager.StartCallSession(callerId, answerCallResult.CallConnection.CallConnectionId);
            
            var remainingTime = callSessionManager.GetRemainingTime(callerId);
            logger.LogInformation($"⏰ Call session started with {remainingTime.TotalSeconds:F0}s timeout for: {callerId}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"❌ Failed to answer call for: {callerId}");
            return Results.Problem("Failed to answer call");
        }
    }
    return Results.Ok();
});

// api to handle call back events - captures CallConnectionId
app.MapPost("/api/callbacks/{contextId}", async (
    [FromBody] CloudEvent[] cloudEvents,
    [FromRoute] string contextId,
    [Required] string callerId,
    ILogger<Program> logger,
    ICallSessionManager callSessionManager) =>
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
            
            // Log session information
            var remainingTime = callSessionManager.GetRemainingTime(callerId);
            logger.LogInformation($"⏰ Call connected with {remainingTime.TotalSeconds:F0}s remaining for: {callerId}");
        }
        else if (@event is CallDisconnected callDisconnectedEvent)
        {
            activeCallConnections.Remove(contextId);
            logger.LogInformation($"Call disconnected - removed CallConnectionId for context: {contextId}");
            
            // NEW: End call session tracking
            callSessionManager.EndCallSession(callerId);
            logger.LogInformation($"✅ Call session ended for: {callerId} | Active calls: {callSessionManager.GetActiveCallCount()}/2");
        }
    }

    return Results.Ok();
});

// NEW: Monitoring endpoint for call session status
app.MapGet("/api/monitoring/sessions", (ICallSessionManager callSessionManager) =>
{
    try
    {
        var stats = callSessionManager.GetSessionStats();
        var expiredCalls = callSessionManager.GetExpiredCalls();
        
        return Results.Ok(new
        {
            timestamp = DateTime.UtcNow,
            status = "healthy",
            sessionManagement = stats,
            expiredCalls = expiredCalls.Select(call => new
            {
                callerId = call.CallerId,
                connectionId = call.ConnectionId,
                overtimeSeconds = call.Overtime.TotalSeconds
            }).ToList(),
            policies = new
            {
                maxConcurrentCalls = 2,
                sessionTimeoutSeconds = 90,
                nameCollectionRequired = true,
                firstAndLastNameMandatory = true
            }
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error retrieving session stats: {ex.Message}");
    }
});

// NEW: Health check endpoint with session awareness
app.MapGet("/api/health", (ICallSessionManager callSessionManager) =>
{
    try
    {
        var activeCallCount = callSessionManager.GetActiveCallCount();
        var expiredCalls = callSessionManager.GetExpiredCalls();
        
        var healthStatus = new
        {
            status = expiredCalls.Any() ? "warning" : "healthy",
            timestamp = DateTime.UtcNow,
            activeCalls = activeCallCount,
            maxCalls = 2,
            capacityUtilization = $"{(activeCallCount * 100) / 2}%",
            expiredCallsCount = expiredCalls.Count,
            sessionTimeoutPolicy = "90 seconds",
            nameCollectionPolicy = "First and Last Name Required",
            billShockPrevention = expiredCalls.Any() ? "Active - Expired calls detected!" : "Active"
        };
        
        // Include expired call details if any exist
        if (expiredCalls.Any())
        {
            return Results.Ok(new
            {
                status = "warning",
                timestamp = DateTime.UtcNow,
                activeCalls = activeCallCount,
                maxCalls = 2,
                capacityUtilization = $"{(activeCallCount * 100) / 2}%",
                expiredCallsCount = expiredCalls.Count,
                sessionTimeoutPolicy = "90 seconds",
                nameCollectionPolicy = "First and Last Name Required",
                billShockPrevention = "Active - Expired calls detected!",
                expiredCalls = expiredCalls.Select(c => new
                {
                    callerId = c.CallerId,
                    overtimeSeconds = c.Overtime.TotalSeconds
                })
            });
        }
        
        return Results.Ok(healthStatus);
    }
    catch (Exception ex)
    {
        return Results.Problem(new
        {
            status = "unhealthy",
            timestamp = DateTime.UtcNow,
            error = ex.Message
        }.ToString());
    }
});

// NEW: Get detailed call statistics
app.MapGet("/api/monitoring/statistics", (ICallSessionManager callSessionManager) =>
{
    try
    {
        var stats = callSessionManager.GetSessionStats();
        var sessions = stats["Sessions"] as List<object> ?? new List<object>();
        
        return Results.Ok(new
        {
            timestamp = DateTime.UtcNow,
            summary = new
            {
                totalActiveCalls = stats["ActiveCallCount"],
                maxConcurrentCalls = stats["MaxConcurrentCalls"],
                sessionTimeoutSeconds = stats["SessionTimeoutSeconds"],
                expiredCallCount = stats["ExpiredCallCount"],
                availableCapacity = (int)stats["MaxConcurrentCalls"] - (int)stats["ActiveCallCount"]
            },
            activeSessions = sessions,
            policies = new
            {
                billShockPrevention = new
                {
                    enabled = true,
                    maxSessionDuration = "90 seconds",
                    autoTermination = true
                },
                concurrencyControl = new
                {
                    enabled = true,
                    maxConcurrentCalls = 2,
                    rejectionPolicy = "New calls rejected when limit reached"
                },
                callerIdentification = new
                {
                    enabled = true,
                    requirement = "First and Last Name mandatory before message collection",
                    enforcement = "Strict - No messages without full caller identification"
                }
            }
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error retrieving statistics: {ex.Message}");
    }
});

// NEW: Force end expired calls endpoint (emergency use)
app.MapPost("/api/admin/force-end-expired", async (
    ICallSessionManager callSessionManager,
    ICallManagementService callManagementService,
    ILogger<Program> logger) =>
{
    try
    {
        var expiredCalls = callSessionManager.GetExpiredCalls();
        var results = new List<object>();
        
        if (!expiredCalls.Any())
        {
            return Results.Ok(new
            {
                timestamp = DateTime.UtcNow,
                message = "No expired calls found",
                expiredCallsProcessed = 0,
                results = new List<object>()
            });
        }
        
        logger.LogWarning($"🚨 ADMIN: Force ending {expiredCalls.Count} expired calls");
        
        foreach (var (callerId, connectionId, overtime) in expiredCalls)
        {
            try
            {
                logger.LogWarning($"🚨 ADMIN: Force ending expired call: {callerId} (overtime: {overtime.TotalSeconds:F1}s)");
                
                // Initialize call management service if needed
                using var scope = app.Services.CreateScope();
                var callMgmt = scope.ServiceProvider.GetRequiredService<ICallManagementService>();
                callMgmt.Initialize(client, activeCallConnections);
                
                var success = await callMgmt.HangUpCallAsync(callerId);
                callSessionManager.EndCallSession(callerId);
                
                results.Add(new
                {
                    callerId = callerId,
                    connectionId = connectionId,
                    overtimeSeconds = overtime.TotalSeconds,
                    forceEndSuccess = success,
                    action = "terminated"
                });
                
                logger.LogInformation($"✅ ADMIN: Successfully force-ended call: {callerId}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"❌ ADMIN: Failed to force-end call: {callerId}");
                results.Add(new
                {
                    callerId = callerId,
                    connectionId = connectionId,
                    overtimeSeconds = overtime.TotalSeconds,
                    forceEndSuccess = false,
                    error = ex.Message,
                    action = "failed"
                });
            }
        }
        
        return Results.Ok(new
        {
            timestamp = DateTime.UtcNow,
            expiredCallsProcessed = expiredCalls.Count,
            results = results,
            message = $"Processed {expiredCalls.Count} expired calls"
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "❌ ADMIN: Error in force-end-expired endpoint");
        return Results.Problem($"Error force-ending expired calls: {ex.Message}");
    }
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
                Log.Information($"🔗 WebSocket connection established with Caller ID: {callerId}");

                // Get call session manager to verify session is still valid
                var callSessionManager = app.Services.GetRequiredService<ICallSessionManager>();
                
                // Double-check that this call should still be active
                if (callSessionManager.IsCallExpired(callerId))
                {
                    Log.Warning($"⏰ WebSocket connection attempted for expired session: {callerId}");
                    await webSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Session expired", CancellationToken.None);
                    return;
                }

                var remainingTime = callSessionManager.GetRemainingTime(callerId);
                Log.Information($"⏰ WebSocket connected with {remainingTime.TotalSeconds:F0}s remaining for: {callerId}");

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

// Log startup information about new policies
Log.Information("🚀 Azure Voice AI Starting with Enhanced Policies:");
Log.Information("🔒 SECURITY: First and Last Name collection mandatory before messaging");
Log.Information("📞 CAPACITY: Maximum 2 concurrent calls enforced");
Log.Information("⏰ BILL SHOCK PREVENTION: 90-second session timeout with automatic termination");
Log.Information("📊 MONITORING: Real-time session tracking available at /api/monitoring/sessions");
Log.Information("🏥 HEALTH: System health monitoring at /api/health");

await app.RunAsync();
