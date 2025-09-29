namespace CallAutomation.AzureAI.VoiceLive.Services.Interfaces
{
    /// <summary>
    /// Interface for managing call sessions, limits, and timeouts
    /// </summary>
    public interface ICallSessionManager
    {
        /// <summary>
        /// Check if a new call can be accepted based on current limits
        /// </summary>
        bool CanAcceptNewCall();

        /// <summary>
        /// Start tracking a new call session with timeout
        /// </summary>
        void StartCallSession(string callerId, string connectionId);

        /// <summary>
        /// End a call session and stop tracking
        /// </summary>
        void EndCallSession(string callerId);

        /// <summary>
        /// Get current number of active calls
        /// </summary>
        int GetActiveCallCount();

        /// <summary>
        /// Check if a specific call has exceeded the timeout
        /// </summary>
        bool IsCallExpired(string callerId);

        /// <summary>
        /// Get remaining time for a call session
        /// </summary>
        TimeSpan GetRemainingTime(string callerId);

        /// <summary>
        /// Get all expired call sessions that need to be terminated
        /// </summary>
        List<(string CallerId, string ConnectionId, TimeSpan Overtime)> GetExpiredCalls();

        /// <summary>
        /// Get detailed session statistics for monitoring
        /// </summary>
        Dictionary<string, object> GetSessionStats();
    }
}

namespace CallAutomation.AzureAI.VoiceLive.Services
{
    using CallAutomation.AzureAI.VoiceLive.Services.Interfaces;

    /// <summary>
    /// Manages call sessions with concurrent limits and timeouts to prevent bill shock
    /// FIXED: Proper session cleanup to prevent false positive timeout alerts
    /// </summary>
    public class CallSessionManager : ICallSessionManager, IDisposable
    {
        private readonly ILogger<CallSessionManager> _logger;
        private readonly Dictionary<string, CallSession> _activeCalls = new();
        private readonly Timer _timeoutTimer;
        private readonly object _lockObject = new();
        private bool _disposed = false;

        // Configuration constants
        private const int MAX_CONCURRENT_CALLS = 2;
        private const int SESSION_TIMEOUT_SECONDS = 90; // 90 seconds max per call

        public CallSessionManager(ILogger<CallSessionManager> logger)
        {
            _logger = logger;
            
            // Check for expired calls every 10 seconds
            _timeoutTimer = new Timer(CheckForExpiredCalls, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            
            _logger.LogInformation($"📞 CallSessionManager initialized - Max calls: {MAX_CONCURRENT_CALLS}, Timeout: {SESSION_TIMEOUT_SECONDS}s");
        }

        public bool CanAcceptNewCall()
        {
            lock (_lockObject)
            {
                // Clean up any expired sessions first
                CleanupExpiredSessions();
                
                var activeCount = _activeCalls.Count;
                var canAccept = activeCount < MAX_CONCURRENT_CALLS;
                
                _logger.LogInformation($"📊 Call limit check: {activeCount}/{MAX_CONCURRENT_CALLS} active calls, can accept: {canAccept}");
                
                return canAccept;
            }
        }

        public void StartCallSession(string callerId, string connectionId)
        {
            lock (_lockObject)
            {
                // Clean up any existing session for this caller (prevent duplicates)
                if (_activeCalls.ContainsKey(callerId))
                {
                    _logger.LogWarning($"⚠️ Duplicate call session detected for: {callerId} - cleaning up old session");
                    _activeCalls.Remove(callerId);
                }

                var session = new CallSession
                {
                    CallerId = callerId,
                    ConnectionId = connectionId,
                    StartTime = DateTime.UtcNow,
                    TimeoutTime = DateTime.UtcNow.AddSeconds(SESSION_TIMEOUT_SECONDS)
                };

                _activeCalls[callerId] = session;
                
                _logger.LogInformation($"🟢 Call session started: {callerId} | Connection: {connectionId} | Timeout: {session.TimeoutTime:HH:mm:ss} UTC | Active calls: {_activeCalls.Count}/{MAX_CONCURRENT_CALLS}");
            }
        }

        public void EndCallSession(string callerId)
        {
            lock (_lockObject)
            {
                if (_activeCalls.TryGetValue(callerId, out var session))
                {
                    var duration = DateTime.UtcNow - session.StartTime;
                    _activeCalls.Remove(callerId);
                    
                    _logger.LogInformation($"🔴 Call session ended: {callerId} | Duration: {duration.TotalSeconds:F1}s | Active calls: {_activeCalls.Count}/{MAX_CONCURRENT_CALLS}");
                    
                    // FIXED: Explicitly log successful cleanup
                    _logger.LogDebug($"✅ Session cleanup completed for: {callerId}");
                }
                else
                {
                    // FIXED: Changed from Warning to Debug to reduce log noise for legitimate cleanup attempts
                    _logger.LogDebug($"🔍 Attempted to end non-existent call session: {callerId} (may have been cleaned up already)");
                }
            }
        }

        public int GetActiveCallCount()
        {
            lock (_lockObject)
            {
                // Clean up expired sessions before returning count
                CleanupExpiredSessions();
                return _activeCalls.Count;
            }
        }

        public bool IsCallExpired(string callerId)
        {
            lock (_lockObject)
            {
                if (_activeCalls.TryGetValue(callerId, out var session))
                {
                    return DateTime.UtcNow > session.TimeoutTime;
                }
                // FIXED: Return false for non-existent sessions (they're not expired, they're ended)
                return false;
            }
        }

        public TimeSpan GetRemainingTime(string callerId)
        {
            lock (_lockObject)
            {
                if (_activeCalls.TryGetValue(callerId, out var session))
                {
                    var remaining = session.TimeoutTime - DateTime.UtcNow;
                    return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
                }
                return TimeSpan.Zero;
            }
        }

        public List<(string CallerId, string ConnectionId, TimeSpan Overtime)> GetExpiredCalls()
        {
            lock (_lockObject)
            {
                var now = DateTime.UtcNow;
                var expiredCalls = new List<(string CallerId, string ConnectionId, TimeSpan Overtime)>();

                // FIXED: Create a list of sessions to check (avoid modification during iteration)
                var sessionsToCheck = _activeCalls.Values.ToList();

                foreach (var session in sessionsToCheck)
                {
                    if (now > session.TimeoutTime)
                    {
                        var overtime = now - session.TimeoutTime;
                        expiredCalls.Add((session.CallerId, session.ConnectionId, overtime));
                    }
                }

                return expiredCalls;
            }
        }

        /// <summary>
        /// FIXED: Clean up expired sessions automatically to prevent false positives
        /// </summary>
        private void CleanupExpiredSessions()
        {
            var now = DateTime.UtcNow;
            var expiredSessions = new List<string>();

            // Find expired sessions
            foreach (var kvp in _activeCalls)
            {
                if (now > kvp.Value.TimeoutTime)
                {
                    expiredSessions.Add(kvp.Key);
                }
            }

            // Remove expired sessions
            foreach (var callerId in expiredSessions)
            {
                if (_activeCalls.TryGetValue(callerId, out var session))
                {
                    var duration = now - session.StartTime;
                    var overtime = now - session.TimeoutTime;
                    
                    _activeCalls.Remove(callerId);
                    
                    _logger.LogInformation($"🧹 Auto-cleaned expired session: {callerId} | Duration: {duration.TotalSeconds:F1}s | Overtime: {overtime.TotalSeconds:F1}s");
                }
            }
        }

        /// <summary>
        /// Get detailed session information for monitoring
        /// </summary>
        public Dictionary<string, object> GetSessionStats()
        {
            lock (_lockObject)
            {
                // Clean up expired sessions before generating stats
                CleanupExpiredSessions();
                
                var now = DateTime.UtcNow;
                var sessions = _activeCalls.Values.Select(s => new
                {
                    CallerId = s.CallerId,
                    ConnectionId = s.ConnectionId,
                    DurationSeconds = (now - s.StartTime).TotalSeconds,
                    RemainingSeconds = Math.Max(0, (s.TimeoutTime - now).TotalSeconds),
                    IsExpired = now > s.TimeoutTime // This should always be false after cleanup
                }).ToList();

                return new Dictionary<string, object>
                {
                    ["ActiveCallCount"] = _activeCalls.Count,
                    ["MaxConcurrentCalls"] = MAX_CONCURRENT_CALLS,
                    ["SessionTimeoutSeconds"] = SESSION_TIMEOUT_SECONDS,
                    ["Sessions"] = sessions,
                    ["ExpiredCallCount"] = sessions.Count(s => s.IsExpired), // Should always be 0
                    ["LastCleanupTime"] = now
                };
            }
        }

        /// <summary>
        /// FIXED: Timer callback with ACTIVE call termination for bill shock prevention
        /// </summary>
        private void CheckForExpiredCalls(object? state)
        {
            try
            {
                lock (_lockObject)
                {
                    // Get expired calls before cleanup
                    var expiredCalls = GetExpiredCalls();
                    
                    if (expiredCalls.Any())
                    {
                        _logger.LogWarning($"🚨 BILL SHOCK PREVENTION: Found {expiredCalls.Count} expired call(s) - FORCE TERMINATING:");
                        
                        foreach (var (callerId, connectionId, overtime) in expiredCalls)
                        {
                            _logger.LogWarning($"   ⏰ FORCE TERMINATING: {callerId} | Connection: {connectionId} | Overtime: {overtime.TotalSeconds:F1}s");
                            
                            // CRITICAL: Actually terminate the call via callback
                            _ = Task.Run(async () => await ForceTerminateExpiredCall(callerId, connectionId, overtime));
                        }
                        
                        // Clean up session tracking
                        CleanupExpiredSessions();
                        
                        _logger.LogWarning($"🚨 BILL SHOCK PREVENTION: {expiredCalls.Count} call(s) force terminated to prevent billing overrun");
                    }
                    else
                    {
                        // FIXED: Only log periodic health check in debug mode to reduce log noise
                        _logger.LogDebug($"💚 All calls within timeout limit. Active: {_activeCalls.Count}/{MAX_CONCURRENT_CALLS}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error checking for expired calls");
            }
        }

        /// <summary>
        /// NEW: Callback delegate for force terminating expired calls
        /// </summary>
        public Func<string, string, TimeSpan, Task>? ForceTerminateCallback { get; set; }

        /// <summary>
        /// NEW: Force terminate an expired call to prevent bill shock
        /// </summary>
        private async Task ForceTerminateExpiredCall(string callerId, string connectionId, TimeSpan overtime)
        {
            try
            {
                _logger.LogError($"🚨 BILL SHOCK PREVENTION: Force terminating call {callerId} after {overtime.TotalSeconds:F1}s overtime");
                
                if (ForceTerminateCallback != null)
                {
                    await ForceTerminateCallback(callerId, connectionId, overtime);
                    _logger.LogWarning($"✅ BILL SHOCK PREVENTION: Successfully terminated expired call {callerId}");
                }
                else
                {
                    _logger.LogError($"❌ BILL SHOCK PREVENTION: No termination callback configured - cannot force end call {callerId}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ BILL SHOCK PREVENTION: Failed to force terminate call {callerId}");
            }
        }

        /// <summary>
        /// FIXED: Enhanced dispose with proper cleanup
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                lock (_lockObject)
                {
                    // Clean up any remaining sessions
                    var remainingSessions = _activeCalls.Count;
                    if (remainingSessions > 0)
                    {
                        _logger.LogInformation($"🧹 Disposing CallSessionManager - cleaning up {remainingSessions} remaining session(s)");
                        _activeCalls.Clear();
                    }
                }
                
                _timeoutTimer?.Dispose();
                _logger.LogInformation("📞 CallSessionManager disposed successfully");
                _disposed = true;
            }
        }

        /// <summary>
        /// Internal class to track individual call sessions
        /// </summary>
        private class CallSession
        {
            public string CallerId { get; set; } = string.Empty;
            public string ConnectionId { get; set; } = string.Empty;
            public DateTime StartTime { get; set; }
            public DateTime TimeoutTime { get; set; }
        }

        /// <summary>
        /// FIXED: Additional method for emergency cleanup (for admin endpoints)
        /// </summary>
        public int ForceCleanupAllSessions()
        {
            lock (_lockObject)
            {
                var count = _activeCalls.Count;
                if (count > 0)
                {
                    _logger.LogWarning($"🚨 FORCE CLEANUP: Removing {count} active session(s)");
                    _activeCalls.Clear();
                }
                return count;
            }
        }
    }
}
