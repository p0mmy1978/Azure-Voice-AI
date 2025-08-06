This repository demonstrates how to build a **voice‑first virtual assistant** using **Azure Communication Services (ACS)** and **Azure AI Voice Live** via real‑time WebSocket streaming and Azure OpenAI models.

## 🧪 Prerequisites

1. **Azure Subscription**  
2. **Azure Communication Services** resource (with calling-enabled phone number)  
3. **Azure AI Voice live* resource with deployed model (e.g. `gpt‑4o‑mini‑realtime‑preview`)  
4. **Ngrok or Azure DevTunnels CLI** (for exposing local callbacks)

## 🚀 Setup Instructions

### 1. Clone this repo:
```bash
git clone https://github.com/p0mmy1978/Azure-Voice-AI.git
cd Azure-Voice-AI

2. Configure environment (or use appsettings.json):
{
  "AcsConnectionString": "<ACS_CONNECTION_STRING>",
  "AppBaseUrl": "<https://your-ngrok-or-devtunnel>",
  "AzureVoiceLiveEndpoint": "https://(your-endpint-name).cognitiveservices.azure.com/',
  "AzureVoiceLiveApiKey": "<YOUR_VOICE_LIVE_API_KEY>",
  "VoiceLiveModel": "<your-model-name>",
  "SystemPrompt": "<optional‑system‑prompt>"
}


3. set up and Start your dev tunnel:

to install - curl -sL https://aka.ms/DevTunnelCliInstall | bash

Then to set up 
devtunnel user login
devtunnel create --allow-anonymous
devtunnel port create -p 49412
devtunnel host


4. Register ACS EventGrid Callback:
Go to Azure Portal → your Communication Services resource → Event Grid → subscribe to IncomingCall

Set Endpoint URL to:
https://<your-tunnel-domain>/api/incomingCall


5. Run the app:
dotnet run --project CallAutomation_AzureAI_VoiceLive.csproj
