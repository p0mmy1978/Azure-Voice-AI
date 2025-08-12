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
                    "check_staff_exists" => await HandleCheckStaffExists(arguments, callerId),
                    "send_message" => await HandleSendMessage(arguments, callerId),
                    "end_call" => HandleEndCall(),
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

        private async Task<FunctionCallResult> HandleCheckStaffExists(string arguments, string callerId)
        {
            _logger.LogInformation($"🔍 check_staff_exists called with args: {arguments}");

            try
            {
                var parsed = JsonDocument.Parse(arguments);
                var name = parsed.RootElement.GetProperty("name").GetString();
                var department = parsed.RootElement.TryGetProperty("department", out var deptElement) ? 
                    deptElement.GetString() : null;

                _logger.LogInformation($"🔍 Checking staff: name={name}, department={department}");

                var result = await _staffLookupService.CheckStaffExistsAsync(name!, department);

                string output = result.Status switch
                {
                    StaffLookupStatus.Authorized => "authorized",
                    StaffLookupStatus.NotAuthorized => "not_authorized",
                    StaffLookupStatus.MultipleFound => "multiple_found",
                    StaffLookupStatus.NotFound => "not_authorized",
                    _ => "not_authorized"
                };

                _logger.LogInformation($"🔍 Staff check result: {output}");

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

        private async Task<FunctionCallResult> HandleSendMessage(string arguments, string callerId)
        {
            _logger.LogInformation($"📧 send_message called with args: {arguments}");

            try
            {
                var parsed = JsonDocument.Parse(arguments);
                var name = parsed.RootElement.GetProperty("name").GetString();
                var message = parsed.RootElement.GetProperty("message").GetString();
                var department = parsed.RootElement.TryGetProperty("department", out var deptElement) ? 
                    deptElement.GetString() : null;

                _logger.LogInformation($"📧 Parsed: name={name}, message={message}, department={department}");

                // Get staff email using the lookup service
                var email = await _staffLookupService.GetStaffEmailAsync(name!, department);

                if (!string.IsNullOrWhiteSpace(email))
                {
                    _logger.LogInformation($"✅ Sending email to: {name}, email: {email}");
                    
                    // Send the email
                    var emailSuccess = await _emailService.SendMessageEmailAsync(name!, email, message!, callerId);
                    
                    if (emailSuccess)
                    {
                        _logger.LogInformation($"✅ Email sent successfully to {name}");
                        return new FunctionCallResult
                        {
                            Success = true,
                            Output = "success"
                        };
                    }
                    else
                    {
                        _logger.LogWarning($"❌ Failed to send email to {name}");
                        return new FunctionCallResult
                        {
                            Success = false,
                            Output = "failed - email sending error"
                        };
                    }
                }
                else
                {
                    _logger.LogWarning($"❌ No valid email found for: {name}");
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

        private FunctionCallResult HandleEndCall()
        {
            _logger.LogInformation("🔚 end_call function triggered");

            return new FunctionCallResult
            {
                Success = true,
                Output = "call_ended_successfully",
                ShouldEndCall = true
            };
        }
    }
}
