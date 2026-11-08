using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Scoundrel
{
    /// <summary>
    /// Cross-platform clipboard helper with WebGL + itch.io cross-origin iframe support.
    /// Browsers ignore GUIUtility.systemCopyBuffer on uGUI clicks and itch.io iframes do not grant
    /// Permissions-Policy: clipboard-write, so we arm a synchronous DOM user-gesture copy via
    /// document.execCommand('copy') on pointer hover/down and fall back to navigator.clipboard.
    /// </summary>
    public static class WebGLClipboard
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void ScoundrelClipboard_Arm(string text);

        [DllImport("__Internal")]
        private static extern void ScoundrelClipboard_Disarm();

        [DllImport("__Internal")]
        private static extern int ScoundrelClipboard_CopyNow(string text);
#endif

        public static void Arm(string text)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!string.IsNullOrEmpty(text))
            {
                ScoundrelClipboard_Arm(text);
            }
            else
            {
                ScoundrelClipboard_Disarm();
            }
#endif
        }

        public static void Disarm()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            ScoundrelClipboard_Disarm();
#endif
        }

        public static bool Copy(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            GUIUtility.systemCopyBuffer = text;

#if UNITY_WEBGL && !UNITY_EDITOR
            return ScoundrelClipboard_CopyNow(text) != 0;
#else
            return true;
#endif
        }
    }

    public sealed class CopyButtonGestureHook : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler
    {
        private Func<string> _textProvider;
        public bool IsHovered { get; private set; }

        public void Bind(Func<string> textProvider)
        {
            _textProvider = textProvider;
            if (IsHovered)
            {
                ArmCurrent();
            }
        }

        public void RefreshArmedText()
        {
            if (IsHovered)
            {
                ArmCurrent();
            }
        }

        private void ArmCurrent()
        {
            string text = _textProvider?.Invoke();
            WebGLClipboard.Arm(text);
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            IsHovered = true;
            ArmCurrent();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            ArmCurrent();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            IsHovered = false;
            WebGLClipboard.Disarm();
        }

        private void OnDisable()
        {
            IsHovered = false;
            WebGLClipboard.Disarm();
        }
    }
}
