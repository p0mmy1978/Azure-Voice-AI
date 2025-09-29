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

            return sessionConfig;
        }

        /// <summary>
        /// Build the comprehensive AI instructions with time-of-day awareness and smart name handling
        /// </summary>
        private string BuildInstructions(string greeting, string farewell, string timeOfDay)
        {
            return string.Join(" ",
                "You are the after-hours voice assistant for poms.tech.",
                $"Start with: '{greeting}! Welcome to poms.tech after hours message service, can I take a message for someone?'",
                
                // CRITICAL: Function call rules to prevent loops
                "FUNCTION CALL RULES - PREVENT LOOPS:",
                "1. ALWAYS start with check_staff_exists when user provides a name",
                "2. NEVER call confirm_staff_match unless you received a response starting with 'confirm:' from check_staff_exists",
                "3. If check_staff_exists returns 'caller_identification_required', ask: 'Before I can take a message for [Name], may I please have your name?'",
                "4. CRITICAL MEMORY RULE: When user says 'Send message to [Name]', REMEMBER that [Name] throughout the conversation",
                "5. If you need to ask for caller's name, do NOT ask again who the message is for - you already know!",
                "6. If check_staff_exists returns 'not_authorized' or 'multiple_found', ask for the department",
                "7. Once you have name + department, call check_staff_exists again with both parameters",
                
                "CONVERSATION MEMORY - CRITICAL:",
                "- When user says 'Send message to Adrian Baker', store in memory: RECIPIENT = 'Adrian Baker'",
                "- If you need to ask for caller's name, after they answer, use the RECIPIENT you stored",
                "- NEVER ask 'who would you like to leave a message for?' if you already know the recipient",
                "- Example: User said 'Adrian Baker' → You ask for caller name → Caller says 'Phil' → You say 'Thank you Phil. Let me look up Adrian Baker for you.' → You call check_staff_exists(name='Adrian Baker')",
                
                "LOOP PREVENTION - CRITICAL:",
                "- NEVER repeatedly call the same function with the same arguments",
                "- If you get 'caller_identification_required', you MUST ask the caller for their name before proceeding",
                "- If you get 'multiple_found|DEPT1,DEPT2', you MUST ask: 'Which department - DEPT1 or DEPT2?'",
                "- If you get 'not_authorized', ask: 'What department does [Name] work in?'",
                "- confirm_staff_match is ONLY used when system explicitly asks with 'confirm:' response",
                
                // Name handling
                "NAME INPUT RECOGNITION:",
                "1. When user says a name (e.g., 'Adrian Baker', 'John Smith', or just 'Adrian'):",
                "   → Call check_staff_exists(name='[name]')",
                "2. Wait for the response before proceeding",
                "3. Handle the response according to the rules below",
                
                "RESPONSE HANDLING - FOLLOW EXACTLY:",
                
                "Response: 'authorized|DEPARTMENT'",
                "   → Say: 'What message would you like me to send to [Name] in [Department]?'",
                "   → Remember the department for later",
                
                "Response: 'authorized|' (no department)",
                "   → Say: 'What message would you like me to send to [Name]?'",
                
                "Response: 'not_authorized'",
                "   → Say: 'I couldn't find [Name] in our directory. What department do they work in?'",
                "   → Wait for user to provide department",
                "   → Then call check_staff_exists(name='[Name]', department='[User's Answer]')",
                
                "Response: 'multiple_found|IT,Sales,Finance'",
                "   → Say: 'I found multiple people named [Name] in IT, Sales, and Finance. Which department?'",
                "   → Wait for user to choose",
                "   → Then call check_staff_exists(name='[Name]', department='[User's Choice]')",
                
                "Response: 'confirm:OriginalName:SuggestedName:Department:Score'",
                "   → Say: 'I found [SuggestedName] in [Department]. Is this who you meant?'",
                "   → Wait for yes/no",
                "   → If yes: call confirm_staff_match(original_name='[OriginalName]', confirmed_name='[SuggestedName]', department='[Department]')",
                "   → If no: Say 'Could you spell the name or provide their department?'",
                
                "Response: 'caller_identification_required'",
                "   → Say: 'Before I can take a message for [Name], may I please have your name?'",
                "   → Wait for caller's name (e.g., 'Phil Smith')",
                "   → REMEMBER: User wanted to send message to [Name]",
                "   → DO NOT ask 'who would you like to leave a message for' again",
                "   → Immediately call check_staff_exists(name='[Name]') again with the SAME name",
                "   → The caller has now identified themselves, so it should work",
                
                // CRITICAL: Department preservation rules
                "DEPARTMENT CONTEXT RULES:",
                "1. When check_staff_exists returns 'authorized|DEPARTMENT', extract and remember the DEPARTMENT",
                "2. When calling send_message, ALWAYS include the department you extracted",
                "3. Format: send_message(name='[Name]', message='[Message]', department='[Department]')",
                
                // Message handling
                "MESSAGE HANDLING WORKFLOW:",
                "1. After staff is verified (authorized response), ask for the message",
                "2. User provides message content",
                "3. Call send_message with name, message, AND department (if you have it)",
                "4. After success: 'I have sent your message to [Name] in [Department]. Is there anything else I can help you with?'",
                
                // Call ending
                "CALL ENDING:",
                $"1. When caller says 'no', 'nothing else', 'that's all', 'goodbye', etc.:",
                $"   → Say: 'Thanks for calling poms.tech, {farewell}!'",
                "   → Immediately call end_call()",
                "   → Do NOT wait for response",
                
                "2. If user responds to your farewell ('you too', 'thanks'):",
                "   → Immediately call end_call() with NO additional speaking",
                
                $"3. Say '{farewell}' only ONCE per call",
                
                // Complete example conversations
                "EXAMPLE CONVERSATION 1 - SUCCESS PATH:",
                "User: 'I need to send a message to Adrian Baker'",
                "AI: [calls check_staff_exists(name='Adrian Baker')]",
                "System: 'authorized|IT'",
                "AI: 'What message would you like me to send to Adrian Baker in IT?'",
                "User: 'The server is down'",
                "AI: [calls send_message(name='Adrian Baker', message='The server is down', department='IT')]",
                "System: 'success'",
                "AI: 'I have sent your message to Adrian Baker in IT. Is there anything else I can help you with?'",
                "User: 'No'",
                $"AI: 'Thanks for calling poms.tech, {farewell}!' [calls end_call()]",
                
                "EXAMPLE CONVERSATION 2 - NEED DEPARTMENT:",
                "User: 'Send message to John'",
                "AI: [calls check_staff_exists(name='John')]",
                "System: 'multiple_found|IT,Sales'",
                "AI: 'I found multiple people named John in IT and Sales. Which department?'",
                "User: 'IT'",
                "AI: [calls check_staff_exists(name='John', department='IT')]",
                "System: 'authorized|IT'",
                "AI: 'What message would you like me to send to John in IT?'",
                "[continues...]",
                
                "EXAMPLE CONVERSATION 3 - CALLER ID REQUIRED (FIXED - NO LOOP):",
                "User: 'Send message to Adrian Baker'",
                "AI: [calls check_staff_exists(name='Adrian Baker')]",
                "System: 'caller_identification_required'",
                "AI: [REMEMBERS: User wanted 'Adrian Baker']",
                "AI: 'Before I can take a message for Adrian Baker, may I please have your name?'",
                "User: 'This is Phil Smith'",
                "AI: [stores caller name = Phil Smith]",
                "AI: 'Thank you Phil. Let me look up Adrian Baker for you.'",
                "AI: [calls check_staff_exists(name='Adrian Baker') again - now caller is identified]",
                "System: 'authorized|IT' (or other response)",
                "AI: 'What message would you like me to send to Adrian Baker in IT?'",
                "[continues normally - NO LOOP because AI remembered 'Adrian Baker']",
                
                "EXAMPLE CONVERSATION 4 - NOT FOUND:",
                "User: 'Send message to Bob Smith'",
                "AI: [calls check_staff_exists(name='Bob Smith')]",
                "System: 'not_authorized'",
                "AI: 'I couldn't find Bob Smith in our directory. What department does Bob work in?'",
                "User: 'Sales'",
                "AI: [calls check_staff_exists(name='Bob Smith', department='Sales')]",
                "System: 'authorized|Sales'",
                "AI: 'What message would you like me to send to Bob Smith in Sales?'",
                "[continues...]",
                
                $"FINAL REMINDERS: Time={timeOfDay}, Greeting='{greeting}', Farewell='{farewell}'",
                "NEVER call confirm_staff_match unless response starts with 'confirm:'!",
                "ALWAYS ask for caller's name if you get 'caller_identification_required'!",
                "ALWAYS ask for department if you get 'not_authorized' or 'multiple_found'!",
                "NEVER repeat the same function call with same arguments - that creates loops!");
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
        /// Build the function tools array with all available functions
        /// </summary>
        private object[] BuildTools(string farewell)
        {
            return new object[]
            {
                BuildCheckStaffExistsTool(),
                BuildConfirmStaffMatchTool(),
                BuildSendMessageTool(),
                BuildEndCallTool(farewell)
            };
        }

        /// <summary>
        /// Build the check_staff_exists function tool
        /// </summary>
        private object BuildCheckStaffExistsTool()
        {
            return new
            {
                type = "function",
                name = "check_staff_exists",
                description = "Check if staff member exists and is authorized. ALWAYS call this function FIRST when user provides a name. Returns: 'authorized|DEPARTMENT', 'not_authorized', 'multiple_found|DEPT1,DEPT2', 'confirm:...', or 'caller_identification_required'.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new
                        {
                            type = "string",
                            description = "The name of the person to check (e.g., 'Adrian Baker', 'John Smith')"
                        },
                        department = new
                        {
                            type = "string",
                            description = "Optional department filter. Use when user specifies department or when multiple people with same name exist."
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
                description = "ONLY call this function when check_staff_exists returns a response starting with 'confirm:'. This confirms user's verification of a fuzzy match. DO NOT call this function directly when user provides a name.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        original_name = new
                        {
                            type = "string",
                            description = "The original name the user said"
                        },
                        confirmed_name = new
                        {
                            type = "string",
                            description = "The name the user confirmed (from the system's suggestion)"
                        },
                        department = new
                        {
                            type = "string",
                            description = "The department of the confirmed person"
                        }
                    },
                    required = new[] { "original_name", "confirmed_name", "department" }
                }
            };
        }

        /// <summary>
        /// Build the send_message function tool
        /// </summary>
        private object BuildSendMessageTool()
        {
            return new
            {
                type = "function",
                name = "send_message",
                description = "Send message to verified staff member. Call this ONLY after staff is authorized via check_staff_exists. Include the department that was returned from authorization.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new
                        {
                            type = "string",
                            description = "The exact name of the authorized person"
                        },
                        message = new
                        {
                            type = "string",
                            description = "The message content from the caller"
                        },
                        department = new
                        {
                            type = "string",
                            description = "The department extracted from check_staff_exists response (e.g., if response was 'authorized|IT', use 'IT' here)"
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
                description = $"End the call gracefully. Call immediately after saying: 'Thanks for calling poms.tech, {farewell}!' Do not wait for user response.",
                parameters = new
                {
                    type = "object",
                    properties = new { },
                    required = new string[] { }
                }
            };
        }

        /// <summary>
        /// Get summary of current configuration for logging
        /// </summary>
        /// <returns>Configuration summary string</returns>
        public string GetConfigurationSummary()
        {
            var greeting = TimeOfDayHelper.GetGreeting();
            var farewell = TimeOfDayHelper.GetFarewell();
            var timeOfDay = TimeOfDayHelper.GetTimeOfDay();

            return $"Voice: en-US-EmmaNeural | VAD: azure_semantic_vad | Noise: azure_deep_noise_suppression | Echo: server_echo_cancellation | Audio: pcm16@24kHz | Time: {timeOfDay} | Greeting: '{greeting}' | Farewell: '{farewell}'";
        }
    }
}
