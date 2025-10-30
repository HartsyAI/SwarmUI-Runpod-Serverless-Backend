using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.WebAPI;
using Microsoft.AspNetCore.Html;

namespace Hartsy.Extensions.RunPodServerless;

/// <summary>Permissions for the RunPod Serverless extension.</summary>
public static class RunPodPermissions
{
    public static readonly PermInfoGroup RunPodPermGroup = new("RunPodServerless",
        "Permissions related to RunPod serverless GPU backends.");

    public static readonly PermInfo PermUseRunPod = Permissions.Register(new("use_runpod_serverless", "Use RunPod Serverless",
        "Allows using RunPod's serverless GPU endpoints for image generation.",
        PermissionDefault.POWERUSERS, RunPodPermGroup));
}

// NOTE: Classname must match filename.
public class RunPodServerlessExtension : Extension
{
    public override void OnPreInit()
    {
        Logs.Init("Initializing RunPod Serverless Backend (scaffold)...");
        // TODO: If/when you add client assets, register here, e.g.:
        // ScriptFiles.Add("Assets/runpod.js");
    }

    public override void OnInit()
    {
        // Register the backend type so it appears in Server -> Backends Add Dropdown.
        Program.Backends.RegisterBackendType<RunPodServerlessBackend>(
            "runpod_serverless",
            "RunPod Serverless",
            "Serverless GPU inference via RunPod with S3-based model discovery.",
            CanLoadFast: true
        );

        // Register RunPod API key type so it can be managed in User Settings
        BasicAPIFeatures.AcceptedAPIKeyTypes.Add("runpod_api");
        try
        {
            if (!UserUpstreamApiKeys.KeysByType.ContainsKey("runpod_api"))
            {
                UserUpstreamApiKeys.Register(new(
                    KeyType: "runpod_api",
                    JSPrefix: "runpod",
                    Title: "RunPod",
                    CreateLink: "https://www.runpod.io/console/user/settings",
                    InfoHtml: new HtmlString("To use RunPod Serverless with SwarmUI, set your RunPod API key here.")
                ));
                Logs.Verbose("Registered RunPod API key type.");
            }
            else
            {
                Logs.Verbose("RunPod API key type already registered, skipping.");
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"Failed to register RunPod API key type: {ex.Message}");
        }

        // Register REST API endpoints
        RunPodWebAPI.Register();
        
        Logs.Info("RunPod Serverless Backend extension loaded.");
    }
}
