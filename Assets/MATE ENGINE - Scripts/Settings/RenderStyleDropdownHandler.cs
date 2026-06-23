using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Binds a TMP_Dropdown to the runtime render-style system. Mirrors the
/// <see cref="LanguageDropdownHandler"/> pattern: populate options from the
/// manager's built-in style table, reflect the persisted choice without firing,
/// and on change call <see cref="RenderStyleManager.SetStyle"/> (which persists).
/// </summary>
public class RenderStyleDropdownHandler : MonoBehaviour
{
    [Tooltip("Dropdowns that should reflect / drive the selected render style")]
    [SerializeField] private List<TMP_Dropdown> dropdowns = new List<TMP_Dropdown>();

    private bool isInitializing = true;

    private void Start()
    {
        var manager = RenderStyleManager.EnsureInstance();

        string saved = SaveLoadHandler.Instance != null
            ? SaveLoadHandler.Instance.data.renderStyle
            : RenderStyleManager.DefaultCode;
        int index = manager.IndexOf(saved);

        var labels = new List<string>();
        foreach (var style in manager.Styles)
            labels.Add(string.IsNullOrEmpty(style.displayName) ? style.code : style.displayName);

        foreach (var dropdown in dropdowns)
        {
            if (dropdown == null) continue;
            dropdown.ClearOptions();
            dropdown.AddOptions(labels);
            dropdown.SetValueWithoutNotify(index);
            dropdown.RefreshShownValue();
            dropdown.onValueChanged.AddListener(OnRenderStyleChanged);
        }

        isInitializing = false;
    }

    private void OnRenderStyleChanged(int index)
    {
        if (isInitializing) return;

        var manager = RenderStyleManager.Instance;
        if (manager == null) return;
        if (index < 0 || index >= manager.Styles.Count) return;

        // Keep sibling dropdowns in sync.
        foreach (var dropdown in dropdowns)
            if (dropdown != null && dropdown.value != index)
                dropdown.SetValueWithoutNotify(index);

        manager.SetStyle(manager.Styles[index].code);
    }
}
