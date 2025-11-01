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
        JObject request = new JObject
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
            string base64Image = Convert.ToBase64String(initImg.ImageData);
            request["initimage"] = $"data:image/png;base64,{base64Image}";
            request["creativity"] = input.Get(T2IParamTypes.InitImageCreativity, 0.6);
        }

        // Add LoRAs if specified
        if (input.TryGet(T2IParamTypes.Loras, out List<string> loraList) && loraList != null && loraList.Count > 0)
        {
            request["loras"] = JArray.FromObject(loraList);
        }

        // Add sampler if specified
        if (input.TryGet(T2IParamTypes.Sampler, out string sampler) && !string.IsNullOrWhiteSpace(sampler))
        {
            request["sampler"] = sampler;
        }

        // Add scheduler if specified
        if (input.TryGet(T2IParamTypes.Scheduler, out string scheduler) && !string.IsNullOrWhiteSpace(scheduler))
        {
            request["scheduler"] = scheduler;
        }

        Logs.Verbose($"[ParameterMapper] Mapped request: model={request["model"]}, size={request["width"]}x{request["height"]}, steps={request["steps"]}, seed={request["seed"]}");

        return request;
    }

    /// <summary>Extract images from SwarmUI response.</summary>
    public static List<Image> ExtractImagesFromResponse(JObject response)
    {
        List<Image> images = new List<Image>();

        // SwarmUI returns images in the "images" array
        JArray imagesArray = response["images"] as JArray;
        if (imagesArray == null)
        {
            Logs.Warning("[ParameterMapper] Response does not contain 'images' array");
            return images;
        }

        foreach (JToken imageToken in imagesArray)
        {
            string imageData = imageToken.ToString();

            try
            {
                byte[] imageBytes = null;

                // Check if it's a base64 data URI
                if (imageData.StartsWith("data:image"))
                {
                    // Extract base64 from data URI: "data:image/png;base64,XXXXX"
                    int commaIndex = imageData.IndexOf(',');
                    if (commaIndex >= 0 && commaIndex < imageData.Length - 1)
                    {
                        string base64Data = imageData.Substring(commaIndex + 1);
                        imageBytes = Convert.FromBase64String(base64Data);
                    }
                    else
                    {
                        Logs.Warning("[ParameterMapper] Invalid data URI format (no comma found)");
                        continue;
                    }
                }
                // Check if it's a file path (SwarmUI sometimes returns paths)
                else if (imageData.StartsWith("Output/") || imageData.Contains("/"))
                {
                    // This is a file path on the remote worker - we can't access it directly
                    // The worker should return base64 data, not paths
                    Logs.Warning($"[ParameterMapper] Received file path instead of image data: {imageData}");
                    Logs.Warning("[ParameterMapper] Worker should return base64-encoded images, not file paths");
                    continue;
                }
                // Assume it's raw base64
                else
                {
                    imageBytes = Convert.FromBase64String(imageData);
                }

                if (imageBytes != null && imageBytes.Length > 0)
                {
                    images.Add(new Image(imageBytes, Image.ImageType.IMAGE, "png"));
                    Logs.Verbose($"[ParameterMapper] Successfully decoded image ({imageBytes.Length} bytes)");
                }
            }
            catch (FormatException ex)
            {
                Logs.Error($"[ParameterMapper] Failed to decode base64 image data: {ex.Message}");
            }
            catch (Exception ex)
            {
                Logs.Error($"[ParameterMapper] Failed to process image: {ex.Message}");
            }
        }

        if (images.Count == 0)
        {
            Logs.Warning("[ParameterMapper] No valid images extracted from response");
        }

        return images;
    }
}
