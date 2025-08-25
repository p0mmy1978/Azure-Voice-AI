using Azure.Data.Tables;
using CallAutomation.AzureAI.VoiceLive.Models;
using CallAutomation.AzureAI.VoiceLive.Services.Interfaces;

namespace CallAutomation.AzureAI.VoiceLive.Services
{
    public class StaffLookupService : IStaffLookupService
    {
        private readonly TableClient _tableClient;
        private readonly ILogger<StaffLookupService> _logger;

        public StaffLookupService(IConfiguration configuration, ILogger<StaffLookupService> logger)
        {
            _logger = logger;
            
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
                var normalized = NormalizeName(name);
                _logger.LogInformation($"🔍 [StaffLookup] Checking staff: {name} (normalized: {normalized}), department: {department}");

                // First try exact match
                var exactResult = await TryExactMatch(normalized, name, department);
                if (exactResult.Status == StaffLookupStatus.Authorized || exactResult.Status == StaffLookupStatus.MultipleFound)
                {
                    return exactResult;
                }

                // If no exact match, try fuzzy matching
                _logger.LogInformation($"🔍 [StaffLookup] No exact match found, trying fuzzy matching for: {name}");
                var fuzzyResult = await TryFuzzyMatch(name, department);
                
                if (fuzzyResult.Status != StaffLookupStatus.NotFound)
                {
                    return fuzzyResult;
                }

                // If still no match, log common mishearings for debugging
                LogCommonMishearings(name);
                
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

        private async Task<StaffLookupResult> TryFuzzyMatch(string originalName, string? department)
        {
            try
            {
                // Get all staff members for fuzzy comparison
                var allStaff = new List<(string RowKey, TableEntity Entity)>();
                
                var query = _tableClient.QueryAsync<TableEntity>(
                    filter: "PartitionKey eq 'staff'",
                    maxPerPage: 100);
                
                await foreach (var entity in query)
                {
                    allStaff.Add((entity.RowKey, entity));
                }

                _logger.LogInformation($"🔍 [FuzzyMatch] Loaded {allStaff.Count} staff members for fuzzy comparison");

                var matches = new List<(double Score, TableEntity Entity, string MatchType)>();

                foreach (var (rowKey, entity) in allStaff)
                {
                    // Extract name from RowKey (format: "firstname lastname_department" or "firstnamelastname_department")
                    var nameFromRowKey = ExtractNameFromRowKey(rowKey);
                    
                    // Try multiple fuzzy matching approaches
                    var phoneticScore = GetPhoneticSimilarity(originalName, nameFromRowKey);
                    var editDistanceScore = GetEditDistanceSimilarity(originalName, nameFromRowKey);
                    var tokenScore = GetTokenSimilarity(originalName, nameFromRowKey);

                    // Use the highest score from different matching methods
                    var bestScore = Math.Max(Math.Max(phoneticScore, editDistanceScore), tokenScore);
                    var matchType = bestScore == phoneticScore ? "Phonetic" : 
                                   bestScore == editDistanceScore ? "EditDistance" : "Token";

                    if (bestScore > 0.7) // Threshold for fuzzy matching
                    {
                        matches.Add((bestScore, entity, matchType));
                        _logger.LogInformation($"🎯 [FuzzyMatch] Found potential match: '{nameFromRowKey}' for '{originalName}' (Score: {bestScore:F2}, Method: {matchType})");
                    }
                }

                // Sort by similarity score (highest first)
                matches.Sort((a, b) => b.Score.CompareTo(a.Score));

                if (matches.Count == 0)
                {
                    return new StaffLookupResult { Status = StaffLookupStatus.NotFound };
                }

                // Filter by department if specified
                if (!string.IsNullOrWhiteSpace(department))
                {
                    var deptMatches = matches.Where(m => 
                        m.Entity.ContainsKey("Department") && 
                        string.Equals(m.Entity["Department"]?.ToString(), department, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (deptMatches.Count == 1)
                    {
                        var match = deptMatches[0];
                        var email = GetEmailFromEntity(match.Entity);
                        
                        if (IsValidEmail(email))
                        {
                            var matchedName = ExtractNameFromRowKey(match.Entity.RowKey);
                            
                            // *** KEY CHANGE: Return confirmation needed instead of automatic authorization ***
                            if (match.Score < 1.0) // Not perfect match
                            {
                                _logger.LogInformation($"❓ [FuzzyMatch] Fuzzy match found: '{matchedName}' for '{originalName}' in {department} (Score: {match.Score:F2}) - requesting confirmation");
                                
                                return new StaffLookupResult
                                {
                                    Status = StaffLookupStatus.ConfirmationNeeded,
                                    Email = email,
                                    RowKey = match.Entity.RowKey,
                                    Message = $"confirm:{originalName}:{matchedName}:{department}:{match.Score:F2}"
                                };
                            }
                            
                            _logger.LogInformation($"✅ [FuzzyMatch] Perfect department match found: '{matchedName}' for '{originalName}' in {department}");
                            
                            return new StaffLookupResult
                            {
                                Status = StaffLookupStatus.Authorized,
                                Email = email,
                                RowKey = match.Entity.RowKey,
                                Message = $"Found staff member '{matchedName}'"
                            };
                        }
                    }
                    else if (deptMatches.Count > 1)
                    {
                        return new StaffLookupResult
                        {
                            Status = StaffLookupStatus.MultipleFound,
                            Message = "Multiple similar staff members found in the specified department"
                        };
                    }
                }
                else
                {
                    // No department specified - use best match if confidence is high enough
                    var bestMatch = matches[0];
                    
                    // Higher threshold when no department is specified
                    if (bestMatch.Score >= 0.95) // Very high confidence for auto-approval without department
                    {
                        var email = GetEmailFromEntity(bestMatch.Entity);
                        
                        if (IsValidEmail(email))
                        {
                            var matchedName = ExtractNameFromRowKey(bestMatch.Entity.RowKey);
                            _logger.LogInformation($"✅ [FuzzyMatch] Very high-confidence match found: '{matchedName}' for '{originalName}' (Score: {bestMatch.Score:F2}, Method: {bestMatch.MatchType})");
                            
                            return new StaffLookupResult
                            {
                                Status = StaffLookupStatus.Authorized,
                                Email = email,
                                RowKey = bestMatch.Entity.RowKey,
                                Message = $"Found staff member '{matchedName}'"
                            };
                        }
                    }
                    else if (bestMatch.Score > 0.75) // Good match but needs confirmation
                    {
                        var email = GetEmailFromEntity(bestMatch.Entity);
                        
                        if (IsValidEmail(email))
                        {
                            var matchedName = ExtractNameFromRowKey(bestMatch.Entity.RowKey);
                            var department_from_entity = bestMatch.Entity.ContainsKey("Department") ? 
                                bestMatch.Entity["Department"]?.ToString() : "Unknown";
                                
                            _logger.LogInformation($"❓ [FuzzyMatch] Good match found but needs confirmation: '{matchedName}' for '{originalName}' (Score: {bestMatch.Score:F2})");
                            
                            return new StaffLookupResult
                            {
                                Status = StaffLookupStatus.ConfirmationNeeded,
                                Email = email,
                                RowKey = bestMatch.Entity.RowKey,
                                Message = $"confirm:{originalName}:{matchedName}:{department_from_entity}:{bestMatch.Score:F2}"
                            };
                        }
                    }
                    else if (matches.Count > 1 && matches.Take(2).All(m => m.Score > 0.7))
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
                }

                return new StaffLookupResult { Status = StaffLookupStatus.NotFound };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"🔴 Error during fuzzy matching for: {originalName}");
                return new StaffLookupResult { Status = StaffLookupStatus.NotFound };
            }
        }

        // NEW METHOD: Handle user confirmation for fuzzy matches
        public async Task<StaffLookupResult> ConfirmFuzzyMatchAsync(string originalName, string confirmedName, string department)
        {
            try
            {
                _logger.LogInformation($"✅ [Confirmation] User confirmed: '{originalName}' -> '{confirmedName}' in {department}");
                
                // Now do exact lookup with confirmed name
                var normalizedConfirmed = NormalizeName(confirmedName);
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

        private void LogCommonMishearings(string heardName)
        {
            // Log potential alternatives for common speech recognition errors
            var commonMishearings = new Dictionary<string, string[]>
            {
                ["tock"] = ["tops", "talk", "tok", "tox"],
                ["tops"] = ["tock", "tops", "taps", "tips"],
                ["smith"] = ["smyth", "smythe", "schmith"],
                ["jon"] = ["john", "joan", "jean"],
                ["brian"] = ["bryan", "bryant"],
                ["mary"] = ["marie", "merry", "mary"],
                ["catherine"] = ["katherine", "kathryn", "katharine"]
            };

            var normalized = heardName.ToLowerInvariant();
            var alternatives = new List<string>();

            foreach (var (error, corrections) in commonMishearings)
            {
                if (normalized.Contains(error))
                {
                    foreach (var correction in corrections)
                    {
                        var alternative = normalized.Replace(error, correction);
                        alternatives.Add(alternative);
                    }
                }
            }

            if (alternatives.Any())
            {
                _logger.LogInformation($"💡 [SpeechHints] For '{heardName}', consider these alternatives: {string.Join(", ", alternatives)}");
            }
        }

        private string ExtractNameFromRowKey(string rowKey)
        {
            // RowKey format: "firstnamelastname_department" -> extract name part
            var underscoreIndex = rowKey.LastIndexOf('_');
            var namePart = underscoreIndex > 0 ? rowKey.Substring(0, underscoreIndex) : rowKey;
            
            // Try to reconstruct readable name by adding space before capital letters
            // This is a heuristic and may not work perfectly for all names
            var result = new System.Text.StringBuilder();
            for (int i = 0; i < namePart.Length; i++)
            {
                if (i > 0 && char.IsUpper(namePart[i]) && char.IsLower(namePart[i - 1]))
                {
                    result.Append(' ');
                }
                result.Append(namePart[i]);
            }
            
            return result.ToString();
        }

        // Phonetic similarity using simple Soundex-like algorithm
        private double GetPhoneticSimilarity(string name1, string name2)
        {
            var soundex1 = GetSimpleSoundex(name1);
            var soundex2 = GetSimpleSoundex(name2);
            
            return soundex1 == soundex2 ? 1.0 : 0.0;
        }

        private string GetSimpleSoundex(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            
            var normalized = input.ToLowerInvariant().Replace(" ", "");
            var soundex = new System.Text.StringBuilder();
            
            // Keep first letter
            soundex.Append(char.ToUpperInvariant(normalized[0]));
            
            // Simple consonant mapping (simplified Soundex)
            var consonantMap = new Dictionary<char, char>
            {
                ['b'] = '1', ['f'] = '1', ['p'] = '1', ['v'] = '1',
                ['c'] = '2', ['g'] = '2', ['j'] = '2', ['k'] = '2', ['q'] = '2', ['s'] = '2', ['x'] = '2', ['z'] = '2',
                ['d'] = '3', ['t'] = '3',
                ['l'] = '4',
                ['m'] = '5', ['n'] = '5',
                ['r'] = '6'
            };
            
            for (int i = 1; i < normalized.Length && soundex.Length < 4; i++)
            {
                if (consonantMap.TryGetValue(normalized[i], out var code))
                {
                    if (soundex.Length == 1 || soundex[soundex.Length - 1] != code)
                    {
                        soundex.Append(code);
                    }
                }
            }
            
            // Pad with zeros
            while (soundex.Length < 4)
            {
                soundex.Append('0');
            }
            
            return soundex.ToString();
        }

        // Edit distance (Levenshtein) similarity
        private double GetEditDistanceSimilarity(string name1, string name2)
        {
            var s1 = name1.ToLowerInvariant().Replace(" ", "");
            var s2 = name2.ToLowerInvariant().Replace(" ", "");
            
            var distance = LevenshteinDistance(s1, s2);
            var maxLength = Math.Max(s1.Length, s2.Length);
            
            return maxLength == 0 ? 1.0 : 1.0 - (double)distance / maxLength;
        }

        private int LevenshteinDistance(string s1, string s2)
        {
            var dp = new int[s1.Length + 1, s2.Length + 1];
            
            for (int i = 0; i <= s1.Length; i++) dp[i, 0] = i;
            for (int j = 0; j <= s2.Length; j++) dp[0, j] = j;
            
            for (int i = 1; i <= s1.Length; i++)
            {
                for (int j = 1; j <= s2.Length; j++)
                {
                    var cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                    dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
                }
            }
            
            return dp[s1.Length, s2.Length];
        }

        // Token-based similarity (comparing individual words)
        private double GetTokenSimilarity(string name1, string name2)
        {
            var tokens1 = name1.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var tokens2 = name2.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            
            if (tokens1.Length == 0 || tokens2.Length == 0) return 0.0;
            
            var matches = 0;
            foreach (var token1 in tokens1)
            {
                foreach (var token2 in tokens2)
                {
                    if (GetEditDistanceSimilarity(token1, token2) > 0.8)
                    {
                        matches++;
                        break;
                    }
                }
            }
            
            return (double)matches / Math.Max(tokens1.Length, tokens2.Length);
        }

        public async Task<string?> GetStaffEmailAsync(string name, string? department = null)
        {
            var result = await CheckStaffExistsAsync(name, department);
            return result.Status == StaffLookupStatus.Authorized ? result.Email : null;
        }

        private async Task<StaffLookupResult> CheckWithDepartment(string normalized, string originalName, string department)
        {
            var normalizedDept = department.Trim().ToLowerInvariant();
            var exactRowKey = $"{normalized}_{normalizedDept}";
            
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
            _logger.LogInformation($"🔍 [StaffLookup] Searching for all matches of: {normalized}");
            
            // Query all entities that start with the normalized name
            var query = _tableClient.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq 'staff' and RowKey ge '{normalized}' and RowKey lt '{normalized}~'",
                maxPerPage: 10);
            
            var matches = new List<TableEntity>();
            await foreach (var entity in query)
            {
                // Check if RowKey starts with our normalized name
                if (entity.RowKey.StartsWith(normalized))
                {
                    matches.Add(entity);
                }
            }
            
            _logger.LogInformation($"🔍 Found {matches.Count} potential matches");
            
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
                // Multiple matches found - need clarification
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
                    Message = $"Multiple staff members found with name '{originalName}'. Please specify department."
                };
            }
        }

        private static string NormalizeName(string name)
        {
            return (name ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", "");
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
    }
}
