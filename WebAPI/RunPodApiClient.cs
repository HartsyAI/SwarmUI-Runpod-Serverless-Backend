using Hartsy.Extensions.RunPodServerless.Models;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Utils;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Hartsy.Extensions.RunPodServerless.WebAPI;

/// <summary>Client for interacting with RunPod serverless endpoints and workers.</summary>
public class RunPodApiClient(string apiKey, string endpointId)
{
    /// <summary>Shared HTTP client using SwarmUI's infrastructure.</summary>
    public HttpClient HttpClient = NetworkBackendUtils.MakeHttpClient();

    /// <summary>Wake up worker with keepalive. Submits async job and returns job ID immediately.</summary>
    /// <param name="keepaliveDuration">How long to keep worker alive in seconds (default: 3600)</param>
    /// <param name="keepaliveInterval">Ping interval in seconds (default: 30)</param>
    /// <returns>Job ID for tracking the wakeup request</returns>
    public async Task<string> WakeupWorkerAsync(int keepaliveDuration = 3600, int keepaliveInterval = 30, CancellationToken cancel = default)
    {
        JObject payload = new()
        {
            ["input"] = new JObject
            {
                ["action"] = "wakeup",
                ["duration"] = keepaliveDuration,
                ["interval"] = keepaliveInterval
            }
        };
        Logs.Verbose($"[RunPodApiClient] Sending wakeup signal (duration: {keepaliveDuration}s, interval: {keepaliveInterval}s)");
        string jobId = await SubmitJobAsync(payload, cancel);
        Logs.Verbose($"[RunPodApiClient] Wakeup job submitted: {jobId}");
        return jobId;
    }

    /// <summary>Submit a job to RunPod async endpoint, returns job ID immediately without waiting.</summary>
    public async Task<string> SubmitJobAsync(JObject payload, CancellationToken cancel = default)
    {
        string url = $"https://api.runpod.ai/v2/{endpointId}/run";
        using HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        Logs.Verbose($"[RunPodApiClient] Submitting async job to RunPod");
        using HttpResponseMessage response = await HttpClient.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(cancel);
            throw new HttpRequestException($"RunPod API call failed ({response.StatusCode}): {error}");
        }
        string content = await response.Content.ReadAsStringAsync(cancel);
        JObject result = JObject.Parse(content);
        return result["id"]?.ToString() ?? throw new Exception("No job ID in RunPod response");
    }

    /// <summary>Get the current status of a job. Returns the full status response.</summary>
    public async Task<JObject> GetJobStatusAsync(string jobId, CancellationToken cancel = default)
    {
        string url = $"https://api.runpod.ai/v2/{endpointId}/status/{jobId}";
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using HttpResponseMessage response = await HttpClient.SendAsync(request, cancel);
        string content = await response.Content.ReadAsStringAsync(cancel);
        return JObject.Parse(content);
    }

    /// <summary>Check if worker is ready for generation.</summary>
    public async Task<WorkerReadyResponse> CheckWorkerReadyAsync(CancellationToken cancel = default)
    {
        JObject payload = new()
        {
            ["input"] = new JObject
            {
                ["action"] = "ready"
            }
        };

        JObject response = await CallRunPodHandlerAsync(payload, useSync: true, cancel);

        return new WorkerReadyResponse
        {
            Ready = response["ready"]?.Value<bool>() ?? false,
            PublicUrl = response["public_url"]?.ToString(),
            SessionId = response["session_id"]?.ToString(),
            WorkerId = response["worker_id"]?.ToString(),
            Version = response["version"]?.ToString(),
            Error = response["error"]?.ToString()
        };
    }

    /// <summary>Perform health check on worker.</summary>
    public async Task<bool> HealthCheckAsync(CancellationToken cancel = default)
    {
        try
        {
            JObject payload = new()
            {
                ["input"] = new JObject
                {
                    ["action"] = "health"
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

    /// <summary>Send keepalive to extend worker lifetime.</summary>
    public async Task KeepAliveAsync(int duration = 3600, int interval = 30, CancellationToken cancel = default)
    {
        JObject payload = new()
        {
            ["input"] = new JObject
            {
                ["action"] = "keepalive",
                ["duration"] = duration,
                ["interval"] = interval
            }
        };
        await CallRunPodHandlerAsync(payload, useSync: false, cancel);
    }

    /// <summary>Signal worker to shutdown gracefully.</summary>
    public async Task ShutdownWorkerAsync(CancellationToken cancel = default)
    {
        try
        {
            JObject payload = new()
            {
                ["input"] = new JObject
                {
                    ["action"] = "shutdown"
                }
            };
            JObject response = await CallRunPodHandlerAsync(payload, useSync: true, cancel);
            if (response["success"]?.Value<bool>() == true)
            {
                Logs.Verbose("[RunPodApiClient] Shutdown signal acknowledged by worker");
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[RunPodApiClient] Shutdown signal failed (worker may have already scaled down): {ex.Message}");
        }
    }

    /// <summary>Make direct API call to SwarmUI instance running on worker.</summary>
    public async Task<JObject> CallSwarmUIAsync(string workerPublicUrl, string apiPath, JObject requestBody, int timeoutSeconds = 600, CancellationToken cancel = default)
    {
        if (string.IsNullOrEmpty(workerPublicUrl))
        {
            throw new ArgumentException("Worker public URL is required", nameof(workerPublicUrl));
        }
        string url = $"{workerPublicUrl.TrimEnd('/')}/{apiPath.TrimStart('/')}";
        using HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new StringContent(requestBody.ToString(), Encoding.UTF8, "application/json")
        };
        Logs.Verbose($"[RunPodApiClient] Calling SwarmUI: POST {url}");
        using HttpResponseMessage response = await HttpClient.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(cancel);
            throw new HttpRequestException($"SwarmUI API call failed ({response.StatusCode}): {error}");
        }
        string content = await response.Content.ReadAsStringAsync(cancel);
        JObject result = JObject.Parse(content);
        // Check for session errors and throw if found
        RunPodServerlessBackend.AutoThrowException(result);
        return result;
    }

    /// <summary>Call RunPod handler endpoint (sync or async).</summary>
    public async Task<JObject> CallRunPodHandlerAsync(JObject payload, bool useSync, CancellationToken cancel)
    {
        string endpoint = useSync ? "runsync" : "run";
        string url = $"https://api.runpod.ai/v2/{endpointId}/{endpoint}";
        using HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        Logs.Verbose($"[RunPodApiClient] Calling RunPod handler: {endpoint}");
        using HttpResponseMessage response = await HttpClient.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(cancel);
            throw new HttpRequestException($"RunPod API call failed ({response.StatusCode}): {error}");
        }
        string content = await response.Content.ReadAsStringAsync(cancel);
        JObject result = JObject.Parse(content);
        if (!useSync)
        {
            string jobId = result["id"]?.ToString();
            if (!string.IsNullOrEmpty(jobId))
            {
                result = await PollJobStatusAsync(jobId, cancel);
            }
        }
        return result["output"] as JObject ?? result;
    }

    /// <summary>Poll job status for async RunPod calls. Extracts worker info when IN_PROGRESS.</summary>
    public async Task<JObject> PollJobStatusAsync(string jobId, CancellationToken cancel)
    {
        string url = $"https://api.runpod.ai/v2/{endpointId}/status/{jobId}";
        int maxAttempts = 300;
        string lastWorkerId = null;
        
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using HttpResponseMessage response = await HttpClient.SendAsync(request, cancel);
            string content = await response.Content.ReadAsStringAsync(cancel);
            JObject result = JObject.Parse(content);
            string status = result["status"]?.ToString();
            string workerId = result["workerId"]?.ToString();
            
            if (!string.IsNullOrEmpty(workerId) && workerId != lastWorkerId)
            {
                lastWorkerId = workerId;
                Logs.Verbose($"[RunPodApiClient] Job {jobId} assigned to worker: {workerId}");
            }
            
            if (status == "COMPLETED")
            {
                // Ensure workerId is in the result for caller to use
                if (!string.IsNullOrEmpty(lastWorkerId) && result["workerId"] == null)
                {
                    result["workerId"] = lastWorkerId;
                }
                return result;
            }
            else if (status == "IN_PROGRESS")
            {
                Logs.Verbose($"[RunPodApiClient] Job {jobId} in progress on worker {workerId}...");
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
