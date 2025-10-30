using Newtonsoft.Json.Linq;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Hartsy.Extensions.RunPodServerless.Utils;

/// <summary>Maps Swarm T2IParamInput to SwarmUI API format for direct worker calls.</summary>
public static class ParameterMapper
{
    /// <summary>Convert T2IParamInput to SwarmUI /API/GenerateText2Image format.</summary>
    public static JObject MapToSwarmUIRequest(T2IParamInput input, string sessionId)
    {
        var request = new JObject
        {
            ["session_id"] = sessionId,
            ["images"] = input.Get(T2IParamTypes.Images, 1),
            ["prompt"] = input.Get(T2IParamTypes.Prompt, ""),
            ["negativeprompt"] = input.Get(T2IParamTypes.NegativePrompt, ""),
            ["model"] = input.Get(T2IParamTypes.Model)?.Name ?? "",
            ["width"] = input.Get(T2IParamTypes.Width, 1024),
            ["height"] = input.Get(T2IParamTypes.Height, 1024),
            ["steps"] = input.Get(T2IParamTypes.Steps, 20),
            ["cfgscale"] = input.Get(T2IParamTypes.CFGScale, 7.0),
            ["seed"] = input.Get(T2IParamTypes.Seed, -1L)
        };

        // Add VAE if specified
        if (input.TryGet(T2IParamTypes.VAE, out T2IModel vaeModel) && vaeModel != null)
        {
            request["vae"] = vaeModel.Name;
        }

        // Add init image if specified (img2img)
        if (input.TryGet(T2IParamTypes.InitImage, out Image initImg) && initImg?.ImageData != null)
        {
            request["initimage"] = Convert.ToBase64String(initImg.ImageData);
            request["creativity"] = input.Get(T2IParamTypes.InitImageCreativity, 0.6);
        }

        // Add LoRAs if specified
        if (input.TryGet(T2IParamTypes.Loras, out List<string> loraList) && loraList != null && loraList.Count > 0)
        {
            request["loras"] = JArray.FromObject(loraList);
        }

        // Note: Sampler, Scheduler, and ControlNet parameters may be registered by backend-specific extensions
        // The SwarmUI worker will use its own defaults if not specified

        Logs.Verbose($"Mapped request: model={request["model"]}, size={request["width"]}x{request["height"]}, steps={request["steps"]}");
        
        return request;
    }

    /// <summary>Extract images from SwarmUI response.</summary>
    public static List<Image> ExtractImagesFromResponse(JObject response)
    {
        var images = new List<Image>();

        // SwarmUI returns images in the "images" array
        var imagesArray = response["images"] as JArray;
        if (imagesArray != null)
        {
            foreach (var imageToken in imagesArray)
            {
                string imageData = imageToken.ToString();
                
                // Handle both base64 and data URI formats
                if (imageData.StartsWith("data:image"))
                {
                    // Extract base64 from data URI
                    int commaIndex = imageData.IndexOf(',');
                    if (commaIndex >= 0)
                    {
                        imageData = imageData.Substring(commaIndex + 1);
                    }
                }

                try
                {
                    byte[] imageBytes = Convert.FromBase64String(imageData);
                    images.Add(new Image(imageBytes, Image.ImageType.IMAGE, "png"));
                }
                catch (Exception ex)
                {
                    Logs.Error($"Failed to decode image: {ex.Message}");
                }
            }
        }

        return images;
    }
}
