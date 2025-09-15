using CallAutomation.AzureAI.VoiceLive.Helpers;
using Microsoft.Extensions.Logging;

namespace CallAutomation.AzureAI.VoiceLive.Services.Voice
{
    /// <summary>
    /// Builds complex session configuration objects for Azure Voice Live
    /// </summary>
    public class SessionConfigBuilder
    {
        private readonly ILogger<SessionConfigBuilder> _logger;

        public SessionConfigBuilder(ILogger<SessionConfigBuilder> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Build a complete session update object for Azure Voice Live
        /// </summary>
        /// <param name="voiceTemperature">Voice temperature setting</param>
        /// <returns>Session update object ready for JSON serialization</returns>
        public object BuildSessionUpdateObject(double voiceTemperature = 0.8)
        {
            // Get time-based greetings
            var greeting = TimeOfDayHelper.GetGreeting();
            var farewell = TimeOfDayHelper.GetFarewell();
            var timeOfDay = TimeOfDayHelper.GetTimeOfDay();

            _logger.LogInformation($"Building session with: greeting='{greeting}', farewell='{farewell}', timeOfDay='{timeOfDay}'");

            var sessionConfig = new
            {
                type = "session.update",
                session = new
                {
                    instructions = BuildInstructions(greeting, farewell, timeOfDay),
                    turn_detection = BuildTurnDetection(),
                    input_audio_noise_reduction = BuildNoiseReduction(),
                    input_audio_echo_cancellation = BuildEchoCancellation(),
                    voice = BuildVoiceConfig(voiceTemperature),
                    input_audio_format = "pcm16",
                    output_audio_format = "pcm16",
                    input_audio_sampling_rate = 24000,
                    tools = BuildTools(farewell)
                }
            };

            // Enhanced logging for audio enhancement verification
            _logger.LogInformation("Audio Enhancement Settings:");
            _logger.LogInformation("   Noise Reduction: azure_deep_noise_suppression");
            _logger.LogInformation("   Echo Cancellation: server_echo_cancellation");
            _logger.LogInformation("   Voice Activity Detection: azure_semantic_vad");
            _logger.LogInformation("   Audio Format: pcm16 @ 24kHz");
            _logger.LogInformation("🔒 STRICT POLICY: First & Last Name MANDATORY for BOTH Caller AND Recipient");
            _logger.LogInformation("📞 LIMIT: Maximum 2 concurrent calls");
            _logger.LogInformation("⏰ TIMEOUT: 90-second session limit for bill shock prevention");

            return sessionConfig;
        }

        /// <summary>
        /// Build the comprehensive AI instructions with ABSOLUTE name collection enforcement for both caller and recipient
        /// </summary>
        private string BuildInstructions(string greeting, string farewell, string timeOfDay)
        {
            return 
                "You are the after-hours voice assistant for poms.tech. " +
                $"Start with: '{greeting}! Welcome to poms.tech after hours message service, can I take a message for someone?' " +
                
                // ABSOLUTE SECURITY ENFORCEMENT - ENHANCED FOR RECIPIENT NAMES
                "🚨 ABSOLUTE SECURITY PROTOCOL - ENHANCED FOR BOTH CALLER AND RECIPIENT: " +
                "1. When user wants to send a message, FIRST collect caller's FULL NAME (first and last) " +
                "2. THEN collect recipient's FULL NAME (first and last) before doing ANY staff lookup " +
                "3. NEVER do staff lookup with only a first name - ALWAYS require both first and last name of recipient " +
                "4. If user only gives first name for recipient (like 'Adrian'), ask: 'What is Adrian's last name?' " +
                "5. Only call check_staff_exists AFTER you have BOTH first and last name of the recipient " +
                
                "ENHANCED MANDATORY WORKFLOW - FOLLOW EXACTLY: " +
                "Step 1: User wants to send message to someone " +
                "Step 2: Ask: 'I'd be happy to help. First, I need your full name for our records. What is your first and last name please?' " +
                "Step 3: User provides caller names → Call collect_caller_name(first_name='...', last_name='...') " +
                "Step 4: Wait for success: 'caller_identified|...' " +
                "Step 5: Say: 'Thank you [CallerName]. Who would you like to send a message to? Please provide their first and last name.' " +
                "Step 6: If user gives only first name (e.g., 'Adrian'), ask: 'What is Adrian's last name?' " +
                "Step 7: Once you have BOTH first and last name of recipient, call check_staff_exists(name='FirstName LastName') " +
                "Step 8: Continue with normal flow " +
                
                "RECIPIENT NAME COLLECTION EXAMPLES: " +
                "Example 1 - Incomplete name given: " +
                "User: 'Send a message to Adrian' " +
                "AI: 'What is Adrian's last name?' " +
                "User: 'Baker' " +
                "AI: [calls check_staff_exists(name='Adrian Baker')] " +
                "" +
                "Example 2 - Full name given: " +
                "User: 'Send a message to Adrian Baker' " +
                "AI: [calls check_staff_exists(name='Adrian Baker')] " +
                "" +
                "Example 3 - Partial name then completion: " +
                "User: 'I need to contact Terry' " +
                "AI: 'What is Terry's last name?' " +
                "User: 'Smith' " +
                "AI: [calls check_staff_exists(name='Terry Smith')] " +
                
                "STRICT RULES FOR RECIPIENT NAMES: " +
                "- NEVER accept just 'Adrian' - always ask for last name " +
                "- NEVER accept just 'Terry' - always ask for last name " +
                "- NEVER call check_staff_exists with single names " +
                "- Always ask: 'What is [FirstName]'s last name?' if only given first name " +
                "- Only proceed when you have full recipient name like 'Adrian Baker' or 'Terry Smith' " +
                
                "FUNCTION CALL SEQUENCE - ENHANCED MANDATORY ORDER: " +
                "1. collect_caller_name (FIRST - caller's first and last name) " +
                "2. Collect recipient's full name through conversation (no function call needed) " +
                "3. check_staff_exists (ONLY after you have recipient's first AND last name) " +
                "4. confirm_staff_match (if needed for fuzzy matches) " +
                "5. send_message (with both caller and recipient full names) " +
                "6. end_call (when conversation complete) " +
                
                "DEPARTMENT PARSING AFTER FULL NAME CONFIRMATION: " +
                "1. Parse check_staff_exists response: 'authorized|DEPARTMENT' " +
                "2. Extract department after '|' symbol " +
                "3. Ask for message: 'What message would you like me to send to [FirstName LastName] in [Department]?' " +
                "4. Include both caller and recipient full names in communications " +
                
                "ERROR HANDLING FOR NAMES: " +
                "- If user gives incomplete recipient name, ask for missing parts " +
                "- If check_staff_exists returns 'not_found' with full name, suggest checking spelling " +
                "- If multiple matches found, ask for department clarification " +
                "- Always ensure both caller and recipient full names before staff lookup " +
                
                "COMPLETE EXAMPLE CONVERSATION WITH FULL NAME COLLECTION: " +
                $"AI: '{greeting}! Welcome to poms.tech after hours message service, can I take a message for someone?' " +
                "User: 'Yes, I want to send a message to Adrian' " +
                "AI: 'I'd be happy to help. First, I need your full name for our records. What is your first and last name please?' " +
                "User: 'Jack Jones' " +
                "AI: [collect_caller_name(first_name='Jack', last_name='Jones')] → 'caller_identified|Jack Jones' " +
                "AI: 'Thank you Jack Jones. What is Adrian's last name?' " +
                "User: 'Baker' " +
                "AI: [check_staff_exists(name='Adrian Baker')] → 'authorized|IT' " +
                "AI: 'I found Adrian Baker in the IT department. What message would you like me to send to Adrian Baker?' " +
                "User: 'The server is down' " +
                "AI: [send_message(name='Adrian Baker', message='Message from Jack Jones: The server is down', department='IT')] " +
                
                // Call ending
                "CALL ENDING: " +
                $"1. When done, say: 'Thanks for calling poms.tech, {farewell}!' then call end_call " +
                "2. Be efficient due to 90-second session limit " +
                "3. Always collect full names efficiently but thoroughly " +
                
                // SESSION MANAGEMENT
                "SESSION EFFICIENCY WITH FULL NAME REQUIREMENTS: " +
                "- 90-second session limit requires focused interactions " +
                "- Collect caller's full name quickly " +
                "- Collect recipient's full name before lookup " +
                "- Process one message at a time " +
                "- Avoid lengthy explanations " +
                
                $"MEMORY: Time={timeOfDay}, greeting='{greeting}', farewell='{farewell}' " +
                "🚨 SECURITY: ALWAYS collect FULL NAMES for BOTH caller AND recipient! " +
                "🔒 RECIPIENT RULE: NEVER do staff lookup without recipient's first AND last name! " +
                "⏰ EFFICIENCY: 90-second limit - collect names quickly but completely! " +
                "📝 ACCOUNTABILITY: Every message includes full caller and recipient identification!";
        }

        /// <summary>
        /// Build turn detection configuration for voice activity detection
        /// </summary>
        private object BuildTurnDetection()
        {
            return new
            {
                type = "azure_semantic_vad",
                threshold = 0.4,
                prefix_padding_ms = 150,
                silence_duration_ms = 150,
                remove_filler_words = true,
                min_speech_duration_ms = 100,
                max_silence_for_turn_ms = 800
            };
        }

        /// <summary>
        /// Build noise reduction configuration
        /// </summary>
        private object BuildNoiseReduction()
        {
            return new { type = "azure_deep_noise_suppression" };
        }

        /// <summary>
        /// Build echo cancellation configuration
        /// </summary>
        private object BuildEchoCancellation()
        {
            return new { type = "server_echo_cancellation" };
        }

        /// <summary>
        /// Build voice configuration
        /// </summary>
        private object BuildVoiceConfig(double temperature)
        {
            return new
            {
                name = "en-US-EmmaNeural",
                type = "azure-standard", 
                temperature = temperature
            };
        }

        /// <summary>
        /// Build the function tools array with MANDATORY name collection first
        /// </summary>
        private object[] BuildTools(string farewell)
        {
            return new object[]
            {
                BuildCollectCallerNameTool(), // NEW: MUST be called first
                BuildCheckStaffExistsTool(),  // ENHANCED: Requires recipient full name
                BuildConfirmStaffMatchTool(),
                BuildSendMessageTool(),
                BuildEndCallTool(farewell)
            };
        }

        /// <summary>
        /// NEW: Build the collect_caller_name function tool - MUST be called first
        /// </summary>
        private object BuildCollectCallerNameTool()
        {
            return new
            {
                type = "function",
                name = "collect_caller_name",
                description = "🚨 MANDATORY FIRST FUNCTION: Collect and validate caller's first and last name for security and accountability. This MUST be the first function called when user wants to send a message. Call immediately after getting both names from user.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        first_name = new
                        {
                            type = "string",
                            description = "The caller's first name (required)"
                        },
                        last_name = new
                        {
                            type = "string",
                            description = "The caller's last name (required)"
                        }
                    },
                    required = new[] { "first_name", "last_name" }
                }
            };
        }

        /// <summary>
        /// Build the check_staff_exists function tool with ENHANCED requirements for recipient full names
        /// </summary>
        private object BuildCheckStaffExistsTool()
        {
            return new
            {
                type = "function",
                name = "check_staff_exists",
                description = "🚨 ENHANCED SECURITY: Check if staff member exists using FULL NAME (first and last). ⚠️ ABSOLUTE REQUIREMENTS: 1) Caller's full name must already be collected, 2) You must provide recipient's FIRST AND LAST name in the 'name' parameter (e.g., 'Adrian Baker', not just 'Adrian'). NEVER call this function with only a first name. Always ask for the recipient's last name if not provided.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new
                        {
                            type = "string",
                            description = "The FULL NAME (first and last) of the message recipient to check. Examples: 'Adrian Baker', 'Terry Smith', 'John Johnson'. NEVER use single names like 'Adrian' or 'Terry' - always include both first and last name."
                        },
                        department = new
                        {
                            type = "string",
                            description = "Optional department filter for the message recipient."
                        }
                    },
                    required = new[] { "name" }
                }
            };
        }

        /// <summary>
        /// Build the confirm_staff_match function tool
        /// </summary>
        private object BuildConfirmStaffMatchTool()
        {
            return new
            {
                type = "function",
                name = "confirm_staff_match",
                description = "🚨 SECURITY CRITICAL: Confirm a fuzzy match suggestion. ⚠️ REQUIREMENT: Both caller's and recipient's full names must already be collected.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        original_name = new
                        {
                            type = "string",
                            description = "The original recipient full name the user said"
                        },
                        confirmed_name = new
                        {
                            type = "string",
                            description = "The recipient full name the user confirmed"
                        },
                        department = new
                        {
                            type = "string",
                            description = "The department of the confirmed recipient"
                        }
                    },
                    required = new[] { "original_name", "confirmed_name", "department" }
                }
            };
        }

        /// <summary>
        /// Build the send_message function tool with full name enforcement
        /// </summary>
        private object BuildSendMessageTool()
        {
            return new
            {
                type = "function",
                name = "send_message",
                description = "🚨 SECURITY CRITICAL: Send message with MANDATORY full identification for both caller and recipient. ⚠️ REQUIREMENTS: Both caller's and recipient's full names must be collected. Format: 'Message from [CallerFirstName CallerLastName] for [RecipientFirstName RecipientLastName]: [message]'.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new
                        {
                            type = "string",
                            description = "The full name of the authorized message recipient (first and last name)"
                        },
                        message = new
                        {
                            type = "string",
                            description = "The complete message INCLUDING caller identification. Format: 'Message from [CallerFirstName CallerLastName]: [actual message content]'. The caller's full name MUST be included."
                        },
                        department = new
                        {
                            type = "string",
                            description = "The department extracted from check_staff_exists response (after '|' symbol)"
                        }
                    },
                    required = new[] { "name", "message" }
                }
            };
        }

        /// <summary>
        /// Build the end_call function tool
        /// </summary>
        private object BuildEndCallTool(string farewell)
        {
            return new
            {
                type = "function",
                name = "end_call",
                description = $"End the call gracefully. Call immediately after saying: 'Thanks for calling poms.tech, {farewell}!' or if user responds to farewell. Also call if approaching 90-second timeout.",
                parameters = new
                {
                    type = "object",
                    properties = new { },
                    required = new string[] { }
                }
            };
        }

        /// <summary>
        /// Get summary of current configuration for logging (enhanced with full name policies)
        /// </summary>
        /// <returns>Configuration summary string</returns>
        public string GetConfigurationSummary()
        {
            var greeting = TimeOfDayHelper.GetGreeting();
            var farewell = TimeOfDayHelper.GetFarewell();
            var timeOfDay = TimeOfDayHelper.GetTimeOfDay();

            return $"Voice: en-US-EmmaNeural | VAD: azure_semantic_vad | 🚨 ENHANCED SECURITY: Caller+Recipient Full Names MANDATORY | 📞 LIMIT: 2 calls max | ⏰ TIMEOUT: 90s max | Time: {timeOfDay} | Greeting: '{greeting}' | Farewell: '{farewell}'";
        }
    }
}
