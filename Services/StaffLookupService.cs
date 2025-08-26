using CallAutomation.AzureAI.VoiceLive.Models;
using CallAutomation.AzureAI.VoiceLive.Services.Interfaces;
using CallAutomation.AzureAI.VoiceLive.Services.Staff;
using Microsoft.Extensions.Logging;

namespace CallAutomation.AzureAI.VoiceLive.Services
{
    public class StaffLookupService : IStaffLookupService
    {
        private readonly TableQueryService _tableQuery;
        private readonly StaffCacheService _cache;
        private readonly FuzzyMatchingService _fuzzyMatcher;
        private readonly ILogger<StaffLookupService> _logger;

        public StaffLookupService(
            TableQueryService tableQuery,
            StaffCacheService cache,
            FuzzyMatchingService fuzzyMatcher,
            ILogger<StaffLookupService> logger)
        {
            _tableQuery = tableQuery;
            _cache = cache;
            _fuzzyMatcher = fuzzyMatcher;
            _logger = logger;
        }

        public async Task<StaffLookupResult> CheckStaffExistsAsync(string name, string? department = null)
        {
            try
            {
                var normalized = NameNormalizer.Normalize(name);
                _logger.LogInformation($"🔍 [StaffLookup] Checking: {name} (normalized: {normalized}), department: {department}");

                // Check cache first
                if (_cache.TryGetCached(name, department, out var cachedResult))
                {
                    return cachedResult;
                }

                // Try exact match
                var exactResult = await TryExactMatchAsync(normalized, name, department);
                if (exactResult.Status == StaffLookupStatus.Authorized)
                {
                    _cache.CacheResult(name, department, exactResult);
                    return exactResult;
                }
                
                if (exactResult.Status == StaffLookupStatus.MultipleFound)
                {
                    return exactResult;
                }

                // Try fuzzy matching
                _logger.LogInformation($"🔍 [StaffLookup] No exact match, trying fuzzy matching for: {name}");
                var fuzzyResult = await _fuzzyMatcher.TryFuzzyMatchAsync(name, department);
                
                if (fuzzyResult.Status != StaffLookupStatus.NotFound)
                {
                    return fuzzyResult;
                }

                // Log speech hints and return not found
                NameNormalizer.LogSpeechHints(name, _logger);
                return new StaffLookupResult 
                { 
                    Status = StaffLookupStatus.NotFound,
                    Message = $"No staff member found matching '{name}'. Please try spelling the name or provide the department."
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"🔴 Error checking staff existence for: {name}");
                return new StaffLookupResult 
                { 
                    Status = StaffLookupStatus.NotFound,
                    Message = "Error occurred during lookup"
                };
            }
        }

        public async Task<string?> GetStaffEmailAsync(string name, string? department = null)
        {
            try
            {
                var normalized = NameNormalizer.Normalize(name);
                _logger.LogInformation($"📧 [EmailLookup] Getting email for: {name}, department: {department}");

                // Check cache first
                if (_cache.TryGetCached(name, department, out var cachedResult))
                {
                    _logger.LogInformation($"✅ [Cache] Using cached email for {name}: {cachedResult.Email}");
                    return cachedResult.Email;
                }

                // Try exact match with department
                if (!string.IsNullOrWhiteSpace(department))
                {
                    var entity = await _tableQuery.GetStaffByExactMatchAsync(normalized, department);
                    if (entity != null)
                    {
                        var email = TableQueryService.GetEmailFromEntity(entity);
                        if (TableQueryService.IsValidEmail(email))
                        {
                            var result = new StaffLookupResult
                            {
                                Status = StaffLookupStatus.Authorized,
                                Email = email,
                                RowKey = entity.RowKey
                            };
                            _cache.CacheResult(name, department, result);
                            return email;
                        }
                    }
                }

                // Find all matches
                var matches = await _tableQuery.FindAllMatchesAsync(normalized);
                
                if (matches.Count == 1)
                {
                    var email = TableQueryService.GetEmailFromEntity(matches[0]);
                    if (TableQueryService.IsValidEmail(email))
                    {
                        _logger.LogInformation($"✅ [EmailLookup] Single match found: {name} -> {email}");
                        return email;
                    }
                }
                else if (matches.Count > 1)
                {
                    return HandleMultipleMatches(matches, department, name);
                }

                _logger.LogWarning($"❌ [EmailLookup] No valid email found for: {name}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"🔴 Error getting staff email for: {name}");
                return null;
            }
        }

        public async Task<StaffLookupResult> ConfirmFuzzyMatchAsync(string originalName, string confirmedName, string department)
        {
            try
            {
                _logger.LogInformation($"✅ [Confirmation] User confirmed: '{originalName}' -> '{confirmedName}' in {department}");
                
                var normalizedConfirmed = NameNormalizer.Normalize(confirmedName);
                var entity = await _tableQuery.GetStaffByExactMatchAsync(normalizedConfirmed, department);
                
                if (entity != null)
                {
                    var email = TableQueryService.GetEmailFromEntity(entity);
                    if (TableQueryService.IsValidEmail(email))
                    {
                        _logger.LogInformation($"✅ [Confirmation] Confirmed match authorized: {confirmedName}");
                        return new StaffLookupResult
                        {
                            Status = StaffLookupStatus.Authorized,
                            Email = email,
                            RowKey = entity.RowKey,
                            Message = $"Confirmed and authorized: {confirmedName}"
                        };
                    }
                }
                
                return new StaffLookupResult
                {
                    Status = StaffLookupStatus.NotFound,
                    Message = "Confirmed staff member not found"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"🔴 Error confirming fuzzy match for: {originalName} -> {confirmedName}");
                return new StaffLookupResult 
                { 
                    Status = StaffLookupStatus.NotFound,
                    Message = "Error occurred during confirmation"
                };
            }
        }

        private async Task<StaffLookupResult> TryExactMatchAsync(string normalized, string originalName, string? department)
        {
            if (!string.IsNullOrWhiteSpace(department))
            {
                return await CheckWithDepartmentAsync(normalized, originalName, department);
            }
            else
            {
                return await CheckWithoutDepartmentAsync(normalized, originalName);
            }
        }

        private async Task<StaffLookupResult> CheckWithDepartmentAsync(string normalized, string originalName, string department)
        {
            var entity = await _tableQuery.GetStaffByExactMatchAsync(normalized, department);
            
            if (entity != null)
            {
                var email = TableQueryService.GetEmailFromEntity(entity);
                if (TableQueryService.IsValidEmail(email))
                {
                    _logger.LogInformation($"✅ Staff authorized: {originalName} in {department}");
                    return new StaffLookupResult
                    {
                        Status = StaffLookupStatus.Authorized,
                        Email = email,
                        RowKey = entity.RowKey
                    };
                }
                else
                {
                    return new StaffLookupResult
                    {
                        Status = StaffLookupStatus.NotAuthorized,
                        Message = "Invalid email address"
                    };
                }
            }
            else
            {
                return new StaffLookupResult
                {
                    Status = StaffLookupStatus.NotAuthorized,
                    Message = "Staff member not found in specified department"
                };
            }
        }

        private async Task<StaffLookupResult> CheckWithoutDepartmentAsync(string normalized, string originalName)
        {
            var matches = await _tableQuery.FindAllMatchesAsync(normalized);
            
            if (matches.Count == 0)
            {
                return new StaffLookupResult
                {
                    Status = StaffLookupStatus.NotFound,
                    Message = "No staff member found with that name"
                };
            }
            else if (matches.Count == 1)
            {
                var email = TableQueryService.GetEmailFromEntity(matches[0]);
                if (TableQueryService.IsValidEmail(email))
                {
                    _logger.LogInformation($"✅ Single match authorized: {originalName}");
                    return new StaffLookupResult
                    {
                        Status = StaffLookupStatus.Authorized,
                        Email = email,
                        RowKey = matches[0].RowKey
                    };
                }
                else
                {
                    return new StaffLookupResult
                    {
                        Status = StaffLookupStatus.NotAuthorized,
                        Message = "Invalid email address"
                    };
                }
            }
            else
            {
                // Multiple matches found
                var departments = TableQueryService.ExtractDepartments(matches);
                _logger.LogInformation($"🟡 Multiple matches for {originalName}. Departments: {string.Join(", ", departments)}");
                
                return new StaffLookupResult
                {
                    Status = StaffLookupStatus.MultipleFound,
                    AvailableDepartments = departments,
                    Message = $"Multiple staff members named '{originalName}' found. Please specify department: {string.Join(", ", departments)}"
                };
            }
        }

        private string? HandleMultipleMatches(List<Azure.Data.Tables.TableEntity> matches, string? department, string name)
        {
            // If department specified, try to find exact match
            if (!string.IsNullOrWhiteSpace(department))
            {
                var deptMatch = matches.FirstOrDefault(m => 
                    m.ContainsKey("Department") && 
                    string.Equals(m["Department"]?.ToString(), department, StringComparison.OrdinalIgnoreCase));
                
                if (deptMatch != null)
                {
                    var email = TableQueryService.GetEmailFromEntity(deptMatch);
                    if (TableQueryService.IsValidEmail(email))
                    {
                        _logger.LogInformation($"✅ [EmailLookup] Department match: {name} in {department} -> {email}");
                        return email;
                    }
                }
            }

            // Fallback to first valid email
            var departments = TableQueryService.ExtractDepartments(matches);
            _logger.LogWarning($"⚠️ [EmailLookup] Multiple matches for {name}. Departments: {string.Join(", ", departments)}");
            
            var firstValidEmail = matches
                .Select(TableQueryService.GetEmailFromEntity)
                .FirstOrDefault(TableQueryService.IsValidEmail);
                
            if (firstValidEmail != null)
            {
                _logger.LogWarning($"⚠️ [EmailLookup] Using first valid email: {firstValidEmail}");
                return firstValidEmail;
            }

            return null;
        }
    }
}
