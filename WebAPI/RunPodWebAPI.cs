using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Hartsy.Extensions.RunPodServerless.WebAPI;

/// <summary>Web API routes for the RunPod Serverless backend extension.</summary>
public static class RunPodWebAPI
{
    /// <summary>Register all RunPod Web API routes.</summary>
    public static void Register()
    {
        API.RegisterAPICall(RefreshModels, true, RunPodPermissions.PermUseRunPod);
        API.RegisterAPICall(GetStatus, false, RunPodPermissions.PermUseRunPod);
        Logs.Verbose("[RunPodWebAPI] Registered API routes: RefreshModels, GetStatus");
    }

    /// <summary>Manually refresh models from worker for all running RunPod backends.</summary>
    public static async Task<JObject> RefreshModels(Session session)
    {
        Logs.Verbose($"[RunPodWebAPI] RefreshModels API called by session {session?.ID}, user={session?.User?.UserID ?? "<null>"}");
        try
        {
            IEnumerable<RunPodServerlessBackend> backends = Program.Backends.RunningBackendsOfType<RunPodServerlessBackend>();
            int refreshed = 0;
            int failed = 0;
            if (!backends.Any())
            {
                Logs.Verbose("[RunPodWebAPI] RefreshModels: no RunPod backends are currently running");
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = "No RunPod backends are currently running"
                };
            }
            Logs.Verbose($"[RunPodWebAPI] RefreshModels: found {backends.Count()} running RunPod backend(s)");
            foreach (RunPodServerlessBackend backend in backends)
            {
                try
                {
                    Logs.Debug($"[RunPodWebAPI] Refreshing models for backend #{backend.BackendData.ID} ({backend.Title})...");
                    Logs.Verbose($"[RunPodWebAPI] Backend #{backend.BackendData.ID}: endpoint={backend.Config.EndpointId}, status={backend.Status}, currentModels={backend.Models?.Values.Sum(list => list.Count) ?? 0}");
                    await backend.RefreshModelsFromWorkerAsync(session);
                    int modelCount = backend.Models?.Values.Sum(list => list.Count) ?? 0;
                    int remoteCount = backend.RemoteModels?.Values.Sum(dict => dict.Count) ?? 0;
                    Logs.Debug($"[RunPodWebAPI] Backend #{backend.BackendData.ID} now has {modelCount} models");
                    Logs.Verbose($"[RunPodWebAPI] Backend #{backend.BackendData.ID}: {modelCount} models, {remoteCount} remote metadata entries, subtypes: {string.Join(", ", backend.Models?.Keys ?? [])}");
                    refreshed++;
                }
                catch (Exception ex)
                {
                    Logs.Error($"[RunPodWebAPI] Failed to refresh backend #{backend.BackendData.ID}: {ex.Message}");
                    Logs.Verbose($"[RunPodWebAPI] Refresh failure details for backend #{backend.BackendData.ID}: {ex}");
                    failed++;
                }
            }
            Logs.Verbose($"[RunPodWebAPI] RefreshModels complete: {refreshed} refreshed, {failed} failed");
            return new JObject
            {
                ["success"] = true,
                ["refreshed"] = refreshed,
                ["failed"] = failed,
                ["message"] = $"Refreshed {refreshed} RunPod backend(s), {failed} failed."
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[RunPodWebAPI] Error in RefreshModels: {ex.ReadableString()}");
            return new JObject
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }

    /// <summary>Get status information for all RunPod backends.</summary>
    public static async Task<JObject> GetStatus(Session session)
    {
        Logs.Verbose($"[RunPodWebAPI] GetStatus API called by session {session?.ID}");
        try
        {
            IEnumerable<RunPodServerlessBackend> backends = Program.Backends.RunningBackendsOfType<RunPodServerlessBackend>();
            JArray backendStatuses = [];
            foreach (RunPodServerlessBackend backend in backends)
            {
                int modelCount = backend.Models?.Values.Sum(list => list.Count) ?? 0;
                int remoteCount = backend.RemoteModels?.Values.Sum(dict => dict.Count) ?? 0;
                Logs.Verbose($"[RunPodWebAPI] GetStatus: backend #{backend.BackendData.ID} ({backend.Title}): status={backend.Status}, models={modelCount}, remoteModels={remoteCount}, worker={backend.CurrentWorker?.WorkerId ?? "<none>"}, keepaliveJob={backend.ActiveKeepaliveJobId ?? "<none>"}");
                backendStatuses.Add(new JObject
                {
                    ["id"] = backend.BackendData.ID,
                    ["title"] = backend.Title,
                    ["status"] = backend.Status.ToString(),
                    ["endpoint_id"] = backend.Config.EndpointId,
                    ["model_count"] = modelCount,
                    ["auto_refresh"] = backend.Config.AutoRefresh,
                    ["max_concurrent"] = backend.Config.MaxConcurrent
                });
            }
            Logs.Verbose($"[RunPodWebAPI] GetStatus: returning {backendStatuses.Count} backend status(es)");
            return new JObject
            {
                ["success"] = true,
                ["backends"] = backendStatuses,
                ["total_backends"] = backendStatuses.Count
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[RunPodWebAPI] Error in GetStatus: {ex.ReadableString()}");
            return new JObject
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }
}
