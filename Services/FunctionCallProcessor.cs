using System.Text.Json;
using CallAutomation.AzureAI.VoiceLive.Models;
using CallAutomation.AzureAI.VoiceLive.Services.Interfaces;

namespace CallAutomation.AzureAI.VoiceLive.Services
{
    public class FunctionCallProcessor : IFunctionCallProcessor
    {
        private readonly IStaffLookupService _staffLookupService;
        private readonly IEmailService _emailService;
        private readonly ILogger<FunctionCallProcessor> _logger;

        // Track caller name collection state per call
        private readonly Dictionary<string, CallerInfo> _callerInfoCache = new();
        
        // NEW: Cache for name corrections from fuzzy matching
        private readonly Dictionary<string, NameCorrection> _nameCorrections = new();

        public FunctionCallProcessor(
            IStaffLookupService staffLookupService,
            IEmailService emailService,
            ILogger<FunctionCallProcessor> logger)
        {
            _staffLookupService = staffLookupService;
            _emailService = emailService;
            _logger = logger;
        }

        public async Task<FunctionCallResult> ProcessFunctionCallAsync(string functionName, string arguments, string callId, string callerId)
        {
            _logger.LogInformation($"🟢 Processing function call: {functionName} with args: {arguments}");

            try
            {
                return functionName switch
                {
                    "collect_caller_name" => HandleCollectCallerName(arguments, callerId),
                    "check_staff_exists" => await HandleCheckStaffExists(arguments, callerId),
                    "confirm_staff_match" => await HandleConfirmStaffMatch(arguments, callerId),
                    "send_message" => await HandleSendMessage(arguments, callerId),
                    "end_call" => HandleEndCall(callerId),
                    _ => new FunctionCallResult
                    {
                        Success = false,
                        Output = "unknown_function",
                        ErrorMessage = $"Unknown function: {functionName}"
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"🔴 Failed to process function call: {functionName}");
                return new FunctionCallResult
                {
                    Success = false,
                    Output = "error",
                    ErrorMessage = ex.Message
                };
            }
        }

        public async Task<bool> SendFunctionResponseAsync(string callId, string output, Func<string, CancellationToken, Task> sendMessageCallback)
        {
            try
            {
                // Send function response back to AI
                var functionResponse = new
                {
                    type = "conversation.item.create",
                    item = new
                    {
                        type = "function_call_output",
                        call_id = callId,
                        output = output
                    }
                };

                var jsonResponse = JsonSerializer.Serialize(functionResponse, new JsonSerializerOptions { WriteIndented = true });
                _logger.LogInformation($"[DEBUG] Sending function response to AI: {jsonResponse}");
                await sendMessageCallback(jsonResponse, CancellationToken.None);

                // Trigger AI response
                var createResponse = new { type = "response.create" };
                var jsonCreateResponse = JsonSerializer.Serialize(createResponse, new JsonSerializerOptions { WriteIndented = true });
                _logger.LogInformation($"[DEBUG] Triggering AI response: {jsonCreateResponse}");
                await sendMessageCallback(jsonCreateResponse, CancellationToken.None);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🔴 Failed to send function response to AI");
                return false;
            }
        }

        private FunctionCallResult HandleCollectCallerName(string arguments, string callerId)
        {
            _logger.LogInformation($"📝 collect_caller_name called with args: {arguments}");

            try
            {
                var parsed = JsonDocument.Parse(arguments);
                var firstName = parsed.RootElement.GetProperty("first_name").GetString()?.Trim();
                var lastName = parsed.RootElement.GetProperty("last_name").GetString()?.Trim();

                if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
                {
                    _logger.LogWarning($"⚠️ Incomplete name collection: first='{firstName}', last='{lastName}'");
                    return new FunctionCallResult
                    {
                        Success = false,
                        Output = "incomplete_name",
                        ErrorMessage = "Both first and last names are required"
                    };
                }

                _callerInfoCache[callerId] = new CallerInfo 
                { 
                    FirstName = firstName!, 
                    LastName = lastName!,
                    CollectedAt = DateTime.UtcNow
                };
                
                _logger.LogInformation($"✅ Caller information collected and stored: {firstName} {lastName} for call: {callerId}");

                return new FunctionCallResult
                {
                    Success = true,
                    Output = $"caller_identified|{firstName} {lastName}"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🔴 Error in HandleCollectCallerName");
                return new FunctionCallResult
                {
                    Success = false,
                    Output = "error",
                    ErrorMessage = ex.Message
                };
            }
        }

        private bool ValidateCallerIdentification(string callerId, string functionName)
        {
            if (!_callerInfoCache.TryGetValue(callerId, out var callerInfo) || 
                string.IsNullOrWhiteSpace(callerInfo.FirstName) || 
                string.IsNullOrWhiteSpace(callerInfo.LastName))
            {
                _logger.LogError($"🚨 SECURITY VIOLATION: {functionName} called without caller identification for: {callerId}");
                return false;
            }

            _logger.LogInformation($"✅ Caller validation passed: {callerInfo.FirstName} {callerInfo.LastName} for {functionName}");
            return true;
        }

        private async Task<FunctionCallResult> HandleCheckStaffExists(string arguments, string callerId)
        {
            _logger.LogInformation($"🔍 check_staff_exists called with args: {arguments}");

            if (!ValidateCallerIdentification(callerId, "check_staff_exists"))
            {
                return new FunctionCallResult
                {
                    Success = false,
                    Output = "caller_identification_required",
                    ErrorMessage = "Must call collect_caller_name first to get caller's full name before staff lookup"
                };
            }

            try
            {
                var parsed = JsonDocument.Parse(arguments);
                var name = parsed.RootElement.GetProperty("name").GetString();
                var department = parsed.RootElement.TryGetProperty("department", out var deptElement) ? 
                    deptElement.GetString() : null;

                var callerInfo = _callerInfoCache[callerId];
                _logger.LogInformation($"🔍 Checking staff: name={name}, department={department}, caller={callerInfo.FullName}");

                var result = await _staffLookupService.CheckStaffExistsAsync(name!, department);

                // NEW: Store name correction if fuzzy match was used
                if (result.Status == StaffLookupStatus.Authorized && 
                    !string.IsNullOrEmpty(result.SuggestedName) &&
                    !string.Equals(name, result.SuggestedName, StringComparison.OrdinalIgnoreCase))
                {
                    var correctionKey = CreateCorrectionKey(callerId, name!);
                    _nameCorrections[correctionKey] = new NameCorrection
                    {
                        OriginalName = name!,
                        CorrectedName = result.SuggestedName,
                        Department = result.SuggestedDepartment ?? department,
                        Email = result.Email,
                        CorrectedAt = DateTime.UtcNow
                    };
                    
                    _logger.LogInformation($"✅ Stored name correction: '{name}' → '{result.SuggestedName}' in {result.SuggestedDepartment}");
                }

                string output = result.Status switch
                {
                    StaffLookupStatus.Authorized => CreateAuthorizedOutput(result, name!, department),
                    StaffLookupStatus.NotAuthorized => "not_authorized", 
                    StaffLookupStatus.MultipleFound => CreateMultipleFoundOutput(result),
                    StaffLookupStatus.NotFound => "not_authorized",
                    StaffLookupStatus.ConfirmationNeeded => result.Message!, 
                    _ => "not_authorized"
                };

                _logger.LogInformation($"🔍 Staff check result: {result.Status} -> output: {output}");

                return new FunctionCallResult
                {
                    Success = true,
                    Output = output
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🔴 Error in HandleCheckStaffExists");
                return new FunctionCallResult
                {
                    Success = false,
                    Output = "error",
                    ErrorMessage = ex.Message
                };
            }
        }

        private string CreateAuthorizedOutput(StaffLookupResult result, string name, string? requestedDepartment)
        {
            var department = result.SuggestedDepartment ?? requestedDepartment ?? "";
            department = department?.Trim() ?? "";
            
            _logger.LogInformation($"🔍 Creating authorized output: name='{name}', department='{department}' (suggested: '{result.SuggestedDepartment}', requested: '{requestedDepartment}')");
            
            return $"authorized|{department}";
        }

        private string CreateMultipleFoundOutput(StaffLookupResult result)
        {
            if (result.AvailableDepartments.Any())
            {
                var departments = string.Join(", ", result.AvailableDepartments);
                return $"multiple_found|{departments}";
            }
            return "multiple_found";
        }

        private async Task<FunctionCallResult> HandleConfirmStaffMatch(string arguments, string callerId)
        {
            _logger.LogInformation($"✅ confirm_staff_match called with args: {arguments}");

            if (!ValidateCallerIdentification(callerId, "confirm_staff_match"))
            {
                return new FunctionCallResult
                {
                    Success = false,
                    Output = "caller_identification_required",
                    ErrorMessage = "Must call collect_caller_name first before staff confirmation"
                };
            }

            try
            {
                var parsed = JsonDocument.Parse(arguments);
                var originalName = parsed.RootElement.GetProperty("original_name").GetString();
                var confirmedName = parsed.RootElement.GetProperty("confirmed_name").GetString();
                var department = parsed.RootElement.GetProperty("department").GetString();

                var callerInfo = _callerInfoCache[callerId];
                _logger.LogInformation($"✅ User {callerInfo.FullName} confirmed: '{originalName}' -> '{confirmedName}' in {department}");

                if (_staffLookupService is StaffLookupService concreteService)
                {
                    var result = await concreteService.ConfirmFuzzyMatchAsync(originalName!, confirmedName!, department!);

                    string output = result.Status switch
                    {
                        StaffLookupStatus.Authorized => $"authorized|{department}",
                        StaffLookupStatus.NotAuthorized => "not_authorized",
                        _ => "error"
                    };

                    _logger.LogInformation($"✅ Confirmation result: {result.Status} -> output: {output}");

                    return new FunctionCallResult
                    {
                        Success = true,
                        Output = output
                    };
                }
                else
                {
                    _logger.LogError("❌ StaffLookupService is not the concrete implementation");
                    return new FunctionCallResult
                    {
                        Success = false,
                        Output = "error",
                        ErrorMessage = "Service implementation error"
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🔴 Error in HandleConfirmStaffMatch");
                return new FunctionCallResult
                {
                    Success = false,
                    Output = "error",
                    ErrorMessage = ex.Message
                };
            }
        }

        private async Task<FunctionCallResult> HandleSendMessage(string arguments, string callerId)
        {
            _logger.LogInformation($"📧 send_message called with args: {arguments}");

            if (!ValidateCallerIdentification(callerId, "send_message"))
            {
                return new FunctionCallResult
                {
                    Success = false,
                    Output = "caller_identification_required",
                    ErrorMessage = "Must call collect_caller_name first before sending messages"
                };
            }

            try
            {
                var parsed = JsonDocument.Parse(arguments);
                var name = parsed.RootElement.GetProperty("name").GetString();
                var message = parsed.RootElement.GetProperty("message").GetString();
                var department = parsed.RootElement.TryGetProperty("department", out var deptElement) ? 
                    deptElement.GetString() : null;

                var callerInfo = _callerInfoCache[callerId];
                var callerFullName = callerInfo.FullName;
                
                // NEW: Check if we have a corrected name from fuzzy matching
                var correctionKey = CreateCorrectionKey(callerId, name!);
                string actualName = name!;
                string? actualEmail = null;
                string? actualDepartment = department;
                
                if (_nameCorrections.TryGetValue(correctionKey, out var correction))
                {
                    actualName = correction.CorrectedName;
                    actualEmail = correction.Email;
                    actualDepartment = correction.Department ?? department;
                    
                    _logger.LogInformation($"🔄 Using corrected name: '{name}' → '{actualName}' in {actualDepartment}");
                    _logger.LogInformation($"📧 Using cached email from fuzzy match: {actualEmail}");
                }
                else
                {
                    _logger.LogInformation($"📧 No name correction found, using original: {name}");
                }
                
                _logger.LogInformation($"📧 Parsed: name={name} (actual={actualName}), message={message}, department={actualDepartment}, caller={callerFullName}");

                if (!message!.Contains(callerFullName))
                {
                    _logger.LogInformation($"📧 Adding caller identification to message: {callerFullName}");
                    message = $"Message from {callerFullName}: {message}";
                }

                // Use cached email if available, otherwise lookup
                string? email = actualEmail;
                if (string.IsNullOrWhiteSpace(email))
                {
                    _logger.LogInformation($"📧 No cached email, performing lookup for: {actualName}");
                    email = await _staffLookupService.GetStaffEmailAsync(actualName, actualDepartment);
                }

                if (!string.IsNullOrWhiteSpace(email))
                {
                    _logger.LogInformation($"✅ Sending email to: {actualName}, email: {email}, from caller: {callerFullName}");
                    
                    var emailSuccess = await _emailService.SendMessageEmailAsync(actualName, email, message!, callerId);
                    
                    if (emailSuccess)
                    {
                        var deptInfo = !string.IsNullOrWhiteSpace(actualDepartment) ? $" in {actualDepartment}" : "";
                        _logger.LogInformation($"✅ Email sent successfully to {actualName}{deptInfo} from {callerFullName}");
                        
                        // Clean up the correction after successful send
                        _nameCorrections.Remove(correctionKey);
                        
                        return new FunctionCallResult
                        {
                            Success = true,
                            Output = "success"
                        };
                    }
                    else
                    {
                        _logger.LogWarning($"❌ Failed to send email to {actualName} from {callerFullName}");
                        return new FunctionCallResult
                        {
                            Success = false,
                            Output = "failed - email sending error"
                        };
                    }
                }
                else
                {
                    _logger.LogWarning($"❌ No valid email found for: {actualName} (original: {name}, department: {actualDepartment})");
                    return new FunctionCallResult
                    {
                        Success = false,
                        Output = "failed - staff not found or invalid email"
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🔴 Error in HandleSendMessage");
                return new FunctionCallResult
                {
                    Success = false,
                    Output = "error",
                    ErrorMessage = ex.Message
                };
            }
        }

        private FunctionCallResult HandleEndCall(string callerId)
        {
            _logger.LogInformation("🔚 end_call function triggered");
            CleanupCallerInfo(callerId);

            return new FunctionCallResult
            {
                Success = true,
                Output = "call_ended_successfully",
                ShouldEndCall = true
            };
        }

        // NEW: Helper method to create correction key
        private string CreateCorrectionKey(string callerId, string name)
        {
            return $"{callerId}|{name.ToLowerInvariant()}";
        }

        public void CleanupCallerInfo(string callerId)
        {
            if (_callerInfoCache.Remove(callerId))
            {
                _logger.LogInformation($"🧹 Cleaned up caller info for: {callerId}");
            }
            
            // NEW: Clean up all name corrections for this caller
            var correctionsToRemove = _nameCorrections
                .Where(kvp => kvp.Key.StartsWith(callerId + "|"))
                .Select(kvp => kvp.Key)
                .ToList();
                
            foreach (var key in correctionsToRemove)
            {
                _nameCorrections.Remove(key);
            }
            
            if (correctionsToRemove.Any())
            {
                _logger.LogInformation($"🧹 Cleaned up {correctionsToRemove.Count} name correction(s) for: {callerId}");
            }
        }

        public CallerInfo? GetCallerInfo(string callerId)
        {
            return _callerInfoCache.TryGetValue(callerId, out var info) ? info : null;
        }
    }

    // NEW: Class to store name corrections from fuzzy matching
    public class NameCorrection
    {
        public string OriginalName { get; set; } = string.Empty;
        public string CorrectedName { get; set; } = string.Empty;
        public string? Department { get; set; }
        public string? Email { get; set; }
        public DateTime CorrectedAt { get; set; }
    }

    public class CallerInfo
    {
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public DateTime CollectedAt { get; set; }
        
        public string FullName => $"{FirstName} {LastName}";
        public bool IsComplete => !string.IsNullOrWhiteSpace(FirstName) && !string.IsNullOrWhiteSpace(LastName);
    }
}
