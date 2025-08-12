using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using Azure.Identity;
using CallAutomation.AzureAI.VoiceLive.Services.Interfaces;

namespace CallAutomation.AzureAI.VoiceLive.Services
{
    public class EmailService : IEmailService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<EmailService> _logger;
        private GraphServiceClient _graphClient = default!;
        private bool _isInitialized = false;

        public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public async Task InitializeAsync()
        {
            if (_isInitialized) return;

            try
            {
                var tenantId = _configuration["GraphTenantId"];
                var clientId = _configuration["GraphClientId"];
                var clientSecret = _configuration["GraphClientSecret"];

                if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
                {
                    throw new InvalidOperationException("Graph API configuration is missing. Check GraphTenantId, GraphClientId, and GraphClientSecret in appsettings.json");
                }

                var clientSecretCredential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                _graphClient = new GraphServiceClient(clientSecretCredential, new[] { "https://graph.microsoft.com/.default" });
                
                _isInitialized = true;
                _logger.LogInformation("📧 EmailService initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to initialize EmailService");
                throw;
            }
        }

        public async Task<bool> SendMessageEmailAsync(string recipientName, string recipientEmail, string message, string callerId)
        {
            try
            {
                if (!_isInitialized)
                {
                    await InitializeAsync();
                }

                _logger.LogInformation($"📬 Sending email to {recipientName} <{recipientEmail}> from caller: {callerId}");
                
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                var formattedCallerNumber = FormatCallerNumber(callerId);
                
                var emailBody = CreateEmailBody(timestamp, formattedCallerNumber, message);
                var emailSubject = "New after-hours message from POMS.Tech";

                var mail = new Message
                {
                    Subject = emailSubject,
                    Body = new ItemBody
                    {
                        ContentType = BodyType.Text,
                        Content = emailBody
                    },
                    ToRecipients = new List<Recipient>
                    {
                        new Recipient { EmailAddress = new EmailAddress { Address = recipientEmail } }
                    }
                };

                var requestBody = new SendMailPostRequestBody
                {
                    Message = mail,
                    SaveToSentItems = true
                };

                var senderUPN = _configuration["GraphSenderUPN"];
                if (string.IsNullOrWhiteSpace(senderUPN))
                {
                    throw new InvalidOperationException("GraphSenderUPN is not configured in appsettings.json");
                }

                _logger.LogInformation($"📤 Sending mail via Graph API to {recipientEmail}...");
                await _graphClient.Users[senderUPN].SendMail.PostAsync(requestBody);
                
                _logger.LogInformation($"✅ Email sent successfully to {recipientName} at {recipientEmail} with caller ID: {formattedCallerNumber}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Failed to send email to {recipientName} at {recipientEmail}");
                return false;
            }
        }

        private static string CreateEmailBody(string timestamp, string formattedCallerNumber, string message)
        {
            return $@"You have received a new after-hours message:

Time: {timestamp}
Caller Number: {formattedCallerNumber}
Message: {message}";
        }

        private string FormatCallerNumber(string rawCallerNumber)
        {
            if (string.IsNullOrWhiteSpace(rawCallerNumber))
            {
                return "Unknown";
            }

            try
            {
                _logger.LogDebug($"🔍 Raw caller number received: '{rawCallerNumber}'");
                
                // Remove any non-digit characters except + 
                var digitsOnly = new string(rawCallerNumber.Where(c => char.IsDigit(c)).ToArray());
                
                _logger.LogDebug($"🔍 Digits extracted: '{digitsOnly}'");
                
                // If we have digits, format as E.164
                if (!string.IsNullOrEmpty(digitsOnly))
                {
                    // Look for Australian number pattern (starting with 61)
                    if (digitsOnly.StartsWith("461") && digitsOnly.Length >= 12)
                    {
                        // Remove the leading "4" prefix and format the Australian number
                        var cleanNumber = digitsOnly.Substring(1); // Remove the "4"
                        _logger.LogDebug($"🔍 Removed ACS prefix '4', clean number: '{cleanNumber}'");
                        return $"+{cleanNumber}";
                    }
                    else if (digitsOnly.StartsWith("61") && digitsOnly.Length >= 11)
                    {
                        // Already a proper Australian number
                        return $"+{digitsOnly}";
                    }
                    else if (digitsOnly.Length >= 10)
                    {
                        // Generic international number
                        return $"+{digitsOnly}";
                    }
                }
                
                // Fallback: return original if we can't parse it
                _logger.LogWarning($"Could not format caller number: '{rawCallerNumber}'");
                return rawCallerNumber;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to format caller number: {rawCallerNumber}");
                return rawCallerNumber;
            }
        }
    }
}
