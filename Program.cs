using Serilog;
using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;
using System.Net.WebSockets;
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

// FIXED: Dictionary to track callerId -> callConnectionId mapping (not contextId -> callConnectionId)
var activeCallConnections = new Dictionary<string, string>(); // callerId -> callConnectionId

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

// Register call session management service with ACTIVE bill shock prevention
builder.Services.AddSingleton<ICallSessionManager>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<CallSessionManager>>();
    var callSessionManager = new CallSessionManager(logger);

    // Wire up force termination callback for bill shock prevention
    callSessionManager.ForceTerminateCallback = async (callerId, connectionId, overtime) =>
    {
        try
        {
            logger.LogError($"🚨 BILL SHOCK PREVENTION: Force terminating call {callerId} after {overtime.TotalSeconds:F1}s overtime");

            // Get call management service and force hang up
            using var scope = provider.CreateScope();
            var callManagementService = scope.ServiceProvider.GetRequiredService<ICallManagementService>();
            callManagementService.Initialize(client, activeCallConnections);

            var success = await callManagementService.HangUpCallAsync(callerId);

            if (success)
            {
                logger.LogWarning($"✅ BILL SHOCK PREVENTION: Successfully force terminated call {callerId}");
            }
            else
            {
                logger.LogError($"❌ BILL SHOCK PREVENTION: Failed to force terminate call {callerId}");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"❌ BILL SHOCK PREVENTION: Exception force terminating call {callerId}");
        }
    };

    return callSessionManager;
});

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

        // Check if we can accept this call (max 2 concurrent)
        if (!callSessionManager.CanAcceptNewCall())
        {
            logger.LogWarning($"🚫 Call rejected - maximum concurrent calls (2) reached. Caller: {callerId}");
            logger.LogWarning($"📊 Current active calls: {callSessionManager.GetActiveCallCount()}/2");

            // Return rejection response - call will not be answered
            return Results.Ok(new
            {
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

            // Track this call session with 90-second timeout and ACTIVE bill shock prevention
            callSessionManager.StartCallSession(callerId, answerCallResult.CallConnection.CallConnectionId);

            var remainingTime = callSessionManager.GetRemainingTime(callerId);
            logger.LogInformation($"⏰ Call session started with {remainingTime.TotalSeconds:F0}s timeout for: {callerId}");
            logger.LogWarning($"🚨 BILL SHOCK PREVENTION: Call will be FORCE TERMINATED at 90s limit for: {callerId}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"❌ Failed to answer call for: {callerId}");
            return Results.Problem("Failed to answer call");
        }
    }
    return Results.Ok();
});

// FIXED: api to handle call back events - now properly maps callerId to CallConnectionId
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

        // FIXED: Capture CallConnectionId and map to callerId (not contextId)
        if (@event is CallConnected callConnectedEvent)
        {
            activeCallConnections[callerId] = callConnectedEvent.CallConnectionId;
            logger.LogInformation($"Call connected - storing CallConnectionId: {callConnectedEvent.CallConnectionId} for caller: {callerId}");

            // Log session information with bill shock prevention warning
            var remainingTime = callSessionManager.GetRemainingTime(callerId);
            logger.LogInformation($"⏰ Call connected with {remainingTime.TotalSeconds:F0}s remaining for: {callerId}");
            logger.LogWarning($"🚨 BILL SHOCK PREVENTION: Active monitoring for 90s timeout on: {callerId}");
        }
        else if (@event is CallDisconnected callDisconnectedEvent)
        {
            // FIXED: Remove by callerId (not contextId)
            bool removed = activeCallConnections.Remove(callerId);
            if (removed)
            {
                logger.LogInformation($"Call disconnected - removed CallConnectionId for caller: {callerId}");
            }
            else
            {
                logger.LogWarning($"Call disconnected - CallConnectionId not found for caller: {callerId} (may have been already removed)");
            }

            // End call session tracking
            callSessionManager.EndCallSession(callerId);
            logger.LogInformation($"✅ Call session ended for: {callerId} | Active calls: {callSessionManager.GetActiveCallCount()}/2");
        }
    }

    return Results.Ok();
});

// Enhanced monitoring endpoint for call session status with bill shock prevention info
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
                billShockPrevention = new
                {
                    enabled = true,
                    forceTerminationActive = true,
                    description = "Calls automatically terminated at 90s to prevent billing overrun"
                },
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

// Enhanced health check endpoint with bill shock prevention status
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
            billShockPrevention = new
            {
                status = "Active",
                forceTermination = "Enabled",
                description = expiredCalls.Any() ? "Active - Expired calls detected and terminated!" : "Active - Monitoring for 90s timeout violations"
            }
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
                billShockPrevention = new
                {
                    status = "ACTIVE - CALLS TERMINATED",
                    forceTermination = "Enabled",
                    description = "Expired calls detected and automatically terminated to prevent billing overrun"
                },
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

// Get detailed call statistics with bill shock prevention metrics
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
                    autoTermination = true,
                    forceTerminationEnabled = true,
                    description = "Calls are forcibly terminated at 90s to prevent billing overrun"
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

// Enhanced force end expired calls endpoint with bill shock prevention
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
                results = new List<object>(),
                billShockPrevention = "No action needed - all calls within limits"
            });
        }

        logger.LogError($"🚨 ADMIN BILL SHOCK PREVENTION: Force ending {expiredCalls.Count} expired calls");

        foreach (var (callerId, connectionId, overtime) in expiredCalls)
        {
            try
            {
                logger.LogError($"🚨 ADMIN BILL SHOCK PREVENTION: Force ending expired call: {callerId} (overtime: {overtime.TotalSeconds:F1}s)");

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
                    action = "terminated",
                    reason = "Bill shock prevention - 90s timeout exceeded"
                });

                logger.LogWarning($"✅ ADMIN BILL SHOCK PREVENTION: Successfully force-ended call: {callerId}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"❌ ADMIN BILL SHOCK PREVENTION: Failed to force-end call: {callerId}");
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
            message = $"BILL SHOCK PREVENTION: Processed {expiredCalls.Count} expired calls",
            billShockPrevention = "Emergency termination completed"
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "❌ ADMIN BILL SHOCK PREVENTION: Error in force-end-expired endpoint");
        return Results.Problem($"Error force-ending expired calls: {ex.Message}");
    }
});

app.UseWebSockets();

// WebSocket handler with dependency injection support and bill shock prevention
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
                Log.Warning($"🚨 BILL SHOCK PREVENTION: Call will be force terminated if it exceeds 90s total duration");

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

// Enhanced startup logging with bill shock prevention information
Log.Information("🚀 Azure Voice AI Starting with Enhanced Policies:");
Log.Information("🔒 SECURITY: First and Last Name collection mandatory before messaging");
Log.Information("📞 CAPACITY: Maximum 2 concurrent calls enforced");
Log.Information("⏰ BILL SHOCK PREVENTION: 90-second session timeout with AUTOMATIC CALL TERMINATION");
Log.Information("🚨 FORCE TERMINATION: Calls automatically hung up at 90s to prevent billing overrun");
Log.Information("📊 MONITORING: Real-time session tracking available at /api/monitoring/sessions");
Log.Information("🏥 HEALTH: System health monitoring at /api/health");
Log.Information("⚡ EMERGENCY: Manual expired call termination at /api/admin/force-end-expired");
Log.Information("🔧 FIX APPLIED: CallerId to CallConnectionId mapping corrected to prevent cross-call hangups");

await app.RunAsync();
