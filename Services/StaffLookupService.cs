using Azure.Data.Tables;
using CallAutomation.AzureAI.VoiceLive.Models;
using CallAutomation.AzureAI.VoiceLive.Services.Interfaces;
using CallAutomation.AzureAI.VoiceLive.Services.Staff;
using CallAutomation.AzureAI.VoiceLive.Services.Staff.Matching.StringSimilarity;

namespace CallAutomation.AzureAI.VoiceLive.Services
{
    public class StaffLookupService : IStaffLookupService
    {
        private readonly TableClient _tableClient;
        private readonly ILogger<StaffLookupService> _logger;
        private readonly CompositeSimilarityMatcher _similarityMatcher;
        
        // Cache for recent successful lookups to avoid repeated table queries
        private readonly Dictionary<string, (string Email, string RowKey, DateTime CachedAt)> _recentLookups = new();
        private readonly TimeSpan _cacheTimeout = TimeSpan.FromMinutes(10);

        public StaffLookupService(
            IConfiguration configuration, 
            ILogger<StaffLookupService> logger,
            CompositeSimilarityMatcher similarityMatcher)
        {
            _logger = logger;
            _similarityMatcher = similarityMatcher;
            
            var tableServiceUri = new Uri(configuration["StorageUri"]!);
            _tableClient = new TableClient(
                tableServiceUri,
                configuration["TableName"]!,
                new TableSharedKeyCredential(
                    configuration["StorageAccountName"]!,
                    configuration["StorageAccountKey"]!));
        }

        public async Task<StaffLookupResult> CheckStaffExistsAsync(string name, string? department = null)
        {
            try
            {
                var normalized = NameNormalizer.Normalize(name);
                _logger.LogInformation($"🔍 [StaffLookup] Checking staff: {name} (normalized: {normalized}), department: {department}");

                // Check cache first for exact matches
                var cacheKey = NameNormalizer.CreateCacheKey(normalized, department);
                if (TryGetFromCache(cacheKey, out var cachedResult))
                {
                    return cachedResult;
                }

                // First try exact match
                var exactResult = await TryExactMatch(normalized, name, department);
                if (exactResult.Status == StaffLookupStatus.Authorized)
                {
                    CacheSuccessfulLookup(cacheKey, exactResult);
                    return exactResult;
                }
                
                if (exactResult.Status == StaffLookupStatus.MultipleFound)
                {
                    return exactResult; // Return multiple found for user clarification
                }

                // If no exact match, try fuzzy matching
                _logger.LogInformation($"🔍 [StaffLookup] No exact match found, trying fuzzy matching for: {name}");
                var fuzzyResult = await TryFuzzyMatch(name, department);
                
                if (fuzzyResult.Status != StaffLookupStatus.NotFound)
                {
                    return fuzzyResult;
                }

                // If still no match, log common mishearings for debugging
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
                _logger.LogInformation($"📧 [EmailLookup] Getting email for: {name} (normalized: {normalized}), department: {department}");

                // Check cache first
                var cacheKey = NameNormalizer.CreateCacheKey(normalized, department);
                if (TryGetFromCache(cacheKey, out var cachedResult) && cachedResult.Status == StaffLookupStatus.Authorized)
                {
                    _logger.LogInformation($"✅ [Cache] Using cached email for {name}: {cachedResult.Email}");
                    return cachedResult.Email;
                }

                // If department is specified, do exact lookup first
                if (!string.IsNullOrWhiteSpace(department))
                {
                    var exactEmail = await GetEmailWithDepartment(normalized, name, department);
                    if (!string.IsNullOrWhiteSpace(exactEmail))
                    {
                        // Cache successful lookup
                        var lookupResult = new StaffLookupResult
                        {
                            Status = StaffLookupStatus.Authorized,
                            Email = exactEmail,
                            RowKey = NameNormalizer.CreateRowKey(normalized, department)
                        };
                        CacheSuccessfulLookup(cacheKey, lookupResult);
                        return exactEmail;
                    }
                }

                // If no department specified or exact lookup failed, find all matches
                var matches = await FindAllMatches(normalized, name);
                
                if (matches.Count == 1)
                {
                    var email = GetEmailFromEntity(matches[0]);
                    if (IsValidEmail(email))
                    {
                        _logger.LogInformation($"✅ [EmailLookup] Single match found: {name} -> {email}");
                        return email;
                    }
                }
                else if (matches.Count > 1)
                {
                    return await HandleMultipleEmailMatches(matches, department, name, cacheKey);
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
                
                // Now do exact lookup with confirmed name
                var normalizedConfirmed = NameNormalizer.Normalize(confirmedName);
                var result = await CheckWithDepartment(normalizedConfirmed, confirmedName, department);
                
                if (result.Status == StaffLookupStatus.Authorized)
                {
                    _logger.LogInformation($"✅ [Confirmation] Confirmed match authorized: {confirmedName}");
                    result.Message = $"Confirmed and authorized: {confirmedName}";
                }
                
                return result;
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

        #region Private Methods

        private bool TryGetFromCache(string cacheKey, out StaffLookupResult result)
        {
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

        private void CacheSuccessfulLookup(string cacheKey, StaffLookupResult result)
        {
            if (result.Status == StaffLookupStatus.Authorized && !string.IsNullOrEmpty(result.Email))
            {
                _recentLookups[cacheKey] = (result.Email, result.RowKey!, DateTime.Now);
            }
        }

        private async Task<StaffLookupResult> TryExactMatch(string normalized, string originalName, string? department)
        {
            if (!string.IsNullOrWhiteSpace(department))
            {
                return await CheckWithDepartment(normalized, originalName, department);
            }
            else
            {
                return await CheckWithoutDepartment(normalized, originalName);
            }
        }

        private async Task<StaffLookupResult> CheckWithDepartment(string normalized, string originalName, string department)
        {
            var exactRowKey = NameNormalizer.CreateRowKey(normalized, department);
            
            _logger.LogInformation($"🔍 [StaffLookup] Looking up with department: {exactRowKey}");
            
            var exactResult = await _tableClient.GetEntityIfExistsAsync<TableEntity>("staff", exactRowKey);
            
            if (exactResult.HasValue && exactResult.Value != null)
            {
                var entity = exactResult.Value;
                var email = GetEmailFromEntity(entity);
                
                if (IsValidEmail(email))
                {
                    _logger.LogInformation($"✅ Staff authorized: {originalName} in {department}, email: {email}");
                    return new StaffLookupResult
                    {
                        Status = StaffLookupStatus.Authorized,
                        Email = email,
                        RowKey = exactRowKey
                    };
                }
                else
                {
                    _logger.LogWarning($"❌ Staff found but invalid email: {originalName} in {department}");
                    return new StaffLookupResult
                    {
                        Status = StaffLookupStatus.NotAuthorized,
                        Message = "Invalid email address"
                    };
                }
            }
            else
            {
                _logger.LogWarning($"❌ Staff NOT found: {originalName} in {department}");
                return new StaffLookupResult
                {
                    Status = StaffLookupStatus.NotAuthorized,
                    Message = "Staff member not found in specified department"
                };
            }
        }

        private async Task<StaffLookupResult> CheckWithoutDepartment(string normalized, string originalName)
        {
            var matches = await FindAllMatches(normalized, originalName);
            
            if (matches.Count == 0)
            {
                _logger.LogWarning($"❌ No staff found matching: {originalName}");
                return new StaffLookupResult
                {
                    Status = StaffLookupStatus.NotFound,
                    Message = "No staff member found with that name"
                };
            }
            else if (matches.Count == 1)
            {
                var match = matches[0];
                var email = GetEmailFromEntity(match);
                
                if (IsValidEmail(email))
                {
                    _logger.LogInformation($"✅ Single match found and authorized: {originalName}");
                    return new StaffLookupResult
                    {
                        Status = StaffLookupStatus.Authorized,
                        Email = email,
                        RowKey = match.RowKey
                    };
                }
                else
                {
                    _logger.LogWarning($"❌ Single match found but invalid email: {originalName}");
                    return new StaffLookupResult
                    {
                        Status = StaffLookupStatus.NotAuthorized,
                        Message = "Invalid email address"
                    };
                }
            }
            else
            {
                return HandleMultipleMatches(matches, originalName);
            }
        }

        private StaffLookupResult HandleMultipleMatches(List<TableEntity> matches, string originalName)
        {
            var departments = matches.Where(m => m.ContainsKey("Department"))
                .Select(m => m["Department"]?.ToString())
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct()
                .ToList();
            
            _logger.LogInformation($"🟡 Multiple matches found for {originalName}. Departments: {string.Join(", ", departments)}");
            
            return new StaffLookupResult
            {
                Status = StaffLookupStatus.MultipleFound,
                AvailableDepartments = departments,
                Message = $"Multiple staff members named '{originalName}' found. Please specify which department: {string.Join(", ", departments)}"
            };
        }

        private async Task<List<TableEntity>> FindAllMatches(string normalized, string originalName)
        {
            _logger.LogInformation($"🔍 [StaffLookup] Searching for all matches of: {normalized}");
            
            var matches = new List<TableEntity>();
            
            // Query all entities that start with the normalized name
            var query = _tableClient.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq 'staff' and RowKey ge '{normalized}' and RowKey lt '{normalized}~'",
                maxPerPage: 10);
            
            await foreach (var entity in query)
            {
                // Check if RowKey starts with our normalized name
                if (entity.RowKey.StartsWith(normalized))
                {
                    matches.Add(entity);
                }
            }
            
            _logger.LogInformation($"🔍 [StaffLookup] Found {matches.Count} potential matches for {originalName}");
            return matches;
        }

        private async Task<StaffLookupResult> TryFuzzyMatch(string originalName, string? department)
        {
            try
            {
                // Get all staff members for fuzzy comparison
                var allStaff = await LoadAllStaffForFuzzyMatching();
                var matches = new List<(double Score, TableEntity Entity, string MatchType)>();

                foreach (var (rowKey, entity) in allStaff)
                {
                    // Extract name from RowKey (format: "firstname lastname_department" or "firstnamelastname_department")
                    var nameFromRowKey = NameNormalizer.ExtractNameFromRowKey(rowKey);
                    
                    // Use the composite similarity matcher
                    var result = _similarityMatcher.CalculateBestMatch(originalName, nameFromRowKey);

                    if (result.Score > 0.7) // Threshold for fuzzy matching
                    {
                        matches.Add((result.Score, entity, result.Algorithm));
                        _logger.LogInformation($"🎯 [FuzzyMatch] Found potential match: '{nameFromRowKey}' for '{originalName}' (Score: {result.Score:F2}, Method: {result.Algorithm})");
                    }
                }

                return await ProcessFuzzyMatches(matches, originalName, department);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"🔴 Error during fuzzy matching for: {originalName}");
                return new StaffLookupResult { Status = StaffLookupStatus.NotFound };
            }
        }

        private async Task<List<(string RowKey, TableEntity Entity)>> LoadAllStaffForFuzzyMatching()
        {
            var allStaff = new List<(string RowKey, TableEntity Entity)>();
            
            var query = _tableClient.QueryAsync<TableEntity>(
                filter: "PartitionKey eq 'staff'",
                maxPerPage: 100);
            
            await foreach (var entity in query)
            {
                allStaff.Add((entity.RowKey, entity));
            }

            _logger.LogInformation($"🔍 [FuzzyMatch] Loaded {allStaff.Count} staff members for fuzzy comparison");
            return allStaff;
        }

        private async Task<StaffLookupResult> ProcessFuzzyMatches(
            List<(double Score, TableEntity Entity, string MatchType)> matches, 
            string originalName, 
            string? department)
        {
            if (matches.Count == 0)
            {
                return new StaffLookupResult { Status = StaffLookupStatus.NotFound };
            }

            // Sort by similarity score (highest first)
            matches.Sort((a, b) => b.Score.CompareTo(a.Score));

            // Filter by department if specified
            if (!string.IsNullOrWhiteSpace(department))
            {
                return await ProcessDepartmentFilteredMatches(matches, originalName, department);
            }
            else
            {
                return await ProcessUnfilteredMatches(matches, originalName);
            }
        }

        private async Task<StaffLookupResult> ProcessDepartmentFilteredMatches(
            List<(double Score, TableEntity Entity, string MatchType)> matches, 
            string originalName, 
            string department)
        {
            var deptMatches = matches.Where(m => 
                m.Entity.ContainsKey("Department") && 
                string.Equals(m.Entity["Department"]?.ToString(), department, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (deptMatches.Count == 1)
            {
                return await CreateMatchResult(deptMatches[0], originalName, department, deptMatches[0].Score < 1.0);
            }
            else if (deptMatches.Count > 1)
            {
                return new StaffLookupResult
                {
                    Status = StaffLookupStatus.MultipleFound,
                    Message = "Multiple similar staff members found in the specified department"
                };
            }

            return new StaffLookupResult { Status = StaffLookupStatus.NotFound };
        }

        private async Task<StaffLookupResult> ProcessUnfilteredMatches(
            List<(double Score, TableEntity Entity, string MatchType)> matches, 
            string originalName)
        {
            var bestMatch = matches[0];
            
            // Higher threshold when no department is specified
            if (bestMatch.Score >= 0.95) // Very high confidence for auto-approval without department
            {
                return await CreateMatchResult(bestMatch, originalName, null, false);
            }
            else if (bestMatch.Score > 0.75) // Good match but needs confirmation
            {
                var department_from_entity = bestMatch.Entity.ContainsKey("Department") ? 
                    bestMatch.Entity["Department"]?.ToString() : "Unknown";
                return await CreateMatchResult(bestMatch, originalName, department_from_entity, true);
            }
            else if (matches.Count > 1 && matches.Take(2).All(m => m.Score > 0.7))
            {
                return HandleMultipleFuzzyMatches(matches, originalName);
            }

            return new StaffLookupResult { Status = StaffLookupStatus.NotFound };
        }

        private async Task<StaffLookupResult> CreateMatchResult(
            (double Score, TableEntity Entity, string MatchType) match, 
            string originalName, 
            string? department, 
            bool needsConfirmation)
        {
            var email = GetEmailFromEntity(match.Entity);
            
            if (!IsValidEmail(email))
            {
                return new StaffLookupResult { Status = StaffLookupStatus.NotFound };
            }

            var matchedName = NameNormalizer.ExtractNameFromRowKey(match.Entity.RowKey);
            
            if (needsConfirmation)
            {
                _logger.LogInformation($"❓ [FuzzyMatch] Match found but needs confirmation: '{matchedName}' for '{originalName}' (Score: {match.Score:F2})");
                
                return new StaffLookupResult
                {
                    Status = StaffLookupStatus.ConfirmationNeeded,
                    Email = email,
                    RowKey = match.Entity.RowKey,
                    Message = $"confirm:{originalName}:{matchedName}:{department}:{match.Score:F2}"
                };
            }
            
            _logger.LogInformation($"✅ [FuzzyMatch] High-confidence match found: '{matchedName}' for '{originalName}' (Score: {match.Score:F2}, Method: {match.MatchType})");
            
            return new StaffLookupResult
            {
                Status = StaffLookupStatus.Authorized,
                Email = email,
                RowKey = match.Entity.RowKey,
                Message = $"Found staff member '{matchedName}'"
            };
        }

        private StaffLookupResult HandleMultipleFuzzyMatches(
            List<(double Score, TableEntity Entity, string MatchType)> matches, 
            string originalName)
        {
            // Multiple good matches - ask for department
            var departments = matches.Take(3)
                .Where(m => m.Entity.ContainsKey("Department"))
                .Select(m => m.Entity["Department"]?.ToString())
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct()
                .ToList();

            return new StaffLookupResult
            {
                Status = StaffLookupStatus.MultipleFound,
                AvailableDepartments = departments,
                Message = $"Found multiple similar staff members for '{originalName}'. Please specify the department."
            };
        }

        private async Task<string?> GetEmailWithDepartment(string normalized, string originalName, string department)
        {
            var exactRowKey = NameNormalizer.CreateRowKey(normalized, department);
            
            _logger.LogInformation($"🔍 [EmailLookup] Looking up with department: {exactRowKey}");
            
            var exactResult = await _tableClient.GetEntityIfExistsAsync<TableEntity>("staff", exactRowKey);
            
            if (exactResult.HasValue && exactResult.Value != null)
            {
                var email = GetEmailFromEntity(exactResult.Value);
                if (IsValidEmail(email))
                {
                    _logger.LogInformation($"✅ [EmailLookup] Found exact department match: {originalName} in {department} -> {email}");
                    return email;
                }
            }
            
            return null;
        }

        private async Task<string?> HandleMultipleEmailMatches(
            List<TableEntity> matches, 
            string? department, 
            string name, 
            string cacheKey)
        {
            // Multiple matches - if department was specified, try to find the right one
            if (!string.IsNullOrWhiteSpace(department))
            {
                var deptMatches = matches.Where(m => 
                    m.ContainsKey("Department") && 
                    string.Equals(m["Department"]?.ToString(), department, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (deptMatches.Count == 1)
                {
                    var email = GetEmailFromEntity(deptMatches[0]);
                    if (IsValidEmail(email))
                    {
                        _logger.LogInformation($"✅ [EmailLookup] Department match found: {name} in {department} -> {email}");
                        // Cache this successful lookup
                        var lookupResult = new StaffLookupResult
                        {
                            Status = StaffLookupStatus.Authorized,
                            Email = email,
                            RowKey = deptMatches[0].RowKey
                        };
                        CacheSuccessfulLookup(cacheKey, lookupResult);
                        return email;
                    }
                }
            }

            // If we still have multiple matches, log the issue but try the first valid email
            // This is a fallback to prevent complete failure
            var departments = matches.Where(m => m.ContainsKey("Department"))
                .Select(m => m["Department"]?.ToString())
                .ToList();
            
            _logger.LogWarning($"⚠️ [EmailLookup] Multiple matches found for {name}. Departments: {string.Join(", ", departments)}");
            
            // Try to use the first valid email as a fallback
            var firstValidEmail = matches
                .Select(GetEmailFromEntity)
                .FirstOrDefault(IsValidEmail);
                
            if (firstValidEmail != null)
            {
                _logger.LogWarning($"⚠️ [EmailLookup] Using first valid email as fallback: {firstValidEmail}");
                return firstValidEmail;
            }

            return null;
        }

        private static string? GetEmailFromEntity(TableEntity entity)
        {
            var emailObj = entity.ContainsKey("email") ? entity["email"] : null;
            return emailObj?.ToString()?.Trim();
        }

        private static bool IsValidEmail(string? email)
        {
            return !string.IsNullOrWhiteSpace(email) && email.Contains("@");
        }

        #endregion
    }
}
