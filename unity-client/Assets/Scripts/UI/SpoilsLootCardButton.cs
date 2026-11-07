using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Scoundrel
{
    public class SpoilsLootCardButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerMoveHandler
    {
        public Button Button;
        public RectTransform CardRect;
        public RectTransform ShadowRect;
        public Image BackgroundImage;
        public Image BorderImage;
        public Image IconImage;
        public TextMeshProUGUI TitleText;
        public TextMeshProUGUI DescriptionText;
        public TextMeshProUGUI TagText;

        private Material _foilInstance;
        private bool _foilActive;
        private static readonly int TiltProp = Shader.PropertyToID("_Tilt");
        private static readonly int FoilModeProp = Shader.PropertyToID("_FoilMode");
        private static readonly int FoilIntensityProp = Shader.PropertyToID("_FoilIntensity");

        public void SetFoil(Material baseFoilMat, bool active, bool legendaryGold)
        {
            _foilActive = active && baseFoilMat != null && IconImage != null;
            if (!_foilActive)
            {
                if (IconImage != null) IconImage.material = null;
                return;
            }

            if (_foilInstance == null)
            {
                _foilInstance = new Material(baseFoilMat);
            }
            _foilInstance.SetFloat(FoilModeProp, legendaryGold ? 1f : 0f);
            _foilInstance.SetFloat(FoilIntensityProp, legendaryGold ? 1.05f : 0.85f);
            IconImage.material = _foilInstance;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (CardRect == null || (Button != null && !Button.interactable)) return;
            CardRect.DOKill();
            CardRect.DOScale(1.06f, 0.16f).SetEase(Ease.OutBack);
            WeaponSkinFXController.Instance?.PlayCardHover();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (CardRect == null) return;
            CardRect.DOKill();
            CardRect.DOScale(1f, 0.18f).SetEase(Ease.OutQuad);
            CardRect.DOLocalRotate(Vector3.zero, 0.18f).SetEase(Ease.OutQuad);
            if (ShadowRect != null)
            {
                ShadowRect.DOAnchorPos(new Vector2(6f, -10f), 0.18f);
            }
            if (_foilActive && _foilInstance != null)
            {
                _foilInstance.SetVector(TiltProp, Vector4.zero);
            }
        }

        public void OnPointerMove(PointerEventData eventData)
        {
            if (CardRect == null || (Button != null && !Button.interactable)) return;
            Canvas canvas = CardRect.GetComponentInParent<Canvas>();
            Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? canvas.worldCamera : null;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(CardRect, eventData.position, cam, out Vector2 localPt))
            {
                Rect rect = CardRect.rect;
                float nx = Mathf.Clamp(localPt.x / Mathf.Max(1f, rect.width * 0.5f), -1f, 1f);
                float ny = Mathf.Clamp(localPt.y / Mathf.Max(1f, rect.height * 0.5f), -1f, 1f);
                CardRect.localRotation = Quaternion.Euler(ny * 10f, -nx * 10f, 0f);
                if (ShadowRect != null)
                {
                    ShadowRect.anchoredPosition = new Vector2(6f - nx * 10f, -10f - ny * 10f);
                }
                if (_foilActive && _foilInstance != null)
                {
                    _foilInstance.SetVector(TiltProp, new Vector4(nx, ny, 0f, 0f));
                }
            }
        }

        private void OnDestroy()
        {
            if (CardRect != null) CardRect.DOKill();
            if (ShadowRect != null) ShadowRect.DOKill();
            if (_foilInstance != null)
            {
                Destroy(_foilInstance);
                _foilInstance = null;
            }
        }
    }
}
