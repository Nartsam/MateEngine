using UnityEngine;

/// <summary>
/// Camera image effect that applies the render-style color grade
/// (Hidden/RenderStyleGrade) via OnRenderImage. Lives on the main camera;
/// added/driven at runtime by <see cref="RenderStyleManager"/>.
///
/// When inactive (or no material) it is a pure passthrough, so the "默认" style
/// is byte-identical to having no effect at all. The grade shader preserves the
/// source alpha, so this never breaks the transparent layered window.
/// </summary>
[DisallowMultipleComponent]
public class RenderStyleImageEffect : MonoBehaviour
{
    public Material material;
    public bool active;

    private void OnRenderImage(RenderTexture src, RenderTexture dst)
    {
        if (active && material != null)
            Graphics.Blit(src, dst, material);
        else
            Graphics.Blit(src, dst);
    }
}
