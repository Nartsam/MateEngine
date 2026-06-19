using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public class SettingsMenuScrollBoundsLimiter : MonoBehaviour
{
    [SerializeField] private ScrollRect scrollRect;
    [SerializeField] private RectTransform bottomElement;
    [SerializeField] private float bottomPadding = 0f;

    private void OnEnable()
    {
        Apply();
        StartCoroutine(ApplyNextFrame());
    }

    private IEnumerator ApplyNextFrame()
    {
        yield return null;
        Apply();
    }

    public void Apply()
    {
        if (scrollRect == null || scrollRect.content == null || bottomElement == null)
            return;

        Canvas.ForceUpdateCanvases();

        RectTransform content = scrollRect.content;
        Vector3[] corners = new Vector3[4];
        bottomElement.GetWorldCorners(corners);

        float minY = float.PositiveInfinity;
        for (int i = 0; i < corners.Length; i++)
        {
            Vector3 local = content.InverseTransformPoint(corners[i]);
            minY = Mathf.Min(minY, local.y);
        }

        float height = Mathf.Max(0f, -minY + bottomPadding);
        if (height <= 0f)
            return;

        if (Mathf.Abs(content.rect.height - height) > 0.5f)
            content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);

        scrollRect.movementType = ScrollRect.MovementType.Clamped;
        scrollRect.StopMovement();
        scrollRect.verticalNormalizedPosition = Mathf.Clamp01(scrollRect.verticalNormalizedPosition);
    }
}
