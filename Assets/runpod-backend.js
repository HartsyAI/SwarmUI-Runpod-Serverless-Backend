// RunPod Serverless Backend - Feature Handler
// This file manages UI feature flags and parameter visibility for RunPod backend

featureSetChangers.push(() => {
    if (!gen_param_types) {
        return [[], []];
    }

    const isRunPodBackend = currentModelHelper.curArch === 'runpod_serverless';

    const coreParamsToHideForAPI = [
        'vaetilesize', 'vaetileoverlap', 'automaticvae',
        'clipstopatlayer', 'modelspecificenhancements'
    ];

    for (let param of gen_param_types) {
        if (coreParamsToHideForAPI.includes(param.id)) {
            if (isRunPodBackend) {
                if (!param.hasOwnProperty('original_feature_flag_runpod')) {
                    param.original_feature_flag_runpod = param.feature_flag;
                }
                param.feature_flag = param.original_feature_flag_runpod
                    ? `${param.original_feature_flag_runpod},__runpod_incompatible__`
                    : '__runpod_incompatible__';
            } else if (param.hasOwnProperty('original_feature_flag_runpod')) {
                param.feature_flag = param.original_feature_flag_runpod;
                delete param.original_feature_flag_runpod;
            }
        }
    }

    if (!isRunPodBackend) {
        return [[], ['runpod_serverless']];
    }

    const removeFlags = [
        'sampling', 'refiners', 'controlnet', 'variation_seed',
        'video', 'autowebui', 'comfyui', 'frameinterps', 'ipadapter',
        'sdxl', 'cascade', 'sd3', 'seamless', 'freeu', 'teacache',
        'text2video', 'yolov8', 'aitemplate', 'endstepsearly',
        'dynamic_thresholding', 'zero_negative'
    ];

    // Features to add for RunPod backend
    const addFlags = ['runpod_serverless', 'prompt', 'images'];

    console.log(`[runpod-backend] Adding feature flags: ${addFlags.join(', ')}`);
    console.log(`[runpod-backend] Removing feature flags: ${removeFlags.join(', ')}`);

    return [addFlags, removeFlags];
});

if (typeof addModelChangeCallback === 'function') {
    addModelChangeCallback(() => {
        console.log(`[runpod-backend] Model changed to: ${currentModelHelper.curArch}`);

        // Update the feature set and parameter visibility
        reviseBackendFeatureSet();
        hideUnsupportableParams();
    });
}

// Initial parameter setup after UI loads
setTimeout(() => {
    console.log('[runpod-backend] Initial parameter setup starting');
    reviseBackendFeatureSet();
    hideUnsupportableParams();
}, 500);

console.log('[runpod-backend] Feature handler loaded');
