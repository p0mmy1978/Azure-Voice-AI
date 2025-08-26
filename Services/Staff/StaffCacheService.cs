using CallAutomation.AzureAI.VoiceLive.Models;
using CallAutomation.AzureAI.VoiceLive.Services.Staff;
using Microsoft.Extensions.Logging;

namespace CallAutomation.AzureAI.VoiceLive.Services.Staff
{
    /// <summary>
    /// Handles caching for staff lookup results
    /// </summary>
    public class StaffCacheService
    {
        private readonly ILogger<StaffCacheService> _logger;
        private readonly Dictionary<string, (string Email, string RowKey, DateTime CachedAt)> _recentLookups = new();
        private readonly TimeSpan _cacheTimeout = TimeSpan.FromMinutes(10);

        public StaffCacheService(ILogger<StaffCacheService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Try to get a cached lookup result
        /// </summary>
        public bool TryGetCached(string name, string? department, out StaffLookupResult result)
        {
            var cacheKey = NameNormalizer.CreateCacheKey(NameNormalizer.Normalize(name), department);
            
            if (_recentLookups.TryGetValue(cacheKey, out var cached) && 
                DateTime.Now - cached.CachedAt < _cacheTimeout)
            {
                _logger.LogInformation($"✅ [Cache] Found cached result for key: {cacheKey}");
                result = new StaffLookupResult
                {
                    Status = StaffLookupStatus.Authorized,
                    Email = cached.Email,
                    RowKey = cached.RowKey
                };
                return true;
            }

            result = null!;
            return false;
        }

        /// <summary>
        /// Cache a successful lookup result
        /// </summary>
        public void CacheResult(string name, string? department, StaffLookupResult result)
        {
            if (result.Status == StaffLookupStatus.Authorized && !string.IsNullOrEmpty(result.Email))
            {
                var cacheKey = NameNormalizer.CreateCacheKey(NameNormalizer.Normalize(name), department);
                _recentLookups[cacheKey] = (result.Email, result.RowKey!, DateTime.Now);
                _logger.LogDebug($"📝 [Cache] Stored result for key: {cacheKey}");
            }
        }

        /// <summary>
        /// Clear expired cache entries
        /// </summary>
        public void ClearExpired()
        {
            var expired = _recentLookups.Where(kvp => DateTime.Now - kvp.Value.CachedAt > _cacheTimeout).ToList();
            foreach (var item in expired)
            {
                _recentLookups.Remove(item.Key);
            }
            
            if (expired.Any())
            {
                _logger.LogDebug($"🧹 [Cache] Cleared {expired.Count} expired entries");
            }
        }

        /// <summary>
        /// Get cache statistics
        /// </summary>
        public (int TotalEntries, int ExpiredEntries) GetStats()
        {
            var total = _recentLookups.Count;
            var expired = _recentLookups.Count(kvp => DateTime.Now - kvp.Value.CachedAt > _cacheTimeout);
            return (total, expired);
        }
    }
}
