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

            _logger.LogInformation($"🕐 Building session with: greeting='{greeting}', farewell='{farewell}', timeOfDay='{timeOfDay}'");

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
                
                // CRITICAL: Department preservation rules
                "DEPARTMENT PRESERVATION RULES:",
                "1. When check_staff_exists returns 'authorized' with a department specified, YOU MUST remember that department for the entire conversation with that person.",
                "2. When calling send_message, ALWAYS include the department that was used in the successful check_staff_exists call.",
                "3. NEVER call send_message without the department if a department was used during staff verification.",
                "4. Track the department context throughout the conversation - do not lose it between function calls.",
                
                "DETAILED WORKFLOW:",
                "Step 1: User asks to send message to [Name]",
                "Step 2: Call check_staff_exists with name (and department if user provided it)",
                "Step 3a: If result is 'authorized' - remember the department that was used and proceed to get message",
                "Step 3b: If result shows multiple departments available, ask user to specify department",
                "Step 4: If user specifies department, call check_staff_exists again with name AND department",
                "Step 5: When 'authorized', remember the EXACT department that made it authorized",
                "Step 6: Get message content from user",  
                "Step 7: Call send_message with name, message, AND the department that was authorized in steps 3a or 5",
                
                // Enhanced name handling with confirmation flow
                "NAME HANDLING RULES:",
                "1. When a caller provides a name, ALWAYS use the check_staff_exists function first.",
                "2. If check_staff_exists returns 'authorized', proceed with taking the message.",
                "3. If check_staff_exists returns 'not_authorized', politely ask the caller to spell the name or provide the department.",
                "4. If check_staff_exists returns a message starting with 'confirm:', parse it and ask for confirmation.",
                "5. Only proceed with message taking after getting 'authorized' from either check_staff_exists or confirm_staff_match.",
                
                // Message handling
                "MESSAGE HANDLING:",
                "1. After staff verification is successful, ask: 'What message would you like me to send to [Name] in [Department]?'",
                "2. Use send_message function with name, message, AND department.",
                "3. After sending successfully, say 'I have sent your message to [Name] in [Department]. Is there anything else I can help you with?'",
                
                // FIXED: Call ending with ACTUAL time-of-day farewell embedded
                "CALL ENDING - CRITICAL FAREWELL INSTRUCTIONS:",
                $"1. When the caller says 'no', 'nothing else', 'that's all', 'goodbye', etc., you MUST say EXACTLY: 'Thanks for calling poms.tech, {farewell}!' and then use the end_call function.",
                $"2. The current time-appropriate farewell is: '{farewell}' - use this EXACT phrase.",
                $"3. Your mandatory goodbye message format: 'Thanks for calling poms.tech, {farewell}!'",
                "4. CRITICAL: Always call end_call immediately after saying the goodbye message.",
                $"5. FORBIDDEN: Never say 'goodbye' - always use the specific farewell: '{farewell}'",
                $"6. DOUBLE CHECK: The farewell phrase is '{farewell}' - memorize this and use it exactly.",
                
                "CORRECT FAREWELL EXAMPLES (use these exact formats):",
                $"✅ CORRECT: 'Thanks for calling poms.tech, {farewell}!'",
                $"✅ CORRECT: 'Thank you for calling poms.tech, {farewell}!'", 
                $"✅ CORRECT: 'Thanks for using poms.tech after hours service, {farewell}!'",
                "❌ WRONG: 'Thanks for calling poms.tech, goodbye!'",
                "❌ WRONG: 'Thanks for calling, bye!'",
                "❌ WRONG: Any variation with 'goodbye', 'bye', 'farewell', or generic closings",
                
                $"MEMORY AID: Current farewell = '{farewell}'. Use this exact phrase every time.",
                $"TIME CONTEXT: It is currently {timeOfDay} time, so '{farewell}' is the appropriate closing.",
                
                $"Remember: greeting='{greeting}', farewell='{farewell}', time={timeOfDay}.",
                "The system helps find staff even with speech recognition errors, but always preserve department context between function calls.",
                "DEPARTMENT CONTEXT IS CRITICAL - Never call send_message without the department if one was used during verification!");
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
                description = "Check if a staff member is authorized to receive messages. Returns 'authorized', 'not_authorized', or lists available departments for duplicates. Remember the department used when this returns 'authorized'.",
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
                            description = "The department the person works in. REQUIRED when multiple people have the same name."
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
                description = "Confirm a fuzzy match suggestion when check_staff_exists returns a confirmation request.",
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
                            description = "The department"
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
                description = "Send a message to a staff member after verification. CRITICAL: Must include the department if it was used during check_staff_exists verification.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new
                        {
                            type = "string",
                            description = "The exact name of the person to send the message to (must match the verified name)"
                        },
                        message = new
                        {
                            type = "string",
                            description = "The message content from the caller"
                        },
                        department = new
                        {
                            type = "string",
                            description = "The department the person works in. REQUIRED if a department was used during check_staff_exists verification. This ensures the message goes to the correct person when there are multiple staff with the same name."
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
                description = $"End the call gracefully. This should only be called AFTER saying the goodbye message: 'Thanks for calling poms.tech, {farewell}!'",
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
