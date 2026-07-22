using UnityEngine;

namespace Merge2048.Presentation
{
    public sealed class SafeAreaFitter : MonoBehaviour
    {
        private RectTransform _rectTransform;
        private Rect _lastSafeArea;
        private Vector2Int _lastScreenSize;

        private void Awake()
        {
            _rectTransform = transform as RectTransform;
            ApplySafeArea();
        }

        private void Update()
        {
            if (_rectTransform == null)
            {
                return;
            }

            bool safeAreaChanged = Screen.safeArea != _lastSafeArea;
            bool screenSizeChanged = Screen.width != _lastScreenSize.x || Screen.height != _lastScreenSize.y;

            if (safeAreaChanged || screenSizeChanged)
            {
                ApplySafeArea();
            }
        }

        private void ApplySafeArea()
        {
            if (_rectTransform == null || Screen.width <= 0 || Screen.height <= 0)
            {
                return;
            }

            var safeArea = Screen.safeArea;
            _lastSafeArea = safeArea;
            _lastScreenSize = new Vector2Int(Screen.width, Screen.height);

            var anchorMin = safeArea.position;
            var anchorMax = safeArea.position + safeArea.size;

            anchorMin.x /= Screen.width;
            anchorMin.y /= Screen.height;
            anchorMax.x /= Screen.width;
            anchorMax.y /= Screen.height;

            _rectTransform.anchorMin = anchorMin;
            _rectTransform.anchorMax = anchorMax;
            _rectTransform.offsetMin = Vector2.zero;
            _rectTransform.offsetMax = Vector2.zero;
        }
    }
}
