# Setup Guide - Azure Voice AI

This guide will help you configure and run the Azure Voice AI application.

## Prerequisites

Before you begin, ensure you have:

1. **Azure Subscription** with the following resources:
   - Azure Communication Services (with a phone number)
   - Azure AI Services (Speech/Voice Live)
   - Azure Storage Account (for staff directory)
   - Azure AD App Registration (for Microsoft Graph)

2. **.NET 8 SDK** installed
   ```bash
   dotnet --version  # Should show 8.0.x or higher
   ```

3. **Dev Tunnel CLI** or **Ngrok** (for local development)

---

## Configuration Steps

### 1. Create Your Configuration File

Copy the example configuration file:

```bash
cp appsettings.example.json appsettings.json
```

**IMPORTANT**: Never commit `appsettings.json` to git! It's already in `.gitignore`.

---

### 2. Configure Azure Communication Services

1. Go to **Azure Portal** → **Communication Services** → Your resource
2. Copy the **Connection String** from the "Keys" section
3. Update in `appsettings.json`:
   ```json
   "AcsConnectionString": "endpoint=https://YOUR_RESOURCE.communication.azure.com/;accesskey=..."
   ```

---

### 3. Configure Azure AI Voice Live

1. Go to **Azure Portal** → **Azure AI Services** → Your resource
2. Copy the **Endpoint** and **API Key** from "Keys and Endpoint"
3. Update in `appsettings.json`:
   ```json
   "AzureVoiceLiveApiKey": "your-api-key-here",
   "AzureVoiceLiveEndpoint": "https://your-resource.cognitiveservices.azure.com/",
   "VoiceLiveModel": "gpt-4o-mini-realtime-preview"
   ```

---

### 4. Configure Microsoft Graph API (Email Sending)

1. **Create App Registration** in Azure AD:
   - Go to **Azure Portal** → **Azure Active Directory** → **App registrations** → **New registration**
   - Name: "Azure Voice AI Email Service"
   - Click **Register**

2. **Configure API Permissions**:
   - Go to **API permissions** → **Add a permission** → **Microsoft Graph** → **Application permissions**
   - Add: `Mail.Send`
   - Click **Grant admin consent**

3. **Create Client Secret**:
   - Go to **Certificates & secrets** → **New client secret**
   - Copy the secret value immediately (it won't show again!)

4. **Update Configuration**:
   ```json
   "GraphTenantId": "your-tenant-id",
   "GraphClientId": "your-app-client-id",
   "GraphClientSecret": "your-client-secret",
   "GraphSenderUPN": "sender@yourdomain.com"
   ```

---

### 5. Configure Azure Table Storage (Staff Directory)

1. Go to **Azure Portal** → **Storage accounts** → Your storage account
2. Copy the **Connection details** from "Access keys"
3. Create a table named `StaffDirectory` in **Tables** section
4. Update in `appsettings.json`:
   ```json
   "StorageUri": "https://your-storage.table.core.windows.net",
   "StorageAccountName": "your-storage-account",
   "StorageAccountKey": "your-storage-key",
   "TableName": "StaffDirectory"
   ```

**Staff Directory Schema**:
```
PartitionKey: "Staff"
RowKey: <unique-id>
FirstName: "John"
LastName: "Doe"
Email: "john.doe@yourdomain.com"
Department: "Engineering"
```

---

### 6. Setup Dev Tunnel (Local Development)

Install and configure Dev Tunnel:

```bash
# Install
curl -sL https://aka.ms/DevTunnelCliInstall | bash

# Login
devtunnel user login

# Create tunnel
devtunnel create --allow-anonymous

# Create port
devtunnel port create -p 49412

# Start hosting
devtunnel host
```

Copy the HTTPS URL (e.g., `https://abc123.devtunnels.ms`) and update:

```json
"AppBaseUrl": "https://abc123.devtunnels.ms"
```

---

### 7. Configure Event Grid Webhook

1. Go to **Azure Portal** → **Communication Services** → Your resource → **Events**
2. Click **+ Event Subscription**
3. Configure:
   - **Name**: `incoming-call-webhook`
   - **Event Types**: Select **Incoming Call**
   - **Endpoint Type**: `Web Hook`
   - **Endpoint**: `https://YOUR_TUNNEL_OR_WEBAPP/api/incomingCall`
4. Click **Create**

---

### 8. Build and Run

```bash
# Restore dependencies
dotnet restore

# Build
dotnet build

# Run
dotnet run --project CallAutomation_AzureAI_VoiceLive.csproj
```

The application will start on:
- HTTPS: `https://localhost:49411`
- HTTP: `http://localhost:49412`

---

## Testing Your Setup

1. **Check Health Endpoint**:
   ```bash
   curl https://localhost:49411/api/health
   ```
   Should return status: "healthy"

2. **Test with a Phone Call**:
   - Call your ACS phone number
   - The system should answer and greet you
   - Follow the voice prompts

3. **Check Logs**:
   - Logs are written to `logs/app-log-YYYY-MM-DD.txt`
   - Monitor for any errors or warnings

---

## Common Issues

### Issue: "AcsConnectionString is missing"
**Solution**: Ensure you copied the full connection string including `endpoint=` and `accesskey=`

### Issue: "Graph API configuration is missing"
**Solution**: Verify all Graph settings (TenantId, ClientId, ClientSecret, SenderUPN) are configured

### Issue: Event Grid webhook validation fails
**Solution**:
1. Ensure your tunnel/app is running
2. Check the endpoint URL is correct
3. Look for subscription validation events in logs

### Issue: WebSocket timeout errors
**Solution**: These are typically network-related. The app uses 120s timeout to handle this.

---

## Security Checklist

- [ ] `appsettings.json` is in `.gitignore`
- [ ] Never commit real credentials to git
- [ ] Use Azure Key Vault for production secrets
- [ ] Enable HTTPS only in production
- [ ] Restrict Event Grid webhook to Azure IP ranges
- [ ] Review and restrict Graph API permissions
- [ ] Enable audit logging on Azure resources

---

## Production Deployment

For deploying to Azure Web App, see [README.md](README.md#-deploying-to-azure-web-apps)

**Key differences for production**:
1. Use **App Settings** in Azure Web App instead of `appsettings.json`
2. Store secrets in **Azure Key Vault**
3. Use **Managed Identity** instead of client secrets where possible
4. Enable **Application Insights** for monitoring
5. Configure **auto-scaling** based on concurrent calls

---

## Support

For issues or questions:
1. Check logs in `logs/` directory
2. Review [claude.md](claude.md) for architecture details
3. Open an issue on GitHub

---

**Last Updated**: 2025-11-25
