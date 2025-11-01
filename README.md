# RunPod Serverless Backend for SwarmUI

**Serverless GPU inference for SwarmUI powered by RunPod**

Generate images using on-demand RunPod GPUs that scale automatically and only charge for actual usage. No dedicated servers required—workers start on-demand, generate images, and shut down automatically to minimize costs.

---

## 📋 Table of Contents

- [What Is This?](#what-is-this)
- [Why Use RunPod Serverless?](#why-use-runpod-serverless)
- [Prerequisites](#prerequisites)
- [Installation Guide](#installation-guide)
  - [Step 1: Deploy RunPod Worker](#step-1-deploy-runpod-worker)
  - [Step 2: Install SwarmUI Extension](#step-2-install-swarmui-extension)
  - [Step 3: Configure API Key](#step-3-configure-api-key)
  - [Step 4: Add Backend](#step-4-add-backend)
  - [Step 5: First Generation](#step-5-first-generation)
- [How It Works](#how-it-works)
- [Usage Patterns](#usage-patterns)
- [Cost Management](#cost-management)
- [Troubleshooting](#troubleshooting)
- [Advanced Configuration](#advanced-configuration)
- [API Documentation](#api-documentation)
- [Support](#support)

---

## What Is This?

This extension connects your local SwarmUI installation to **RunPod's serverless GPU infrastructure**. Instead of running models on your own hardware, you can:

- ✅ Generate images using powerful GPUs (RTX 4090, A100) on-demand
- ✅ Only pay for the seconds your GPU is actually running
- ✅ Auto-scale: workers start when you generate, shut down when idle
- ✅ Access GPUs you don't own (great for testing Flux, SDXL, etc.)
- ✅ No server management—RunPod handles all infrastructure

**Think of it as:** Uber for GPUs. Request a GPU when you need it, it arrives, does your work, and leaves. You only pay for the ride.

---

## Why Use RunPod Serverless?

### Perfect For:

| Use Case | Why RunPod Serverless? |
|----------|------------------------|
| **Testing new models** | Try Flux, SDXL, or custom models without downloading GBs |
| **Occasional generation** | Pay-per-use beats paying for idle GPU time |
| **No local GPU** | Generate high-quality images without expensive hardware |
| **Overflow capacity** | Use local GPU normally, overflow to RunPod during heavy workloads |
| **Travel/mobility** | Generate from any device—phone, tablet, laptop |

### Cost Examples (RTX 4090):

| Task | Duration | Cost |
|------|----------|------|
| Single 1024x1024 image | ~40 seconds | ~$0.01 |
| Batch of 10 images | ~5 minutes | ~$0.07 |
| Hour of generation | 60 minutes | ~$0.89 |
| 100 images/month | ~70 minutes | ~$1.05 |

**Compare to:** Renting a dedicated RTX 4090 24/7 = ~$650/month

---

## Prerequisites

Before starting, you'll need:

### Required:

1. **SwarmUI installed locally**
   - Download: https://github.com/mcmonkeyprojects/SwarmUI
   - This extension works with SwarmUI 0.6.5-Beta or newer

2. **RunPod account**
   - Sign up: https://runpod.io
   - Add payment method (credit card required)
   - Load initial credits ($10-25 recommended for testing)

3. **RunPod worker deployed**
   - See Step 1 below for deployment guide
   - One-time setup, takes ~30 minutes

### Optional:

- Models uploaded to RunPod storage (or use default models)
- Understanding of SwarmUI backends (helpful but not required)

### Estimated Time:

- **First-time setup:** 45-60 minutes
- **Subsequent usage:** < 2 minutes to start generating

---

## Installation Guide

Follow these steps in order for a smooth setup experience.

---

### Step 1: Deploy RunPod Worker

Before installing the extension, you need a RunPod serverless worker running SwarmUI.

#### 1.1: Create RunPod Account

1. Go to https://runpod.io
2. Click **"Sign Up"** and create account
3. Verify your email address
4. Go to **Billing** → Add payment method
5. Load initial credits (minimum $10, recommend $25 for testing)

💡 **Cost estimate:** First-time setup will cost ~$0.20-0.50 (20-30 minutes on cheap GPU)

#### 1.2: Get Your API Key

1. Go to https://runpod.io/console/user/settings
2. Click **"+ API Key"** button
3. Name it: `swarmui-backend`
4. Click **"Create"**
5. **COPY THE KEY IMMEDIATELY** (you can only see it once!)
6. Save it somewhere safe (you'll need it in Step 3)

Example key format: `ABCDEF123456789...` (long alphanumeric string)

#### 1.3: Create Network Volume

Network volumes store your SwarmUI installation and models persistently across worker restarts.

1. Go to https://runpod.io/console/storage
2. Click **"+ New Network Volume"**
3. Configure:
   - **Name:** `swarmui-models`
   - **Size:** `100 GB` (minimum)
   - **Region:** Choose based on GPU availability:
     - `US-OR-1` (US West) - Good availability
     - `US-TX-3` (US Central) - Good availability
     - `EU-RO-1` (Europe) - For EU users
4. Click **"Create"**
5. Wait ~1 minute for volume to provision
6. **Note the Volume ID** (e.g., `abc123xyz`) - you'll need this

💰 **Cost:** ~$7/month for 100GB (charged per GB per month)

#### 1.4: Deploy Serverless Endpoint

1. Go to https://runpod.io/console/serverless
2. Click **"+ New Endpoint"**
3. Configure endpoint:

**Name & Image:**
- **Name:** `swarmui-worker`
- **Docker Image:** `your-dockerhub-user/swarmui-runpod:latest`
  - Or use public image if available
  - See worker documentation for building your own

**GPU Configuration:**

⚠️ **IMPORTANT:** For first-time setup, use a **cheap GPU**:
- **GPU Type:** Select **"RTX 4000 Ada (20GB)"** or **"RTX A4000 (16GB)"**
  - Cost: ~$0.39-0.45/hour
  - First installation takes 20-30 minutes
  - Total cost: ~$0.20 for initial setup

After setup is complete, you can change to production GPUs:
- **RTX 4090 (24GB)** - ~$0.89/hr - Great for SDXL
- **A100 40GB** - ~$1.89/hr - Great for Flux

**Scaling Configuration:**
- **Active Workers:** `0` (start on-demand)
- **Max Workers:** `3`
- **Idle Timeout:** `120` seconds
- **FlashBoot:** ✅ Enable (faster cold starts)

**Storage:**
- **Container Disk:** `15 GB`
- **Network Volume:** Select `swarmui-models` (from Step 1.3)

**Advanced Settings:**
- **Execution Timeout:** `3600` seconds
- **Request Timeout:** `3600` seconds

4. Click **"Deploy"**
5. Wait ~1 minute for deployment
6. **Copy your Endpoint ID** (shows in the endpoint list: `abc123xyz`)

💡 Endpoint URL format: `https://api.runpod.ai/v2/{ENDPOINT_ID}/runsync`

#### 1.5: First-Time Worker Installation

The first time your worker starts, it needs to install SwarmUI and ComfyUI. This is a **one-time process** that takes 20-30 minutes.

**Installation happens automatically when you first generate an image.** The extension will:
1. Start the worker
2. Wait for SwarmUI to install (~5 minutes)
3. You'll need to manually install ComfyUI in the UI (~15-20 minutes)
4. After this, future starts take only 60-90 seconds

We'll walk through this in Step 5 (First Generation).

✅ **You've completed RunPod setup!** Keep your API Key and Endpoint ID handy for the next steps.

---

### Step 2: Install SwarmUI Extension

Now we'll install this extension in your local SwarmUI.

#### 2.1: Open SwarmUI Extensions

1. **Launch SwarmUI** on your local machine
2. Open SwarmUI in your browser (usually http://localhost:7801)
3. Click the **☰ menu** (hamburger menu, top-left)
4. Click **"Server"** → **"Extensions"**

You should see the Extensions management page.

#### 2.2: Install from GitHub (Recommended)

1. In the Extensions page, find **"Install Extension from Git"** section
2. Enter the Git URL:
   ```
   https://github.com/yourusername/RunPodServerless
   ```
   *(Replace with actual repository URL)*

3. Click **"Install"**
4. Wait for installation (usually 5-10 seconds)
5. You should see a success message

#### 2.3: Alternative - Manual Installation

If Git installation doesn't work:

1. Download the extension as ZIP from GitHub
2. Extract the ZIP file
3. Find your SwarmUI `src/Extensions` folder:
   - Windows: `C:\Users\YourName\SwarmUI\src\Extensions`
   - Linux/Mac: `~/SwarmUI/src/Extensions`
4. Create a new folder: `src/Extensions/RunPodServerless`
5. Copy all extension files into this folder

#### 2.4: Restart SwarmUI

1. Stop SwarmUI (Ctrl+C in terminal, or close the launcher)
2. Start SwarmUI again
3. Open SwarmUI in browser
4. You should see in the console:
   ```
   [Init] Initializing RunPod Serverless Backend Extension...
   [Init] RunPod Serverless Backend extension loaded successfully.
   ```

✅ **Extension installed!** Now let's configure it.

---

### Step 3: Configure API Key

SwarmUI needs your RunPod API key to communicate with your workers.

#### 3.1: Open User Settings

1. In SwarmUI, click **☰ menu** (top-left)
2. Click **"User"** → **"User Settings"**
3. Scroll down to **"API Keys"** section

#### 3.2: Add RunPod API Key

1. Find **"RunPod"** in the API Keys list
2. Click **"Add Key"** or **"Configure"**
3. Paste your RunPod API key (from Step 1.2)
   - Example: `ABCDEF123456789...`
4. Click **"Save"** or **"Apply"**

💡 **Security Note:** API keys are stored securely and never exposed to the browser. Each user can have their own RunPod key for billing isolation.

#### 3.3: Verify Key Saved

1. Refresh the page
2. Go back to User Settings → API Keys
3. You should see **"RunPod"** with a checkmark ✅
4. The key itself will be hidden (shows as `••••••••`)

✅ **API key configured!** Now we can add the backend.

---

### Step 4: Add Backend

Now we'll add the RunPod backend to SwarmUI so you can use it for generation.

#### 4.1: Open Backend Settings

1. In SwarmUI, click **☰ menu** (top-left)
2. Click **"Server"** → **"Backends"**
3. You'll see a list of your current backends (if any)

#### 4.2: Add New Backend

1. Click **"Add New Backend"** button (usually at top or bottom)
2. In the **"Backend Type"** dropdown, select:
   ```
   RunPod Serverless
   ```
3. You should see the backend configuration form

#### 4.3: Configure Backend

Fill in the configuration:

**Basic Settings:**

- **Backend Name/Title:** `RunPod Production`
  - (Or any name you prefer—purely for your reference)

- **Endpoint ID:** `your-endpoint-id-from-step-1-4`
  - Paste the endpoint ID you copied in Step 1.4
  - Example: `abc123xyz456`

**API Key Options:**

You have two choices for API key:

**Option A: Use User Key (Recommended)**
- Leave **"RunPod API Key"** field **empty**
- Backend will use your personal API key from Step 3
- Each user's generations bill to their own RunPod account

**Option B: Use Backend Key**
- Enter a RunPod API key directly in **"RunPod API Key"** field
- All users share this backend key
- All generations bill to this single account
- Required for AutoRefresh (see below)

**Model Discovery:**

- **Auto Refresh:** ❌ **Disable for now**
  - We'll test manually first
  - Enable later once you verify everything works
  - Requires Backend API Key if enabled

**Performance Settings:**

Leave these at defaults for now:
- **Use Async:** ✅ Enabled
- **Max Concurrent:** `10`
- **Startup Timeout Sec:** `120`
- **Generation Timeout Sec:** `300`
- **Poll Interval Ms:** `2000`

#### 4.4: Save Backend

1. Click **"Save"** or **"Add Backend"** button
2. You should see the backend appear in your backend list
3. Status should show as **"Idle"** or **"Ready"**

#### 4.5: Verify Backend Added

1. In the Backends list, you should see:
   ```
   ✅ RunPod Production
   Type: RunPod Serverless
   Status: IDLE / RUNNING
   Models: 0 (will populate after first use)
   ```

✅ **Backend configured!** Almost there—let's generate your first image.

---

### Step 5: First Generation

Your first generation will trigger the one-time worker installation. This section walks you through the complete process.

#### 5.1: Enable Backend

1. Go back to the main generation interface (home page)
2. Look for **"Backend"** dropdown (usually near model selection)
3. Select **"RunPod Production"** (or whatever you named it)
4. Backend should activate

#### 5.2: Select Model

Initially, you won't see any models because the backend hasn't fetched them yet.

**Option A: Manually Refresh Models First (Recommended)**

1. Go to **Server** → **Backends**
2. Find your RunPod backend
3. Click **"Refresh Models"** or call the API:
   ```bash
   POST /API/RunPod/RefreshModels
   ```
4. Wait 2-3 minutes (worker starts, lists models, shuts down)
5. Refresh the page
6. Models should now appear in dropdown

**Option B: Generate Directly (Models Load Automatically)**

1. Just proceed with generation
2. Backend will fetch models on first use
3. This takes longer but works fine

**Select a Model:**
- Choose any model (e.g., `OfficialStableDiffusion/sd_xl_base_1.0`)
- For first test, use SDXL Base (fast and reliable)

#### 5.3: First Generation (Cold Start)

⏱️ **Expected Duration:** 2-3 minutes total
- Worker startup: 60-90 seconds
- ComfyUI backend load: 10 seconds (first gen only)
- Image generation: 30-40 seconds

**Steps:**

1. **Enter a simple prompt:**
   ```
   a beautiful mountain landscape at sunset, photorealistic
   ```

2. **Use fast settings for testing:**
   - Width: `512`
   - Height: `512`
   - Steps: `20`
   - CFG Scale: `7.5`

3. **Click "Generate"**

4. **Watch the status messages:**
   ```
   Starting generation on endpoint abc123...
   Waking up worker (keepalive: 180s)...
   Polling for worker ready...
   Worker ready after 72s
   Sending generation request to worker...
   Generated 1 image(s) successfully
   Sending shutdown signal to worker...
   ```

5. **Wait patiently!** First generation takes 2-3 minutes.

6. **Image appears!** 🎉

#### 5.4: What Just Happened?

Behind the scenes:

1. **Extension woke up RunPod worker**
   - RunPod started a GPU pod with SwarmUI
   - Worker connected to your network volume
   - SwarmUI initialized (or was already installed)

2. **Worker became ready**
   - SwarmUI API started responding
   - Extension got public URL (e.g., `https://xyz789-7801.proxy.runpod.net`)
   - Extension got session ID for API calls

3. **Generation happened on worker**
   - Extension sent your prompt/settings to worker's SwarmUI API
   - ComfyUI backend loaded (10 seconds, first time only)
   - Worker generated image using selected model
   - Image returned as base64 data

4. **Worker shut down automatically**
   - Extension sent shutdown signal
   - Worker will scale down after current keepalive expires
   - You only paid for ~2-3 minutes of GPU time (~$0.03)

#### 5.5: Second Generation (Warm Start)

Try another generation immediately:

⏱️ **Expected Duration:** 30-40 seconds
- Worker startup: ~5 seconds (still warm from first gen)
- ComfyUI load: 0 seconds (already loaded)
- Generation: 30-40 seconds

Much faster! This is because:
- Worker might still be alive (within 120 second idle timeout)
- Even if not, warm starts are faster than cold starts
- ComfyUI backend stays loaded across generations

✅ **Congratulations!** You've successfully generated images using RunPod serverless!

---

## How It Works

Understanding the system helps you use it effectively.

### Architecture Overview

```
┌─────────────────────┐
│   Your SwarmUI      │ ← You use this normally
│   (Local)           │
└──────────┬──────────┘
           │
           │ Extension connects your local SwarmUI
           │ to remote RunPod worker
           ↓
┌─────────────────────┐
│  RunPod Worker      │ ← Starts on-demand
│  - SwarmUI          │ ← Full SwarmUI running remotely
│  - ComfyUI          │ ← With ComfyUI backend
│  - Models           │ ← Your models on network volume
└─────────────────────┘
           │
           │ Uses GPU for actual generation
           ↓
┌─────────────────────┐
│  RTX 4090 / A100    │ ← Powerful GPU
│  (Serverless)       │ ← Only runs when needed
└─────────────────────┘
```

### Generation Lifecycle

1. **You click Generate in local SwarmUI**

2. **Extension wakes RunPod worker:**
   - Sends "wakeup" signal to RunPod endpoint
   - RunPod finds available GPU and starts container
   - SwarmUI initializes on worker
   - Extension polls until worker is ready

3. **Extension makes direct API calls:**
   - Gets worker's public URL (e.g., `https://xyz-7801.proxy.runpod.net`)
   - Creates SwarmUI session on worker
   - Sends generation parameters via SwarmUI's API
   - Worker's ComfyUI processes generation

4. **Worker returns image:**
   - Image generated on worker's GPU
   - Returned as base64 data
   - Extension converts to SwarmUI Image object
   - Displayed in your local UI

5. **Extension shuts down worker:**
   - Sends shutdown signal immediately
   - Worker scales down to save costs
   - Network volume persists (models, settings)

**Key Insight:** The extension makes your local SwarmUI act as a **frontend** to remote SwarmUI workers. You get the UI you know, powered by serverless GPUs.

### Model Storage

Models are stored on your RunPod network volume:

```
Network Volume (Persistent)
└── SwarmUI/
    ├── Models/
    │   ├── Stable-Diffusion/
    │   │   ├── sd_xl_base_1.0.safetensors
    │   │   └── flux1-dev-fp8.safetensors
    │   ├── LoRA/
    │   └── VAE/
    └── Output/
        └── (generated images)
```

Models persist across all worker startups—you only download them once.

---

## Usage Patterns

### Pattern 1: Occasional Generation

**Use case:** Generate a few images per day

**Best practice:**
- Use default settings (worker shuts down after each gen)
- Each generation: wake → generate → shutdown
- Cost: ~$0.01-0.03 per image
- No wasted idle time

**Example workflow:**
```
Morning: Generate 3 images → 2 minutes → $0.03
Afternoon: Generate 5 images → 4 minutes → $0.06
Evening: Generate 2 images → 1.5 minutes → $0.02
Daily total: ~$0.11
```

### Pattern 2: Batch Generation

**Use case:** Generate many images at once

**Best practice:**
- Generate multiple images in quick succession
- Worker stays warm between generations
- Use batch mode (multiple images per request)

**Example workflow:**
```
1. Generate first image (cold start: 2 min)
2. Generate 9 more images (warm: 30s each = 4.5 min)
Total: 6.5 minutes → $0.10 (vs $0.30 if cold starting each)
```

### Pattern 3: Interactive Session

**Use case:** Actively tweaking prompts, testing variations

**Best practice:**
- Make multiple generations while warm
- Worker idles out after 120s (default)
- Stay active to keep worker warm

**Example workflow:**
```
Generate → Review → Adjust → Generate → Review → Adjust
All generations hit warm worker (30-40s each)
Session length: 10 minutes → $0.15
```

### Pattern 4: Model Testing

**Use case:** Trying different models

**Best practice:**
- Manually refresh models first (one-time cost)
- Switch models and generate
- First generation with new model loads it (~10s)

**Example workflow:**
```
1. Refresh models (2 min, one-time)
2. Test SDXL (first gen: 40s, subsequent: 30s)
3. Switch to Flux (first gen: 50s, subsequent: 40s)
4. Switch to SD 1.5 (first gen: 30s, subsequent: 20s)
```

### Pattern 5: Burst Usage

**Use case:** Heavy generation for a project

**Best practice:**
- Consider keeping worker alive longer
- Use faster models (Flux Schnell, SDXL Turbo)
- Lower steps for speed

**Example workflow:**
```
Project needs 100 images:
- Use SDXL Turbo (4 steps)
- ~15 seconds per image
- Total: 25 minutes → $0.37
```

---

## Cost Management

### Understanding Costs

You pay for two things:

1. **GPU time** (per second when worker is running)
2. **Storage** (per GB per month for network volume)

**GPU Pricing (as of 2024):**

| GPU | VRAM | Cost/Hour | Best For |
|-----|------|-----------|----------|
| RTX 4000 Ada | 20GB | $0.39 | Initial setup, SD 1.5 |
| RTX 4090 | 24GB | $0.89 | SDXL, general use |
| A100 40GB | 40GB | $1.89 | Flux, large models |
| A100 80GB | 80GB | $3.19 | Massive models |

**Storage Pricing:**
- Network Volume: ~$0.07 per GB per month
- 100GB volume: ~$7/month

### Cost Optimization Tips

#### ✅ Optimize Generation Settings

**Faster = Cheaper:**
- Use fewer steps (20 instead of 50)
- Use smaller resolutions (512x512 vs 1024x1024)
- Use faster models (Flux Schnell vs Flux Dev)
- Use efficient samplers (DPM++ 2M Karras)

**Example savings:**
```
Slow: 1024x1024, 50 steps = 60s = $0.015
Fast: 512x512, 20 steps = 20s = $0.005
Savings: 66% less cost per image
```

#### ✅ Use Appropriate GPU

Don't use A100 for SDXL:
```
SDXL on A100: $1.89/hr = $0.032 per minute
SDXL on RTX 4090: $0.89/hr = $0.015 per minute
Savings: 53% cheaper on 4090 (and just as good!)
```

Match GPU to model:
- **SD 1.5, SDXL:** RTX 4090
- **Flux, Large LoRAs:** A100 40GB
- **Massive custom models:** A100 80GB

#### ✅ Batch Generations

Stay warm between generations:
```
Cold start each time: 10 gens × 2 min = 20 min = $0.30
Stay warm: 1 cold (2 min) + 9 warm (30s each) = 6.5 min = $0.10
Savings: 67% cheaper
```

#### ✅ Let Workers Shut Down

Extension automatically shuts down workers after generation. Don't override this unless you have a good reason!

```
Auto-shutdown: Pay for 2 minutes per gen
Manual keepalive: Pay for full keepalive duration
If you forget: Could pay for hours unnecessarily!
```

#### ✅ Use Model Refresh Wisely

Don't enable AutoRefresh unless needed:
```
AutoRefresh on startup: ~$0.02 each time SwarmUI restarts
Manual refresh: ~$0.02 when you need it
```

Only refresh when:
- You've added new models
- Models aren't showing up
- Not every single startup!

#### ✅ Monitor Usage

1. Check RunPod dashboard regularly:
   - https://runpod.io/console/serverless
   - View worker execution time
   - See current spend

2. Set spending limits:
   - RunPod Settings → Billing → Set budget alerts
   - Get notified before overspending

3. Check SwarmUI logs:
   - Look for shutdown confirmations
   - Verify workers aren't staying alive

### Monthly Cost Estimates

**Light usage (50 images/month):**
```
50 images × 40 seconds = 33 minutes
RTX 4090: 33 min × $0.015/min = $0.50
Storage: 100GB × $0.07 = $7.00
Total: ~$7.50/month
```

**Moderate usage (300 images/month):**
```
300 images × 35 seconds (mostly warm) = 175 minutes
RTX 4090: 175 min × $0.015/min = $2.63
Storage: 100GB × $0.07 = $7.00
Total: ~$9.63/month
```

**Heavy usage (1000 images/month):**
```
1000 images × 30 seconds (all warm) = 500 minutes
RTX 4090: 500 min × $0.015/min = $7.50
Storage: 150GB × $0.07 = $10.50
Total: ~$18/month
```

**Compare to alternatives:**
- Local RTX 4090: $1,600 upfront + electricity
- Dedicated RunPod RTX 4090: ~$650/month 24/7
- Serverless: Pay only for what you use!

---

## Troubleshooting

### Extension Not Appearing

**Symptoms:** Don't see "RunPod Serverless" in backend types

**Solutions:**

1. **Verify extension installed:**
   ```
   Check: SwarmUI/src/Extensions/RunPodServerless/
   Should contain: RunPodServerlessExtension.cs and other files
   ```

2. **Check console logs:**
   ```
   Look for: "RunPod Serverless Backend extension loaded successfully"
   If missing: Extension failed to load
   ```

3. **Restart SwarmUI:**
   - Stop SwarmUI completely
   - Start again
   - Extension loads on startup

4. **Check file permissions:**
   - Linux/Mac: Files should be readable
   - Windows: No special permissions needed

---

### Cannot Add Backend

**Symptoms:** "Add Backend" button doesn't work, form doesn't save

**Solutions:**

1. **Verify API key added:**
   - Go to User Settings → API Keys
   - Confirm RunPod key is present
   - Try re-entering key

2. **Check endpoint ID format:**
   - Should be alphanumeric string (e.g., `abc123xyz456`)
   - No spaces, no URLs, just the ID
   - Get from RunPod dashboard: Serverless → Your endpoint

3. **Check browser console:**
   - Press F12 → Console tab
   - Look for JavaScript errors
   - Report any errors in GitHub issues

4. **Try different browser:**
   - Some extensions can interfere
   - Try Chrome/Firefox incognito mode

---

### No Models Showing Up

**Symptoms:** Backend added but model dropdown is empty

**Solutions:**

1. **Manually refresh models:**
   - Go to Server → Backends
   - Find your RunPod backend
   - Click "Refresh Models" button
   - Or call API: `POST /API/RunPod/RefreshModels`
   - Wait 2-3 minutes

2. **Check worker has models:**
   - Verify models uploaded to network volume
   - Path: `/mnt/user-data/SwarmUI/Models/Stable-Diffusion/`
   - Use RunPod pods to browse volume

3. **Check backend status:**
   ```bash
   GET /API/RunPod/GetStatus
   ```
   Should show: `model_count > 0`

4. **Try generating anyway:**
   - Sometimes models load on first generation
   - Enter prompt and click Generate
   - Models may appear after first use

5. **Check logs:**
   ```
   Look for: "[RunPodServerless] Registered X models for subtype 'Stable-Diffusion'"
   If missing: Model refresh failed
   ```

---

### Worker Won't Start / Timeout Errors

**Symptoms:** "Worker did not become ready within 120 seconds"

**Solutions:**

1. **Increase timeout:**
   - Go to Server → Backends → Edit RunPod backend
   - Change "Startup Timeout Sec" to `180` or `240`
   - Cold starts can take 90+ seconds

2. **Check RunPod dashboard:**
   - Go to https://runpod.io/console/serverless
   - Check your endpoint status
   - Look for error messages

3. **Verify GPU availability:**
   - Sometimes GPUs are unavailable
   - Try different region (EU vs US)
   - Try different GPU type
   - Try different time of day

4. **Check network volume:**
   - Verify volume is attached to endpoint
   - Check volume has space (>15GB free)
   - Volume region must match GPU region

5. **Check worker logs:**
   - RunPod dashboard → Your endpoint → Logs
   - Look for startup errors
   - Worker should show: "SwarmUI ready"

---

### Generation Fails / Timeout

**Symptoms:** "Generation failed" or request times out

**Solutions:**

1. **Check generation timeout:**
   - Server → Backends → Edit RunPod backend
   - Increase "Generation Timeout Sec" to `600`
   - Complex generations take time

2. **Simplify generation:**
   - Reduce steps (20 instead of 50)
   - Reduce resolution (512 instead of 1024)
   - Use simpler model (SDXL Base instead of Flux)
   - Test if it's a timeout vs actual failure

3. **Check model compatibility:**
   - Some models need specific settings
   - Some models are broken/incompatible
   - Try official models first (SDXL Base)

4. **Check worker status:**
   - Worker might have crashed
   - Try refreshing models again
   - Restart backend (disable then re-enable)

5. **Check logs for details:**
   ```
   Look for specific error messages:
   - "Model not found" → Model isn't on worker
   - "Out of memory" → GPU too small for model
   - "Backend not ready" → ComfyUI didn't load
   ```

---

### "Permission Denied" Errors

**Symptoms:** "You do not have permission to use RunPod Serverless backends"

**Solutions:**

1. **Check user permissions:**
   - Go to User Settings
   - Look for RunPod permissions
   - Ensure "Use RunPod Serverless" is enabled

2. **Admin users only:**
   - By default, only power users can use RunPod
   - Admin can grant permission to other users
   - Server Settings → User Permissions

3. **Verify API key belongs to user:**
   - Each user needs their own RunPod API key
   - Or use shared backend API key
   - Can't mix user keys across accounts

---

### High Costs / Unexpected Charges

**Symptoms:** RunPod bill higher than expected

**Solutions:**

1. **Check for orphaned workers:**
   - RunPod dashboard → Active workers
   - Look for workers running for hours
   - Manually stop any stuck workers

2. **Verify auto-shutdown:**
   - Check SwarmUI logs for "Shutdown signal sent"
   - If missing: Extension might not be sending shutdown
   - Report as bug if consistent

3. **Check AutoRefresh settings:**
   - Disable AutoRefresh if enabled
   - Only refresh models manually when needed
   - AutoRefresh triggers on every SwarmUI restart

4. **Review generation settings:**
   - High steps = longer time = higher cost
   - Large resolutions = longer time = higher cost
   - Optimize settings for your needs

5. **Set budget alerts:**
   - RunPod → Settings → Billing
   - Set daily/weekly spend limits
   - Get notified before overspending

---

### "Invalid API Key" Errors

**Symptoms:** API key rejected by RunPod

**Solutions:**

1. **Verify key is correct:**
   - RunPod dashboard → Settings → API Keys
   - Regenerate key if unsure
   - Copy entire key (no spaces)

2. **Check key permissions:**
   - Key needs serverless permissions
   - Old keys might be disabled
   - Create new key if problems persist

3. **Re-enter key:**
   - SwarmUI → User Settings → API Keys
   - Delete old RunPod key
   - Add new key
   - Save and refresh

---

### ComfyUI Won't Install (First Time)

**Symptoms:** First generation hangs during "Installing ComfyUI"

**Known Issue:** ComfyUI installation sometimes hangs due to network issues.

**Solutions:**

1. **Wait longer:**
   - Installation can take 15-20 minutes
   - Check RunPod dashboard logs for progress
   - Look for download/install messages

2. **Set logs to Debug:**
   - In worker's SwarmUI UI (public URL)
   - Settings → Log Level → Debug
   - See exactly what's happening

3. **Restart worker:**
   - RunPod dashboard → Terminate worker
   - Try generation again
   - Usually works within 2-3 attempts

4. **Try different time/region:**
   - Network issues vary by time of day
   - Try different region (US-OR-1 vs US-TX-3)
   - More stable during off-peak hours

**This is a one-time issue** - once ComfyUI installs, it's stored on your network volume permanently.

---

## Advanced Configuration

### Backend Settings Explained

```csharp
EndpointId              // Your RunPod endpoint ID (required)
RunPodApiKey            // Backend API key (optional, for AutoRefresh)
AutoRefresh             // Auto-discover models on startup (false recommended)
UseAsync                // Use async endpoint (true recommended)
MaxConcurrent           // Max parallel requests (10 default)
StartupTimeoutSec       // Max worker startup wait (120 default)
GenerationTimeoutSec    // Max generation wait (300 default)
PollIntervalMs          // Status check interval (2000 default)
```

### Tuning for Performance

**Fast startup (light generations):**
```
StartupTimeoutSec: 90
GenerationTimeoutSec: 180
PollIntervalMs: 1000
```

**Slow startup (heavy generations):**
```
StartupTimeoutSec: 240
GenerationTimeoutSec: 600
PollIntervalMs: 3000
```

**High concurrency (batch jobs):**
```
MaxConcurrent: 20
UseAsync: true
```

### Multiple Backends

You can add multiple RunPod backends for different use cases:

**Example setup:**
```
Backend 1: "RunPod Fast" (RTX 4090, SDXL models)
Backend 2: "RunPod Slow" (A100, Flux models)
Backend 3: "RunPod Test" (RTX 4000 Ada, testing)
```

Each backend can have:
- Different endpoint IDs
- Different GPU types
- Different models
- Different settings

### Using Backend API Keys vs User Keys

**User Keys (Recommended):**
- Each user has their own RunPod account
- Each user's generations bill to their account
- Good for teams, billing isolation
- Can't use AutoRefresh

**Backend Keys (Advanced):**
- Single shared RunPod account for backend
- All users share billing
- Can use AutoRefresh
- Simpler for single-user setups

To use backend key:
1. Backend settings → "RunPod API Key"
2. Enter your key directly
3. Leave user key empty
4. Enable AutoRefresh if desired

---

## API Documentation

The extension provides REST endpoints for programmatic access.

### Manually Refresh Models

Trigger model refresh without restarting backend:

```http
POST /API/RunPod/RefreshModels
Authorization: Bearer <swarmui-session-token>
Content-Type: application/json

Response 200 OK:
{
  "success": true,
  "refreshed": 1,
  "failed": 0,
  "message": "Refreshed 1 RunPod backend(s), 0 failed."
}
```

**Use when:**
- You've uploaded new models to RunPod
- Models aren't showing up
- You want to force a refresh

**Cost:** ~$0.02 (2-3 minutes on RTX 4090)

### Get Backend Status

Check status of all RunPod backends:

```http
GET /API/RunPod/GetStatus
Authorization: Bearer <swarmui-session-token>

Response 200 OK:
{
  "success": true,
  "total_backends": 2,
  "backends": [
    {
      "id": 1,
      "title": "RunPod Production",
      "status": "RUNNING",
      "endpoint_id": "abc123",
      "model_count": 42,
      "auto_refresh": false,
      "max_concurrent": 10
    },
    {
      "id": 2,
      "title": "RunPod Test",
      "status": "IDLE",
      "endpoint_id": "xyz789",
      "model_count": 5,
      "auto_refresh": true,
      "max_concurrent": 5
    }
  ]
}
```

**Use for:**
- Monitoring backend health
- Checking model count
- Debugging issues

### Integration Examples

**Python:**
```python
import requests

# Refresh models
response = requests.post(
    "http://localhost:7801/API/RunPod/RefreshModels",
    headers={"Authorization": f"Bearer {session_token}"}
)
print(response.json())

# Get status
response = requests.get(
    "http://localhost:7801/API/RunPod/GetStatus",
    headers={"Authorization": f"Bearer {session_token}"}
)
print(response.json())
```

**JavaScript:**
```javascript
// Refresh models
fetch('/API/RunPod/RefreshModels', {
  method: 'POST',
  headers: {
    'Authorization': `Bearer ${sessionToken}`
  }
})
.then(r => r.json())
.then(data => console.log(data));

// Get status
fetch('/API/RunPod/GetStatus', {
  headers: {
    'Authorization': `Bearer ${sessionToken}`
  }
})
.then(r => r.json())
.then(data => console.log(data));
```

---

## Support

### Getting Help

**Before asking for help, gather this info:**

1. **SwarmUI version:**
   - Help → About → Version number

2. **Extension logs:**
   ```
   Look for lines with: [RunPodServerless], [RunPodApiClient], [ParameterMapper]
   Copy recent logs around your error
   ```

3. **Error messages:**
   - Full error text from UI
   - Any error codes
   - When error occurs (startup, generation, etc.)

4. **Configuration:**
   - GPU type selected in RunPod
   - Backend settings (timeout values, etc.)
   - AutoRefresh enabled or disabled

5. **Steps to reproduce:**
   - What you did before error
   - Can you reproduce consistently?

### Where to Get Help

**GitHub Issues (Best for bugs):**
- Repository: https://github.com/HartsyAI/RunPodServerless
- Search existing issues first
- Provide logs and configuration
- Tag with appropriate labels

**SwarmUI Discord:**
- SwarmUI Community: https://discord.gg/q2y38cqjNw
- Channel: #extensions or #help
- Good for usage questions

**Hartsy Discord**

**RunPod Discord:**
- RunPod Community: https://discord.gg/runpod
- Channel: #serverless
- Good for RunPod platform issues

### Common Questions

**Q: Can I use this with other backends simultaneously?**
A: Yes! RunPod backend works alongside local backends. Select RunPod when you want serverless, select local when you want local.

**Q: Can I access worker's SwarmUI directly?**
A: Yes! Public URL is available in logs (e.g., `https://xyz-7801.proxy.runpod.net`). Access while worker is alive.

**Q: How do I add custom models?**
A: Upload to your network volume's Models folder. Use temporary RunPod pod to transfer files. Models persist across workers.

---

## Roadmap

Planned features (not yet implemented):

- [ ] Direct ComfyUI workflow support
- [ ] Video generation support (AnimateDiff, SVD)
- [ ] ControlNet support
- [ ] Img2Img support
- [ ] Inpainting support
- [ ] Automatic model caching strategies
- [ ] Cost tracking and budgets per user
- [ ] Queue management for batch jobs
- [ ] Region selection in UI
- [ ] GPU type selection per generation

Want a feature? Open a GitHub issue or contribute!

---

## Contributing

Contributions welcome! Areas that need help:

- Testing on different operating systems
- Testing with various models
- Documentation improvements
- Bug fixes
- Feature implementations

**To contribute:**

1. Fork the repository
2. Create feature branch
3. Make changes with tests
4. Submit pull request
5. Describe changes clearly

See CONTRIBUTING.md (if exists) for detailed guidelines.

---

## License

This extension is licensed under the MIT License.

See LICENSE file for full terms.

**Key points:**
- ✅ Free to use personally and commercially
- ✅ Modify and redistribute freely
- ✅ No warranty provided
- ✅ Credit appreciated but not required

---

## Acknowledgments

**Built with:**
- [SwarmUI](https://github.com/mcmonkeyprojects/SwarmUI) by mcmonkey
- [RunPod](https://runpod.io) serverless infrastructure
- [ComfyUI](https://github.com/comfyanonymous/ComfyUI) backend

**Thanks to:**
- SwarmUI community for testing and feedback
- RunPod team for serverless platform
- All contributors and issue reporters

---

## Quick Start Summary

**5-minute checklist for returning users:**

- [ ] RunPod account funded
- [ ] Worker endpoint deployed
- [ ] Extension installed in SwarmUI
- [ ] API key configured
- [ ] Backend added
- [ ] Select backend → Select model → Generate!

**Typical generation flow:**
```
1. Open SwarmUI
2. Select "RunPod Production" backend
3. Choose model from dropdown
4. Enter prompt
5. Click Generate
6. Wait 30-90 seconds
7. Image appears!
```

**That's it!** Enjoy serverless GPU generation! 🚀

---

**Last Updated:** 2024
**Extension Version:** 1.0.0
**SwarmUI Compatibility:** 0.6.5-Beta and newer
