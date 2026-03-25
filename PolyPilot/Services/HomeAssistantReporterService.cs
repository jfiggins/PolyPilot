using System.Net.Http.Headers;
using System.Net.Http.Json;
using PolyPilot.Models;

namespace PolyPilot.Services;

/// <summary>
/// Reports PolyPilot status to a Home Assistant instance via its REST API
/// whenever CopilotService state changes. Pushes two entities:
///   • sensor.polypilot_status — mode, session counts, active session details
///   • binary_sensor.polypilot_processing — on/off for any active processing
/// All errors are silently swallowed — reporting is best-effort.
/// </summary>
public class HomeAssistantReporterService : IAsyncDisposable
{
    private readonly CopilotService _copilotService;
    private readonly HttpMessageHandler? _httpHandler;
    private HttpClient? _httpClient;
    private HttpClient HttpClient => _httpClient ??= _httpHandler != null
        ? new HttpClient(_httpHandler, disposeHandler: false)
        : new HttpClient();

    // 0 = idle, 1 = report pending. Atomic flag for debouncing rapid state changes.
    private int _pendingReport;

    /// <summary>True if the last report succeeded, false if it failed, null if never attempted.</summary>
    public bool? LastReportSuccess { get; private set; }

    /// <summary>UTC time of the last report attempt (success or failure).</summary>
    public DateTime? LastReportTime { get; private set; }

    /// <summary>Error message from the last failed report, or null if last report succeeded.</summary>
    public string? LastReportError { get; private set; }

    public HomeAssistantReporterService(CopilotService copilotService)
        : this(copilotService, null) { }

    internal HomeAssistantReporterService(CopilotService copilotService, HttpMessageHandler? httpHandler)
    {
        _copilotService = copilotService;
        _httpHandler = httpHandler;
        _copilotService.OnStateChanged += OnStateChanged;
    }

    private void OnStateChanged()
    {
        // Debounce: collapse rapid-fire state changes into a single deferred report.
        if (Interlocked.CompareExchange(ref _pendingReport, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            Interlocked.Exchange(ref _pendingReport, 0);
            await DoReportAsync();
        });
    }

    private async Task DoReportAsync()
    {
        var settings = _copilotService.CurrentSettings;
        if (settings is null || !settings.HomeAssistantEnabled)
            return;

        var url = settings.HomeAssistantUrl?.TrimEnd('/');
        var token = settings.HomeAssistantToken;

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
            return;

        try
        {
            var activeSession = _copilotService.GetActiveSession();
            var processingCount = _copilotService.ProcessingSessionCount;

            // ── sensor.polypilot_status ──────────────────────────────────
            var statusPayload = new
            {
                state = _copilotService.CurrentMode.ToString(),
                attributes = new
                {
                    session_count = _copilotService.Organization.Sessions.Count,
                    processing_count = processingCount,
                    active_session = _copilotService.ActiveSessionName ?? "",
                    active_session_model = activeSession?.Model ?? "",
                    active_session_input_tokens = activeSession?.TotalInputTokens ?? 0,
                    active_session_output_tokens = activeSession?.TotalOutputTokens ?? 0,
                    active_session_is_processing = activeSession?.IsProcessing ?? false,
                    is_initialized = _copilotService.IsInitialized,
                    is_bridge_connected = _copilotService.IsBridgeConnected,
                    github_login = _copilotService.GitHubLogin ?? "",
                    version = BuildInfo.BuildTimestamp,
                    friendly_name = "PolyPilot Status"
                }
            };

            await PostEntityAsync(url, token, "sensor.polypilot_status", statusPayload);

            // ── binary_sensor.polypilot_processing ──────────────────────
            var processingSessions = _copilotService.GetAllSessions()
                .Where(s => s.IsProcessing)
                .Select(s => s.Name)
                .ToList();

            var processingPayload = new
            {
                state = processingCount > 0 ? "on" : "off",
                attributes = new
                {
                    processing_count = processingCount,
                    processing_sessions = processingSessions,
                    device_class = "running",
                    friendly_name = "PolyPilot Processing"
                }
            };

            await PostEntityAsync(url, token, "binary_sensor.polypilot_processing", processingPayload);

            LastReportSuccess = true;
            LastReportError = null;
            LastReportTime = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            LastReportSuccess = false;
            LastReportError = ex.Message;
            LastReportTime = DateTime.UtcNow;
        }
    }

    private async Task PostEntityAsync(string url, string token, string entityId, object payload)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{url}/api/states/{entityId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(payload);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await HttpClient.SendAsync(request, cts.Token);
    }

    /// <summary>
    /// Tests connectivity to a Home Assistant instance by calling GET /api/.
    /// Returns null on success, or an error message on failure.
    /// </summary>
    public async Task<string?> TestConnectionAsync(string? url, string? token)
    {
        url = url?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(url))
            return "URL is required.";
        if (string.IsNullOrWhiteSpace(token))
            return "Access token is required.";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{url}/api/");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await HttpClient.SendAsync(request, cts.Token);

            if (response.IsSuccessStatusCode)
                return null;

            return response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "Invalid access token.",
                System.Net.HttpStatusCode.Forbidden => "Token does not have sufficient permissions.",
                _ => $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}"
            };
        }
        catch (TaskCanceledException)
        {
            return "Connection timed out — check the URL.";
        }
        catch (HttpRequestException ex)
        {
            return $"Connection failed: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Unexpected error: {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        _copilotService.OnStateChanged -= OnStateChanged;
        _httpClient?.Dispose();
        _httpClient = null;
    }
}
