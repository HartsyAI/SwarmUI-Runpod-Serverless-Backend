using System.Linq;
using System.Collections.Generic;
using FreneticUtilities.FreneticExtensions;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.WebAPI;
using Microsoft.AspNetCore.Html;
using Hartsy.Extensions.RunPodServerless.WebAPI;

namespace Hartsy.Extensions.RunPodServerless;

/// <summary>Permissions for the RunPod Serverless extension.</summary>
public static class RunPodPermissions
{
    public static readonly PermInfoGroup RunPodPermGroup = new("RunPodServerless", "Permissions related to RunPod serverless GPU backends.");
    public static readonly PermInfo PermUseRunPod = Permissions.Register(new PermInfo("use_runpod_serverless", "Use RunPod Serverless",
        "Allows using RunPod's serverless GPU endpoints for image generation.", PermissionDefault.POWERUSERS, RunPodPermGroup));
}

/// <summary>RunPod Serverless Backend Extension - Provides serverless GPU inference via RunPod.</summary>
public class RunPodServerlessExtension : Extension
{
    public override void OnPreInit()
    {
        Logs.Init("Initializing Hartsy's RunPod Serverless Backend Extension...");
    }

    public override void OnInit()
    {
        Program.Backends.RegisterBackendType<RunPodServerlessBackend>("runpod_serverless", "RunPod Serverless",
            "Serverless GPU inference via RunPod with direct SwarmUI API access. Supports on-demand scaling and cost-effective generation.",
            CanLoadFast: true);
        BasicAPIFeatures.AcceptedAPIKeyTypes.Add("runpod_api");
        try
        {
            if (!UserUpstreamApiKeys.KeysByType.ContainsKey("runpod_api"))
            {
                UserUpstreamApiKeys.Register(new UserUpstreamApiKeys.ApiKeyInfo(KeyType: "runpod_api", JSPrefix: "runpod", Title: "RunPod",
                    CreateLink: "https://www.runpod.io/console/user/settings",
                    InfoHtml: new HtmlString("Enter your RunPod API key to use RunPod Serverless backends. Get your API key from <a href='https://www.runpod.io/console/user/settings' target='_blank'>RunPod Settings</a>.")
                ));
                Logs.Debug("Registered RunPod API key type in user settings.");
            }
            else
            {
                Logs.Verbose("RunPod API key type already registered.");
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"Failed to register RunPod API key type: {ex.Message}");
        }
        // Register extra models provider so RunPod remote models appear in model listings (like SwarmSwarm backend)
        try
        {
            if (!ModelsAPI.ExtraModelProviders.ContainsKey("runpod_serverless"))
            {
                ModelsAPI.ExtraModelProviders["runpod_serverless"] = (string subtype) =>
                {
                    RunPodServerlessBackend[] backs = [.. Program.Backends.RunningBackendsOfType<RunPodServerlessBackend>().Where(b => b.RemoteModels is not null)];
                    IEnumerable<Dictionary<string, Newtonsoft.Json.Linq.JObject>> sets = backs.Select(b => b.RemoteModels.GetValueOrDefault(subtype)).Where(s => s is not null);
                    if (!sets.Any())
                    {
                        return [];
                    }
                    return sets.Aggregate((a, b) => a.Union(b).PairsToDictionary(false));
                };
                Logs.Debug("Registered RunPod Serverless models provider for extra remote models.");
            }
            else
            {
                Logs.Verbose("RunPod Serverless models provider already registered.");
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"Failed to register RunPod extra models provider: {ex.Message}");
        }
        RunPodWebAPI.Register();
        Logs.Info("RunPod Serverless Backend extension loaded successfully.");
    }
}
