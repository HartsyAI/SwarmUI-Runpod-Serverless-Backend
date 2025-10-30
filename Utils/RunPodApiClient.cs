using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Hartsy.Extensions.RunPodServerless.Models;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;

namespace Hartsy.Extensions.RunPodServerless.Utils;

/// <summary>Client for interacting with RunPod serverless endpoints and workers.</summary>
public class RunPodApiClient
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly string _apiKey;
    private readonly string _endpointId;

    public RunPodApiClient(string apiKey, string endpointId)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _endpointId = endpointId ?? throw new ArgumentNullException(nameof(endpointId));
    }

    /// <summary>Wake up worker and optionally keep it alive.</summary>
    /// <param name="keepaliveDuration">How long to keep worker alive in seconds (default: 3600)</param>
    /// <param name="keepaliveInterval">Ping interval in seconds (default: 30)</param>
    public async Task<WorkerInfo> WakeupWorkerAsync(int keepaliveDuration = 3600, int keepaliveInterval = 30, CancellationToken cancel = default)
    {
        var payload = new
        {
            input = new
            {
                action = "wakeup",
                duration = keepaliveDuration,
                interval = keepaliveInterval
            }
        };

        JObject response = await CallRunPodHandlerAsync(payload, useSync: false, cancel);
        
        if (response["success"]?.Value<bool>() != true)
        {
            string error = response["error"]?.ToString() ?? "Unknown error";
            throw new Exception($"Worker wakeup failed: {error}");
        }

        return new WorkerInfo
        {
            PublicUrl = response["public_url"]?.ToString(),
            SessionId = response["session_id"]?.ToString(),
            WorkerId = response["worker_id"]?.ToString(),
            Version = response["version"]?.ToString()
        };
    }

    /// <summary>Check if worker is ready for generation.</summary>
    public async Task<WorkerReadyResponse> CheckWorkerReadyAsync(CancellationToken cancel = default)
    {
        var payload = new
        {
            input = new
            {
                action = "ready"
            }
        };

        JObject response = await CallRunPodHandlerAsync(payload, useSync: true, cancel);
        
        return new WorkerReadyResponse
        {
            Ready = response["ready"]?.Value<bool>() ?? false,
            PublicUrl = response["public_url"]?.ToString(),
            SessionId = response["session_id"]?.ToString(),
            WorkerId = response["worker_id"]?.ToString(),
            Error = response["error"]?.ToString()
        };
    }

    /// <summary>Perform health check on worker.</summary>
    public async Task<bool> HealthCheckAsync(CancellationToken cancel = default)
    {
        try
        {
            var payload = new
            {
                input = new
                {
                    action = "health"
                }
            };

            JObject response = await CallRunPodHandlerAsync(payload, useSync: true, cancel);
            return response["healthy"]?.Value<bool>() ?? false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Send keepalive to worker.</summary>
    public async Task KeepAliveAsync(int duration = 3600, int interval = 30, CancellationToken cancel = default)
    {
        var payload = new
        {
            input = new
            {
                action = "keepalive",
                duration,
                interval
            }
        };

        await CallRunPodHandlerAsync(payload, useSync: false, cancel);
    }

    /// <summary>Signal worker to shutdown.</summary>
    public async Task ShutdownWorkerAsync(CancellationToken cancel = default)
    {
        try
        {
            var payload = new
            {
                input = new
                {
                    action = "shutdown"
                }
            };

            await CallRunPodHandlerAsync(payload, useSync: true, cancel);
        }
        catch (Exception ex)
        {
            Logs.Verbose($"Shutdown signal failed (worker may have already scaled down): {ex.Message}");
        }
    }

    /// <summary>Make direct API call to SwarmUI instance running on worker.</summary>
    public async Task<JObject> CallSwarmUIAsync(string workerPublicUrl, string apiPath, JObject requestBody, CancellationToken cancel = default)
    {
        if (string.IsNullOrEmpty(workerPublicUrl))
            throw new ArgumentException("Worker public URL is required", nameof(workerPublicUrl));

        string url = $"{workerPublicUrl.TrimEnd('/')}/{apiPath.TrimStart('/')}"; 

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(requestBody.ToString(), Encoding.UTF8, "application/json")
        };

        Logs.Verbose($"Calling SwarmUI at {url}");

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancel);
        
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(cancel);
            throw new HttpRequestException($"SwarmUI API call failed ({response.StatusCode}): {error}");
        }

        string content = await response.Content.ReadAsStringAsync(cancel);
        return JObject.Parse(content);
    }

    /// <summary>Call RunPod handler endpoint.</summary>
    private async Task<JObject> CallRunPodHandlerAsync(object payload, bool useSync, CancellationToken cancel)
    {
        string endpoint = useSync ? "runsync" : "run";
        string url = $"https://api.runpod.ai/v2/{_endpointId}/{endpoint}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JObject.FromObject(payload).ToString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        Logs.Verbose($"Calling RunPod handler: {endpoint}");

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancel);
        
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(cancel);
            throw new HttpRequestException($"RunPod API call failed ({response.StatusCode}): {error}");
        }

        string content = await response.Content.ReadAsStringAsync(cancel);
        JObject result = JObject.Parse(content);

        // For sync calls, result is directly in the response
        // For async calls, need to poll for result
        if (!useSync)
        {
            string jobId = result["id"]?.ToString();
            if (!string.IsNullOrEmpty(jobId))
            {
                result = await PollJobStatusAsync(jobId, cancel);
            }
        }

        // Extract output from wrapper
        return result["output"] as JObject ?? result;
    }

    /// <summary>Poll job status for async calls.</summary>
    private async Task<JObject> PollJobStatusAsync(string jobId, CancellationToken cancel)
    {
        string url = $"https://api.runpod.ai/v2/{_endpointId}/status/{jobId}";
        int maxAttempts = 300; // 5 minutes with 1s interval
        
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancel.ThrowIfCancellationRequested();

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancel);
            string content = await response.Content.ReadAsStringAsync(cancel);
            JObject result = JObject.Parse(content);

            string status = result["status"]?.ToString();
            
            if (status == "COMPLETED")
            {
                return result["output"] as JObject ?? result;
            }
            else if (status == "FAILED")
            {
                string error = result["error"]?.ToString() ?? "Job failed";
                throw new Exception($"RunPod job failed: {error}");
            }

            await Task.Delay(1000, cancel);
        }

        throw new TimeoutException($"Job {jobId} did not complete within timeout");
    }
}

/// <summary>Worker information from wakeup call.</summary>
public class WorkerInfo
{
    public string PublicUrl { get; set; }
    public string SessionId { get; set; }
    public string WorkerId { get; set; }
    public string Version { get; set; }
}

/// <summary>Worker ready check response.</summary>
public class WorkerReadyResponse
{
    public bool Ready { get; set; }
    public string PublicUrl { get; set; }
    public string SessionId { get; set; }
    public string WorkerId { get; set; }
    public string Error { get; set; }
}
