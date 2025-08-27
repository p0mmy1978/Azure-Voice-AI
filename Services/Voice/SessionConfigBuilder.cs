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

            return new
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
        }

        /// <summary>
        /// Build the comprehensive AI instructions with time-of-day awareness
        /// </summary>
        private string BuildInstructions(string greeting, string farewell, string timeOfDay)
        {
            return string.Join(" ",
                "You are the after-hours voice assistant for poms.tech.",
                $"Start with: '{greeting}! Welcome to poms.tech after hours message service, can I take a message for someone?'",
                
                // CRITICAL: Enhanced department preservation rules with parsing instructions
                "DEPARTMENT CONTEXT RULES - CRITICAL FOR CORRECT MESSAGING:",
                "1. When check_staff_exists returns 'authorized|DEPARTMENT', parse it as: status='authorized', department='DEPARTMENT'",
                "2. When check_staff_exists returns 'authorized|' (with empty department), parse as: status='authorized', department=''",
                "3. When check_staff_exists returns just 'authorized', treat as: status='authorized', department='' (legacy format)",
                "4. ALWAYS extract and remember the department part after the '|' symbol",
                "5. The department you extract MUST be passed to send_message function",
                
                "PARSING EXAMPLES:",
                "- 'authorized|IT' → Person works in IT department, use department='IT' in send_message",
                "- 'authorized|Sales' → Person works in Sales department, use department='Sales' in send_message",
                "- 'authorized|' → Person authorized but no specific department, use department='' in send_message",
                "- 'multiple_found|IT,Sales,Finance' → Multiple people found in different departments",
                
                "DETAILED WORKFLOW WITH PARSING:",
                "Step 1: User asks to send message to [Name]",
                "Step 2: Call check_staff_exists with name (and department if user provided it)",
                "Step 3: Parse the response:",
                "  - If starts with 'authorized|', extract department after '|'",
                "  - If 'multiple_found|', list available departments and ask user to choose",
                "  - If 'not_authorized', inform user and ask them to spell name or provide department",
                "  - If starts with 'confirm:', handle confirmation flow",
                "Step 4: Store the parsed department in your conversation context",
                "Step 5: Ask for message: 'What message would you like me to send to [Name] in [Department]?' (or just [Name] if no department)",
                "Step 6: When user provides message, call send_message with name, message, AND the department you stored",
                
                "CONVERSATION CONTEXT MEMORY:",
                "Once you parse 'authorized|DEPARTMENT' for a person, remember that [Name] works in [Department] for the rest of the conversation.",
                "When the user provides the message content, you MUST call send_message(name=Name, message=Message, department=StoredDepartment)",
                "Never call send_message without the department if you received one during authorization.",
                
                // Enhanced name handling with confirmation flow
                "NAME HANDLING WITH CONFIRMATION:",
                "1. Always use check_staff_exists function first when user provides a name",
                "2. If response is 'authorized|DEPT', proceed with that person and department",
                "3. If response is 'not_authorized', ask for correct spelling or department",
                "4. If response starts with 'confirm:', parse it and ask user for confirmation",
                "5. Example confirmation flow:",
                "   User: 'Send message to Adrian'",
                "   System: check_staff_exists(name='Adrian')",
                "   Response: 'confirm:Adrian:Adrian Baker:IT:0.85'",
                "   AI: 'I found Adrian Baker in IT department. Is this who you meant?'",
                "   User: 'Yes'", 
                "   AI: confirm_staff_match(original_name='Adrian', confirmed_name='Adrian Baker', department='IT')",
                
                // Message handling with department context
                "MESSAGE HANDLING - DEPARTMENT PRESERVATION:",
                "1. After successful staff verification, ask: 'What message would you like me to send to [Name] in [Department]?'",
                "2. When user provides message, call send_message with ALL three parameters:",
                "   send_message(name='[Name]', message='[User Message]', department='[Stored Department]')",
                "3. After successful send: 'I have sent your message to [Name] in [Department]. Is there anything else I can help you with?'",
                "4. If department was empty, just say: 'I have sent your message to [Name]. Is there anything else I can help you with?'",
                
                // ENHANCED: Call ending with loop prevention
                "CALL ENDING - CRITICAL FAREWELL INSTRUCTIONS:",
                $"1. When caller indicates they're done ('no', 'nothing else', 'that's all', 'goodbye', etc.), say: 'Thanks for calling poms.tech, {farewell}!' then IMMEDIATELY call end_call function",
                $"2. MANDATORY farewell phrase: '{farewell}' - use this EXACTLY",
                $"3. Complete goodbye format: 'Thanks for calling poms.tech, {farewell}!'",
                "4. ALWAYS call end_call immediately after saying goodbye - do NOT wait for user response",
                $"5. NEVER use 'goodbye' - always use '{farewell}'",
                "6. If user responds to your farewell (like 'you too', 'thanks', 'bye'), DO NOT repeat the farewell - just call end_call immediately",
                "7. CRITICAL: Once you say your farewell, the conversation is OVER - call end_call no matter what the user says next",
                
                // NEW: Loop prevention rules
                "LOOP PREVENTION RULES:",
                "- If you have already said your farewell message once, do NOT say it again",
                "- If user responds to your farewell with politeness ('you too', 'thanks', 'bye'), just call end_call",
                "- Never have more than one farewell exchange - one farewell = immediate end_call",
                "- The conversation flow should be: User indicates done → Your farewell → end_call (regardless of user response)",
                $"- The phrase '{farewell}' should only be said ONCE per call",
                
                // NEW: Specific ending scenarios
                "SPECIFIC ENDING SCENARIOS:",
                "Scenario 1: User says 'no message' or 'nothing else'",
                $"- You: 'Thanks for calling poms.tech, {farewell}!' + call end_call",
                "- Do NOT wait for user response",
                
                "Scenario 2: User says 'goodbye' or 'bye'", 
                $"- You: 'Thanks for calling poms.tech, {farewell}!' + call end_call",
                "- Do NOT wait for user response",
                
                "Scenario 3: User responds to your farewell ('you too', 'thanks')",
                "- You: call end_call immediately (NO additional speaking)",
                "- This prevents farewell loops",
                
                "Scenario 4: User says something after you've said farewell",
                "- You: call end_call immediately (NO speaking at all)",
                "- Farewell was already given - conversation is over",
                
                // Example complete conversation flow
                "COMPLETE EXAMPLE CONVERSATION:",
                "User: 'I need to send a message to Terry'",
                "AI: [calls check_staff_exists(name='Terry')]",
                "Response: 'authorized|IT'",
                "AI: [parses: status=authorized, department=IT, stores context]",
                "AI: 'Terry in IT department - what message would you like me to send?'",
                "User: 'The server is down in building 3'",
                "AI: [calls send_message(name='Terry', message='The server is down in building 3', department='IT')]",
                "Response: 'success'",
                "AI: 'I have sent your message to Terry in IT. Is there anything else I can help you with?'",
                "User: 'No that's all'",
                $"AI: 'Thanks for calling poms.tech, {farewell}!' [calls end_call()]",
                "User: 'You too' (if they respond)",
                "AI: [calls end_call() - NO speaking]",
                
                $"MEMORY AID: Current time={timeOfDay}, greeting='{greeting}', farewell='{farewell}'",
                "CRITICAL: Always parse the '|' separated response format from check_staff_exists and preserve department context throughout the conversation!",
                "PARSING IS MANDATORY: 'authorized|IT' means authorized person in IT department - you MUST extract 'IT' and use it in send_message!",
                $"FAREWELL RULE: Say '{farewell}' only ONCE per call, then end_call immediately regardless of user response!");
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
                description = "Check if staff member exists and is authorized. Returns 'authorized|DEPARTMENT' (found in specific dept), 'authorized|' (found, no specific dept), 'multiple_found|DEPT1,DEPT2' (multiple matches), 'not_authorized', or confirmation request starting with 'confirm:'.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new
                        {
                            type = "string",
                            description = "The name of the person to check. Will be fuzzy matched if not found exactly."
                        },
                        department = new
                        {
                            type = "string",
                            description = "Optional department filter. Use when multiple people have the same name."
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
                description = "Confirm a fuzzy match suggestion after user verification. Use when check_staff_exists returns a confirmation request.",
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
                            description = "The name the user confirmed"
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
                description = "Send message to verified staff member. CRITICAL: Include the department that was returned from check_staff_exists authorization. If check_staff_exists returned 'authorized|IT', you MUST include department='IT' here.",
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
                            description = "The department extracted from check_staff_exists response. If check_staff_exists returned 'authorized|DEPARTMENT', use that DEPARTMENT here. Required to ensure message goes to correct person."
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
                description = $"End the call gracefully. Call this immediately after saying the goodbye message: 'Thanks for calling poms.tech, {farewell}!' OR if the user responds to your farewell message, call this with NO additional speaking.",
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

            return $"Voice: en-US-EmmaNeural | VAD: azure_semantic_vad | Time: {timeOfDay} | Greeting: '{greeting}' | Farewell: '{farewell}'";
        }
    }
}
