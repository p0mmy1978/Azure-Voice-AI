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
                }
                else
                {
                    _logger.LogWarning($"⚠️ Attempted to end unknown call session: {callerId}");
                }
            }
        }

        public int GetActiveCallCount()
        {
            lock (_lockObject)
            {
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

                foreach (var session in _activeCalls.Values)
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
        /// Get detailed session information for monitoring
        /// </summary>
        public Dictionary<string, object> GetSessionStats()
        {
            lock (_lockObject)
            {
                var now = DateTime.UtcNow;
                var sessions = _activeCalls.Values.Select(s => new
                {
                    CallerId = s.CallerId,
                    ConnectionId = s.ConnectionId,
                    DurationSeconds = (now - s.StartTime).TotalSeconds,
                    RemainingSeconds = Math.Max(0, (s.TimeoutTime - now).TotalSeconds),
                    IsExpired = now > s.TimeoutTime
                }).ToList();

                return new Dictionary<string, object>
                {
                    ["ActiveCallCount"] = _activeCalls.Count,
                    ["MaxConcurrentCalls"] = MAX_CONCURRENT_CALLS,
                    ["SessionTimeoutSeconds"] = SESSION_TIMEOUT_SECONDS,
                    ["Sessions"] = sessions,
                    ["ExpiredCallCount"] = sessions.Count(s => s.IsExpired)
                };
            }
        }

        /// <summary>
        /// Timer callback to check for and log expired calls
        /// </summary>
        private void CheckForExpiredCalls(object? state)
        {
            try
            {
                var expiredCalls = GetExpiredCalls();
                
                if (expiredCalls.Any())
                {
                    foreach (var (callerId, connectionId, overtime) in expiredCalls)
                    {
                        _logger.LogWarning($"⏰ EXPIRED CALL DETECTED: {callerId} | Connection: {connectionId} | Overtime: {overtime.TotalSeconds:F1}s | REQUIRES IMMEDIATE TERMINATION");
                    }
                    
                    _logger.LogWarning($"🚨 {expiredCalls.Count} call(s) have exceeded the {SESSION_TIMEOUT_SECONDS}s timeout limit!");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error checking for expired calls");
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _timeoutTimer?.Dispose();
                _logger.LogInformation("📞 CallSessionManager disposed");
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
    }
}
