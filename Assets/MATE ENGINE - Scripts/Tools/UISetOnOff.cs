using UnityEngine;
using System.Collections.Generic;

public class UISetOnOff : MonoBehaviour
{
    public GameObject target;
    public void ToggleTarget()
    {
        if (target != null)
            target.SetActive(!target.activeSelf);
    }
    public void SetOnOff(GameObject obj)
    {
        if (obj != null)
            obj.SetActive(!obj.activeSelf);
    }

    public void ToggleAccessoryByName(string ruleName)
    {
        foreach (var handler in AccessoiresHandler.ActiveHandlers)
        {
            foreach (var rule in handler.rules)
            {
                if (rule.ruleName == ruleName)
                {
                    rule.isEnabled = !rule.isEnabled;
                    break;
                }
            }
        }
    }
    public void ToggleBubbleFeature()
    {
        foreach (var handler in AvatarBubbleHandler.ActiveHandlers)
            handler.ToggleBubbleFromUI();
    }
    public void UnsnapAllAvatars()
    {
        foreach (var h in FindObjectsByType<AvatarWindowHandler>(FindObjectsSortMode.None))
            h.ForceExitWindowSitting();
    }


    public void SetAccessoryState(string ruleName, bool state)
    {
        foreach (var handler in AccessoiresHandler.ActiveHandlers)
        {
            foreach (var rule in handler.rules)
            {
                if (rule.ruleName == ruleName)
                {
                    rule.isEnabled = state;
                    break;
                }
            }
        }
    }
    public void ToggleBigScreenFeature()
    {
        foreach (var handler in AvatarBigScreenHandler.ActiveHandlers)
            handler.ToggleBigScreenFromUI();
    }

    public void ToggleChibiMode()
    {
        // Chibi mode is intentionally disabled in this fork.
    }

    public void CloseApp()
    {
        CloseSettingsPanel();
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        RemoveTaskbarApp.HideMainWindow();
#endif
    }

    private void CloseSettingsPanel()
    {
        // "SettingsMenuCanvas" 是整个设置面板的根容器，默认 inactive，打开设置时被
        // 整体 SetActive(true)（场景中打开入口正是 SetOnOff(SettingsMenuCanvas)）。
        // 点击 X 时设置面板可见，意味着该根容器此刻必然处于 active，因此 GameObject.Find
        // （只能找到 active 对象）必定能定位到它。直接关掉它即可隐藏整个面板（含所有子面板），
        // 且不影响下次打开——打开逻辑会重新激活它。名称在场景中唯一。
        var canvas = GameObject.Find("SettingsMenuCanvas");
        if (canvas != null)
            canvas.SetActive(false);
    }
    public void OpenWebsite(string url)
    {
        if (!string.IsNullOrEmpty(url))
        {
            Application.OpenURL(url);
        }
    }
}
