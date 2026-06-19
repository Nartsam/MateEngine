using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class LoadingScreenController : MonoBehaviour
{
    [Header("Assets")]
    [SerializeField] private Sprite backgroundSprite;
    [SerializeField] private Sprite logoSprite;
    [SerializeField] private TMP_FontAsset fontAsset;

    [Header("Loading")]
    [SerializeField] private string mainScenePath = "Assets/MATE ENGINE - Scenes/Mate Engine Main.unity";
    [SerializeField] private float minimumDisplaySeconds = 0.6f;

    private RectTransform progressFill;
    private TextMeshProUGUI statusText;

    private void Awake()
    {
        CreateLoadingUi();
    }

    private IEnumerator Start()
    {
        yield return null;

        AsyncOperation loadOperation = SceneManager.LoadSceneAsync(mainScenePath);
        if (loadOperation == null)
        {
            if (statusText != null) statusText.text = "加载失败";
            yield break;
        }

        loadOperation.allowSceneActivation = false;
        float startedAt = Time.realtimeSinceStartup;

        while (loadOperation.progress < 0.9f || Time.realtimeSinceStartup - startedAt < minimumDisplaySeconds)
        {
            float normalizedProgress = Mathf.Clamp01(loadOperation.progress / 0.9f);
            SetProgress(normalizedProgress);
            yield return null;
        }

        SetProgress(1f);
        loadOperation.allowSceneActivation = true;
    }

    private void CreateLoadingUi()
    {
        GameObject canvasObject = new GameObject("Loading Canvas", typeof(RectTransform));
        canvasObject.transform.SetParent(transform, false);

        Canvas canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1000;

        CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        canvasObject.AddComponent<GraphicRaycaster>();

        CreateImage("Background", canvasObject.transform, backgroundSprite, Color.white, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

        Image logo = CreateImage("Logo", canvasObject.transform, logoSprite, Color.white, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 80f), new Vector2(820f, 190f));
        if (logo != null) logo.preserveAspect = true;

        statusText = CreateText(canvasObject.transform);
        CreateProgressBar(canvasObject.transform);
        SetProgress(0f);
    }

    private Image CreateImage(string objectName, Transform parent, Sprite sprite, Color color, Vector2 anchorMin, Vector2 anchorMax, Vector2 anchoredPosition, Vector2 sizeDelta)
    {
        GameObject imageObject = new GameObject(objectName, typeof(RectTransform));
        imageObject.transform.SetParent(parent, false);

        RectTransform rect = imageObject.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = sizeDelta;

        Image image = imageObject.AddComponent<Image>();
        image.sprite = sprite;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private TextMeshProUGUI CreateText(Transform parent)
    {
        GameObject textObject = new GameObject("Status Text", typeof(RectTransform));
        textObject.transform.SetParent(parent, false);

        RectTransform rect = textObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = new Vector2(0f, -85f);
        rect.sizeDelta = new Vector2(420f, 70f);

        TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
        if (fontAsset != null) text.font = fontAsset;
        text.text = "加载中...";
        text.alignment = TextAlignmentOptions.Center;
        text.fontSize = 30f;
        text.color = Color.white;
        text.raycastTarget = false;
        return text;
    }

    private void CreateProgressBar(Transform parent)
    {
        GameObject barObject = new GameObject("Progress Bar", typeof(RectTransform));
        barObject.transform.SetParent(parent, false);

        RectTransform barRect = barObject.GetComponent<RectTransform>();
        barRect.anchorMin = new Vector2(0.5f, 0.5f);
        barRect.anchorMax = new Vector2(0.5f, 0.5f);
        barRect.anchoredPosition = new Vector2(0f, -150f);
        barRect.sizeDelta = new Vector2(520f, 8f);

        Image barBackground = barObject.AddComponent<Image>();
        barBackground.color = new Color(1f, 1f, 1f, 0.25f);
        barBackground.raycastTarget = false;

        GameObject fillObject = new GameObject("Fill", typeof(RectTransform));
        fillObject.transform.SetParent(barObject.transform, false);

        progressFill = fillObject.GetComponent<RectTransform>();
        progressFill.anchorMin = Vector2.zero;
        progressFill.anchorMax = new Vector2(0f, 1f);
        progressFill.offsetMin = Vector2.zero;
        progressFill.offsetMax = Vector2.zero;

        Image fillImage = fillObject.AddComponent<Image>();
        fillImage.color = new Color(1f, 1f, 1f, 0.9f);
        fillImage.raycastTarget = false;
    }

    private void SetProgress(float value)
    {
        if (progressFill == null) return;
        progressFill.anchorMax = new Vector2(Mathf.Clamp01(value), 1f);
    }
}
