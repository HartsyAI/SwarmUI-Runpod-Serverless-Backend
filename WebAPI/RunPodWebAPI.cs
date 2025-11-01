using FreneticUtilities.FreneticExtensions;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Hartsy.Extensions.RunPodServerless;

/// <summary>Web API routes for the RunPod Serverless backend extension.</summary>
public static class RunPodWebAPI
{
    /// <summary>Register all RunPod Web API routes.</summary>
    public static void Register()
    {
        // POST /API/RunPod/RefreshModels - Trigger a manual model refresh
        API.RegisterAPICall(RefreshModels, true, RunPodPermissions.PermUseRunPod);

        // GET /API/RunPod/GetStatus - Get current status of all RunPod backends
        API.RegisterAPICall(GetStatus, false, RunPodPermissions.PermUseRunPod);

        Logs.Verbose("[RunPodWebAPI] Registered API routes: RefreshModels, GetStatus");
    }

    /// <summary>Manually refresh models from worker for all running RunPod backends.</summary>
    public static async Task<JObject> RefreshModels(Session session)
    {
        try
        {
            IEnumerable<RunPodServerlessBackend> backends = Program.Backends.RunningBackendsOfType<RunPodServerlessBackend>();
            int refreshed = 0;
            int failed = 0;

            if (!backends.Any())
            {
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = "No RunPod backends are currently running"
                };
            }

            foreach (RunPodServerlessBackend backend in backends)
            {
                try
                {
                    Logs.Info($"[RunPodWebAPI] Refreshing models for backend #{backend.BackendData.ID} ({backend.Title})...");
                    await backend.RefreshModelsFromWorkerAsync(session);

                    int modelCount = backend.Models?.Values.Sum(list => list.Count) ?? 0;
                    Logs.Info($"[RunPodWebAPI] Backend #{backend.BackendData.ID} now has {modelCount} models");

                    refreshed++;
                }
                catch (Exception ex)
                {
                    Logs.Error($"[RunPodWebAPI] Failed to refresh backend #{backend.BackendData.ID}: {ex.Message}");
                    failed++;
                }
            }

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
        try
        {
            IEnumerable<RunPodServerlessBackend> backends = Program.Backends.RunningBackendsOfType<RunPodServerlessBackend>();
            JArray backendStatuses = new JArray();

            foreach (RunPodServerlessBackend backend in backends)
            {
                int modelCount = backend.Models?.Values.Sum(list => list.Count) ?? 0;

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
