using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

[DisallowMultipleComponent]
public class PmxModelRenderProfile : MonoBehaviour
{
    public bool applyBloom;
    public float bloomIntensity = 0.08f;
    public Color bloomColor = new(1f, 0.895f, 0.85f, 1f);

    public bool applyColorGrading;
    public float contrast = 18f;
    public float saturation = 8f;
    public float postExposure = 0f;
    public Color colorFilter = Color.white;
    public Tonemapper tonemapper = Tonemapper.ACES;

    private readonly List<VolumeState> states = new();
    private bool applied;

    private sealed class VolumeState
    {
        public PostProcessVolume volume;
        public GameObject gameObject;
        public bool activeSelf;
        public bool createdVolume;
        public bool hadInstantiatedProfile;
        public PostProcessProfile originalSharedProfile;
        public PostProcessProfile originalInternalProfile;
        public PostProcessProfile runtimeProfile;
    }

    private void OnEnable()
    {
        Apply();
    }

    private void OnDisable()
    {
        Restore();
    }

    private void OnDestroy()
    {
        Restore();
    }

    public void Apply()
    {
        if (applied || (!applyBloom && !applyColorGrading)) return;

        var volumes = Resources.FindObjectsOfTypeAll<PostProcessVolume>();
        foreach (var volume in volumes)
        {
            if (volume == null || volume.hideFlags != HideFlags.None) continue;
            if (!volume.gameObject.scene.IsValid()) continue;
            var profile = volume.sharedProfile != null ? volume.sharedProfile : volume.profile;
            if (profile == null) continue;
            if (!ShouldUseVolume(profile)) continue;

            var state = new VolumeState
            {
                volume = volume,
                gameObject = volume.gameObject,
                activeSelf = volume.gameObject.activeSelf,
                hadInstantiatedProfile = volume.HasInstantiatedProfile(),
                originalSharedProfile = volume.sharedProfile,
                originalInternalProfile = volume.HasInstantiatedProfile() ? volume.profile : null
            };
            state.runtimeProfile = Instantiate(profile);
            state.runtimeProfile.name = profile.name + " (PMX Runtime)";
            volume.sharedProfile = state.runtimeProfile;
            volume.gameObject.SetActive(true);
            ApplyToProfile(state.runtimeProfile);
            states.Add(state);
        }

        if (states.Count == 0)
            CreateRuntimeVolume();

        applied = states.Count > 0;
    }

    public void Restore()
    {
        if (!applied) return;

        foreach (var state in states)
        {
            if (state.createdVolume)
            {
                if (state.gameObject != null)
                    Destroy(state.gameObject);
                if (state.runtimeProfile != null)
                    Destroy(state.runtimeProfile);
                continue;
            }

            if (state.volume == null) continue;
            if (state.hadInstantiatedProfile)
                state.volume.profile = state.originalInternalProfile;
            else
                state.volume.sharedProfile = state.originalSharedProfile;
            if (state.gameObject != null)
                state.gameObject.SetActive(state.activeSelf);
            if (state.runtimeProfile != null)
                Destroy(state.runtimeProfile);
        }

        states.Clear();
        applied = false;
    }

    private void CreateRuntimeVolume()
    {
        var go = new GameObject("PMX Runtime PostProcess");
        go.transform.SetParent(transform, false);

        var volume = go.AddComponent<PostProcessVolume>();
        volume.isGlobal = true;
        volume.priority = 1000f;
        volume.weight = 1f;

        var profile = ScriptableObject.CreateInstance<PostProcessProfile>();
        profile.name = "PMX Runtime Profile";
        volume.sharedProfile = profile;
        ApplyToProfile(profile);

        states.Add(new VolumeState
        {
            volume = volume,
            gameObject = go,
            activeSelf = true,
            createdVolume = true,
            runtimeProfile = profile
        });
    }

    private bool ShouldUseVolume(PostProcessProfile profile)
    {
        if (profile == null) return false;
        if (applyBloom && profile.TryGetSettings(out Bloom _)) return true;
        if (applyColorGrading && profile.TryGetSettings(out ColorGrading _)) return true;
        return false;
    }

    private void ApplyToProfile(PostProcessProfile profile)
    {
        if (applyBloom)
        {
            if (!profile.TryGetSettings(out Bloom bloom))
                bloom = profile.AddSettings<Bloom>();
            bloom.active = true;
            bloom.intensity.Override(bloomIntensity);
            bloom.color.Override(bloomColor);
        }

        if (applyColorGrading)
        {
            if (!profile.TryGetSettings(out ColorGrading colorGrading))
                colorGrading = profile.AddSettings<ColorGrading>();
            colorGrading.active = true;
            colorGrading.tonemapper.Override(tonemapper);
            colorGrading.contrast.Override(contrast);
            colorGrading.saturation.Override(saturation);
            colorGrading.postExposure.Override(postExposure);
            colorGrading.colorFilter.Override(colorFilter);
        }
    }
}
