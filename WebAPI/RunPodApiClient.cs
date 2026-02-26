using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Utils;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Hartsy.Extensions.RunPodServerless.WebAPI;

/// <summary>Client for interacting with RunPod serverless endpoints and workers.
/// All worker lifecycle operations (wakeup, keepalive) use the async /run endpoint to avoid
/// creating multiple jobs. Status polling uses GET /status which is read-only and free.</summary>
public class RunPodApiClient(string apiKey, string endpointId)
{
    /// <summary>Shared HTTP client using SwarmUI's infrastructure. Static to avoid socket exhaustion across instances.</summary>
    public static HttpClient HttpClient = NetworkBackendUtils.MakeHttpClient();

    /// <summary>Submit a job to RunPod's async /run endpoint. Returns immediately with the job ID.
    /// This does NOT wait for the job to complete — use <see cref="WaitForJobAsync"/> for that.</summary>
    public async Task<string> SubmitJobAsync(JObject payload, CancellationToken cancel = default)
    {
        string url = $"https://api.runpod.ai/v2/{endpointId}/run";
        string payloadStr = payload.ToString();
        using HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new StringContent(payloadStr, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        string action = payload["input"]?["action"]?.ToString() ?? "unknown";
        Logs.Debug($"[RunPodApiClient] POST /run (action: {action})");
        Logs.Verbose($"[RunPodApiClient] SubmitJobAsync: url={url}, payloadSize={payloadStr.Length} chars, action={action}");
        using HttpResponseMessage response = await HttpClient.SendAsync(request, cancel);
        Logs.Verbose($"[RunPodApiClient] SubmitJobAsync: response status={response.StatusCode}");
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(cancel);
            Logs.Warning($"[RunPodApiClient] /run failed ({response.StatusCode}): {error}");
            throw new HttpRequestException($"RunPod /run failed ({response.StatusCode}): {error}");
        }
        string content = await response.Content.ReadAsStringAsync(cancel);
        Logs.Verbose($"[RunPodApiClient] SubmitJobAsync: response body ({content.Length} chars): {content}");
        JObject result = JObject.Parse(content);
        string jobId = result["id"]?.ToString();
        if (string.IsNullOrEmpty(jobId))
        {
            throw new Exception($"RunPod /run did not return a job ID. Response: {content}");
        }
        Logs.Debug($"[RunPodApiClient] Job submitted: {jobId} (action: {action})");
        return jobId;
    }

    /// <summary>Poll a job's status via GET /status/{jobId}.
    /// This is a read-only API call — it does NOT create new jobs or spin up workers.</summary>
    public async Task<JObject> GetJobStatusAsync(string jobId, CancellationToken cancel = default)
    {
        string url = $"https://api.runpod.ai/v2/{endpointId}/status/{jobId}";
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        Logs.Verbose($"[RunPodApiClient] GetJobStatusAsync: GET {url}");
        using HttpResponseMessage response = await HttpClient.SendAsync(request, cancel);
        string content = await response.Content.ReadAsStringAsync(cancel);
        Logs.Verbose($"[RunPodApiClient] GetJobStatusAsync: {response.StatusCode} ({content.Length} chars)");
        return JObject.Parse(content);
    }

    /// <summary>Poll a job until it reaches COMPLETED or FAILED. Returns the output from the completed job.
    /// Uses GET /status which is read-only — no new jobs or workers are created during polling.</summary>
    public async Task<JObject> WaitForJobAsync(string jobId, int pollIntervalMs = 2000, int timeoutSec = 600, CancellationToken cancel = default)
    {
        Logs.Verbose($"[RunPodApiClient] WaitForJobAsync: jobId={jobId}, pollInterval={pollIntervalMs}ms, timeout={timeoutSec}s");
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        string lastStatus = "";
        int attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            cancel.ThrowIfCancellationRequested();
            attempt++;
            JObject result = await GetJobStatusAsync(jobId, cancel);
            string status = result["status"]?.ToString() ?? "UNKNOWN";
            if (status is not lastStatus)
            {
                int elapsed = (int)(DateTime.UtcNow - deadline.AddSeconds(-timeoutSec)).TotalSeconds;
                Logs.Info($"[RunPodApiClient] Job {jobId}: {status} (after {elapsed}s)");
                lastStatus = status;
            }
            else
            {
                Logs.Verbose($"[RunPodApiClient] Job {jobId}: {status} (poll #{attempt})");
            }
            if (status is "COMPLETED")
            {
                JObject output = result["output"] as JObject ?? new JObject();
                Logs.Verbose($"[RunPodApiClient] WaitForJobAsync: job {jobId} COMPLETED. Output keys: {string.Join(", ", output.Properties().Select(p => p.Name))}");
                return output;
            }
            else if (status is "FAILED")
            {
                string error = result["error"]?.ToString() ?? "Unknown error";
                Logs.Verbose($"[RunPodApiClient] WaitForJobAsync: job {jobId} FAILED. Error: {error}");
                throw new Exception($"RunPod job {jobId} failed: {error}");
            }
            // IN_QUEUE or IN_PROGRESS — keep polling
            await Task.Delay(pollIntervalMs, cancel);
        }
        throw new TimeoutException($"Job {jobId} did not complete within {timeoutSec}s");
    }

    /// <summary>Cancel a queued or running job. Best-effort — does not throw on failure.</summary>
    public async Task CancelJobAsync(string jobId, CancellationToken cancel = default)
    {
        if (string.IsNullOrEmpty(jobId)) return;
        try
        {
            string url = $"https://api.runpod.ai/v2/{endpointId}/cancel/{jobId}";
            using HttpRequestMessage request = new(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            Logs.Debug($"[RunPodApiClient] Cancelling job {jobId}");
            using HttpResponseMessage response = await HttpClient.SendAsync(request, cancel);
            Logs.Debug($"[RunPodApiClient] Cancel {jobId}: {response.StatusCode}");
        }
        catch (Exception ex)
        {
            Logs.Verbose($"[RunPodApiClient] Cancel failed for {jobId} (may already be done): {ex.Message}");
        }
    }

    /// <summary>Submit a wakeup job via /run. The handler should return quickly with connection info
    /// (public_url, session_id, worker_id). Returns the job ID — use <see cref="WaitForJobAsync"/> to get the output.</summary>
    public async Task<string> WakeupWorkerAsync(CancellationToken cancel = default)
    {
        JObject payload = new()
        {
            ["input"] = new JObject
            {
                ["action"] = "wakeup"
            }
        };
        Logs.Info("[RunPodApiClient] Submitting wakeup job...");
        return await SubmitJobAsync(payload, cancel);
    }

    /// <summary>Submit a keepalive job to keep the worker alive for the specified duration.
    /// This job runs a blocking ping loop on the worker. Returns the job ID for later cancellation.</summary>
    public async Task<string> SubmitKeepaliveAsync(int duration = 3600, int interval = 30, CancellationToken cancel = default)
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
        Logs.Debug($"[RunPodApiClient] Submitting keepalive job (duration: {duration}s, interval: {interval}s)");
        return await SubmitJobAsync(payload, cancel);
    }

    /// <summary>Make direct API call to SwarmUI instance running on worker via its public URL.
    /// This bypasses RunPod's job system entirely — no new jobs are created.</summary>
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
        Logs.Verbose($"[RunPodApiClient] SwarmUI direct call: POST {url} (timeout: {timeoutSeconds}s, bodySize: {requestBody.ToString().Length} chars)");
        using HttpResponseMessage response = await HttpClient.SendAsync(request, cancel);
        Logs.Verbose($"[RunPodApiClient] SwarmUI direct call response: {response.StatusCode} for {apiPath}");
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(cancel);
            Logs.Verbose($"[RunPodApiClient] SwarmUI direct call error body: {error}");
            throw new HttpRequestException($"SwarmUI API call failed ({response.StatusCode}): {error}");
        }
        string content = await response.Content.ReadAsStringAsync(cancel);
        Logs.Verbose($"[RunPodApiClient] SwarmUI direct call success: {apiPath} ({content.Length} chars)");
        JObject result = JObject.Parse(content);
        RunPodServerlessBackend.AutoThrowException(result);
        return result;
    }
}
