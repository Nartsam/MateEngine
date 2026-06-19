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

        // 调整 VLG 的 Bottom padding，让 CSF PreferredSize 自然计算出正确的 Content 高度
        // 优势：与布局系统协同工作，不产生 SetSizeWithCurrentAnchors 带来的偏移
        VerticalLayoutGroup vlg = content.GetComponent<VerticalLayoutGroup>();
        if (vlg != null)
        {
            // VLG preferred = padding.top + sum(active RectTransform children heights) + spacing + padding.bottom
            // Content height = VLG preferred (via CSF PreferredSize)
            // So: Bottom = height - padding.top - sum(child heights)
            float activeChildHeight = 0f;
            for (int i = 0; i < content.childCount; i++)
            {
                RectTransform child = content.GetChild(i) as RectTransform;
                if (child != null && child.gameObject.activeInHierarchy)
                    activeChildHeight += child.rect.height;
            }

            int newBottom = Mathf.Max(0, Mathf.RoundToInt(height - vlg.padding.top - activeChildHeight));
            if (vlg.padding.bottom != newBottom)
            {
                vlg.padding = new RectOffset(
                    vlg.padding.left,
                    vlg.padding.right,
                    vlg.padding.top,
                    newBottom
                );
                // 触发布局重建，让 CSF 应用新的 VLG Bottom
                Canvas.ForceUpdateCanvases();
            }
        }

        scrollRect.movementType = ScrollRect.MovementType.Clamped;
        scrollRect.StopMovement();
        scrollRect.verticalNormalizedPosition = 1f;
    }
}
