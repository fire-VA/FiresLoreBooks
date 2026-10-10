using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FiresPictures
{
    /// <summary>
    /// One inline picture on a page (mockup 3: a white print with its caption, slightly turned). In the editor (mockup 2) it also
    /// gets handles: drag the picture to move it, a corner to resize, the top knob to rotate, ✂ toggles crop mode (drag an edge
    /// to crop that side), ✕ deletes it. Every edit changes the ImgSpec; changed(spec) runs when the drag ends.
    /// </summary>
    public sealed class PictureFrame : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        private const float Border = 8f, CaptionHeight = 26f, HandleSize = 14f;
        private static readonly Color Paper = new Color(0.98f, 0.97f, 0.93f), HandleColor = new Color(0.25f, 0.45f, 0.75f);
        private static readonly Dictionary<string, float> s_aspect = new Dictionary<string, float>();   // src → height / width

        private RectTransform _area, _rt;
        private RawImage _photo;
        private ImgSpec _spec;
        private Action<ImgSpec> _changed, _deleted;
        private bool _editable, _cropMode;
        private Vector2 _grab;
        private readonly List<GameObject> _cropHandles = new List<GameObject>();

        /// <summary>The picture's height as a fraction of the area's WIDTH (its cropped aspect; 0.75 until it has loaded).</summary>
        internal static float HeightFraction(ImgSpec s)
        {
            float aspect = s_aspect.TryGetValue(s.Src, out float a) ? a : 0.75f;
            return s.W * aspect * (s.Crop.height / Mathf.Max(0.02f, s.Crop.width));
        }

        internal static float CaptionSpace(ImgSpec s) => Border * 2f + (string.IsNullOrEmpty(s.Caption) ? 0f : CaptionHeight);

        internal static PictureFrame Create(RectTransform area, string folder, ImgSpec spec, bool editable, TMP_FontAsset font,
            Action<ImgSpec> changed, Action<ImgSpec> deleted)
        {
            var go = new GameObject("Picture " + spec.Src, typeof(RectTransform), typeof(Image), typeof(PictureFrame));
            go.transform.SetParent(area, false);
            var frame = go.GetComponent<PictureFrame>();
            frame._area = area;
            frame._rt = (RectTransform)go.transform;
            frame._spec = spec;
            frame._editable = editable;
            frame._changed = changed;
            frame._deleted = deleted;
            go.GetComponent<Image>().color = Paper;
            go.GetComponent<Image>().raycastTarget = editable;

            var photoGo = new GameObject("Photo", typeof(RectTransform), typeof(RawImage));
            photoGo.transform.SetParent(go.transform, false);
            frame._photo = photoGo.GetComponent<RawImage>();
            frame._photo.raycastTarget = false;
            frame._photo.color = new Color(0.82f, 0.8f, 0.74f);   // a blank print until the picture arrives

            if (!string.IsNullOrEmpty(spec.Caption))
            {
                var capGo = new GameObject("Caption", typeof(RectTransform), typeof(TextMeshProUGUI));
                capGo.transform.SetParent(go.transform, false);
                var cap = capGo.GetComponent<TextMeshProUGUI>();
                if (font != null) cap.font = font;
                cap.text = spec.Caption;
                cap.fontSize = 18f;
                cap.color = new Color(0.25f, 0.18f, 0.1f);
                cap.alignment = TextAlignmentOptions.Center;
                cap.raycastTarget = false;
                var crt = (RectTransform)capGo.transform;
                crt.anchorMin = new Vector2(0f, 0f); crt.anchorMax = new Vector2(1f, 0f); crt.pivot = new Vector2(0.5f, 0f);
                crt.sizeDelta = new Vector2(0f, CaptionHeight); crt.anchoredPosition = new Vector2(0f, Border * 0.5f);
            }

            if (editable) frame.AddHandles();
            frame.Layout();
            Store.Get(folder, spec.Src, tex =>
            {
                if (frame == null || tex == null) return;
                s_aspect[spec.Src] = tex.height / (float)Mathf.Max(1, tex.width);
                frame._photo.texture = tex;
                frame._photo.color = Color.white;
                frame.Layout();
                FiresLoreBooks.FiresLoreBooksPlugin.Log?.LogDebug($"[Pictures] shown {folder}/{spec.Src} {tex.width}x{tex.height} at x {spec.X:0.00} y {spec.Y:0.00} w {spec.W:0.00} wrap {(spec.WrapAround ? "around" : "past")}");
            });
            return frame;
        }

        /// <summary>Places and sizes the frame from its spec (top-left at x, y of the area; turned about its centre).</summary>
        internal void Layout()
        {
            float areaW = _area.rect.width, areaH = _area.rect.height;
            float photoW = _spec.W * areaW, photoH = HeightFraction(_spec) * areaW;
            float w = photoW + Border * 2f, h = photoH + CaptionSpace(_spec);
            _rt.anchorMin = _rt.anchorMax = new Vector2(0f, 1f);
            _rt.pivot = new Vector2(0.5f, 0.5f);
            _rt.sizeDelta = new Vector2(w, h);
            _rt.anchoredPosition = new Vector2(_spec.X * areaW + w * 0.5f, -(_spec.Y * areaH + h * 0.5f));
            _rt.localEulerAngles = new Vector3(0f, 0f, -_spec.Rot);
            var prt = (RectTransform)_photo.transform;
            prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 1f);
            prt.pivot = new Vector2(0.5f, 1f);
            prt.sizeDelta = new Vector2(photoW, photoH);
            prt.anchoredPosition = new Vector2(0f, -Border);
            _photo.uvRect = _spec.Crop;
        }

        // ── handles ──

        private enum Kind { Resize, Rotate, CropLeft, CropRight, CropTop, CropBottom }

        private void AddHandles()
        {
            foreach (Vector2 corner in new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) })
                MakeHandle(Kind.Resize, corner, Vector2.zero, "");
            MakeHandle(Kind.Rotate, new Vector2(0.5f, 1f), new Vector2(0f, 22f), "");
            MakeButton("✂", new Vector2(1f, 1f), new Vector2(18f, 18f), () => { _cropMode = !_cropMode; foreach (var c in _cropHandles) c.SetActive(_cropMode); });
            MakeButton("✕", new Vector2(0f, 1f), new Vector2(-18f, 18f), () => { FiresLoreBooks.FiresLoreBooksPlugin.Log?.LogDebug($"[Pictures] deleted {_spec.Src} from the page"); _deleted?.Invoke(_spec); });
            _cropHandles.Add(MakeHandle(Kind.CropLeft, new Vector2(0f, 0.5f), Vector2.zero, ""));
            _cropHandles.Add(MakeHandle(Kind.CropRight, new Vector2(1f, 0.5f), Vector2.zero, ""));
            _cropHandles.Add(MakeHandle(Kind.CropTop, new Vector2(0.5f, 1f), new Vector2(0f, -Border), ""));
            _cropHandles.Add(MakeHandle(Kind.CropBottom, new Vector2(0.5f, 0f), Vector2.zero, ""));
            foreach (var c in _cropHandles) { c.GetComponent<Image>().color = new Color(0.85f, 0.35f, 0.2f); c.SetActive(false); }
        }

        private GameObject MakeHandle(Kind kind, Vector2 anchor, Vector2 offset, string label)
        {
            var go = new GameObject("Handle " + kind, typeof(RectTransform), typeof(Image), typeof(Handle));
            go.transform.SetParent(transform, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = anchor;
            rt.sizeDelta = new Vector2(HandleSize, HandleSize);
            rt.anchoredPosition = offset;
            go.GetComponent<Image>().color = HandleColor;
            var h = go.GetComponent<Handle>();
            h.Owner = this;
            h.Kind = kind;
            return go;
        }

        private void MakeButton(string label, Vector2 anchor, Vector2 offset, Action click)
        {
            var go = new GameObject("Button " + label, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(transform, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = anchor;
            rt.sizeDelta = new Vector2(24f, 24f);
            rt.anchoredPosition = offset;
            go.GetComponent<Image>().color = new Color(0.23f, 0.17f, 0.1f, 0.9f);
            go.GetComponent<Button>().onClick.AddListener(() => click());
            var txt = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            txt.transform.SetParent(go.transform, false);
            var t = txt.GetComponent<TextMeshProUGUI>();
            t.text = label; t.fontSize = 16f; t.alignment = TextAlignmentOptions.Center; t.color = new Color(0.95f, 0.9f, 0.78f);
            t.raycastTarget = false;
            ((RectTransform)txt.transform).sizeDelta = new Vector2(24f, 24f);
        }

        private Vector2 Local(PointerEventData e)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_area, e.position, e.pressEventCamera, out Vector2 p);
            return new Vector2(p.x - _area.rect.xMin, _area.rect.yMax - p.y);   // from the area's top-left, y down
        }

        // moving: the whole frame
        public void OnBeginDrag(PointerEventData e)
        {
            if (!_editable) return;
            Vector2 p = Local(e);
            _grab = new Vector2(p.x - _spec.X * _area.rect.width, p.y - _spec.Y * _area.rect.height);
        }

        public void OnDrag(PointerEventData e)
        {
            if (!_editable) return;
            Vector2 p = Local(e) - _grab;
            _spec.X = Mathf.Clamp(p.x / _area.rect.width, -0.2f, 1f - _spec.W * 0.5f);
            _spec.Y = Mathf.Clamp(p.y / _area.rect.height, -0.1f, 0.95f);
            Layout();
        }

        public void OnEndDrag(PointerEventData e) { if (_editable) Edited("move"); }

        internal void Edited(string how)
        {
            FiresLoreBooks.FiresLoreBooksPlugin.Log?.LogDebug($"[Pictures] edited {_spec.Src} ({how}): {PageLayout.Write(_spec)}");
            _changed?.Invoke(_spec);
        }

        private void HandleDrag(Kind kind, PointerEventData e)
        {
            Vector2 p = Local(e);
            float areaW = _area.rect.width, areaH = _area.rect.height;
            Vector2 centre = new Vector2(_spec.X * areaW + _rt.sizeDelta.x * 0.5f, _spec.Y * areaH + _rt.sizeDelta.y * 0.5f);
            switch (kind)
            {
                case Kind.Resize:
                    // the half-width from the centre to the pointer, ignoring the turn; the centre stays put
                    float half = Mathf.Abs(p.x - centre.x) - Border;
                    _spec.W = Mathf.Clamp(half * 2f / areaW, 0.1f, 1f);
                    _spec.X = (centre.x - (_spec.W * areaW + Border * 2f) * 0.5f) / areaW;
                    _spec.Y = (centre.y - (HeightFraction(_spec) * areaW + CaptionSpace(_spec)) * 0.5f) / areaH;
                    break;
                case Kind.Rotate:
                    _spec.Rot = Mathf.Round(Mathf.Atan2(p.x - centre.x, centre.y - p.y) * Mathf.Rad2Deg);
                    if (Mathf.Abs(_spec.Rot) < 2f) _spec.Rot = 0f;   // a small snap back to straight
                    break;
                default:
                    Crop(kind, e.delta);
                    break;
            }
            Layout();
        }

        // an edge dragged by delta screen pixels moves that side of the crop by the same share of the shown picture
        private void Crop(Kind kind, Vector2 delta)
        {
            var prt = (RectTransform)_photo.transform;
            float du = delta.x / Mathf.Max(1f, prt.rect.width) * _spec.Crop.width;
            float dv = delta.y / Mathf.Max(1f, prt.rect.height) * _spec.Crop.height;
            Rect c = _spec.Crop;
            const float min = 0.1f;
            if (kind == Kind.CropLeft) c.xMin = Mathf.Clamp(c.xMin + du, 0f, c.xMax - min);
            else if (kind == Kind.CropRight) c.xMax = Mathf.Clamp(c.xMax + du, c.xMin + min, 1f);
            else if (kind == Kind.CropTop) c.yMax = Mathf.Clamp(c.yMax + dv, c.yMin + min, 1f);
            else c.yMin = Mathf.Clamp(c.yMin + dv, 0f, c.yMax - min);
            _spec.Crop = c;
        }

        private sealed class Handle : MonoBehaviour, IDragHandler, IEndDragHandler, IBeginDragHandler
        {
            internal PictureFrame Owner;
            internal Kind Kind;
            public void OnBeginDrag(PointerEventData e) { }
            public void OnDrag(PointerEventData e) => Owner.HandleDrag(Kind, e);
            public void OnEndDrag(PointerEventData e) => Owner.Edited(Kind.ToString().ToLowerInvariant());
        }
    }
}
