using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using PluginConfig.API;
using PluginConfig.API.Decorators;
using PluginConfig.API.Fields;
using PluginConfig.API.Functionals;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace DVDLogoMod
{
    [BepInPlugin("com.lazurut.dvdlogomod", "DVD_Logo_Mod", "1.0.3")]
    public class DVDLogoPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        private const string GifName = "DVD_VIDEO_logo.gif";
        private const string PngName = "DVD_VIDEO_logo.png";
        private const int MaxRecent = 5;

        private readonly List<string> _recent = new List<string>();
        private readonly ConfigHeader[] _slotNames = new ConfigHeader[MaxRecent];
        private readonly ButtonField[] _slotButtons = new ButtonField[MaxRecent];

        private ConfigEntry<string> _recentCfg;
        private ConfigEntry<string> _currentCfg;
        private string _current;

        private DVDBounce _bounce;
        private KeyCodeField _enlargeKey;
        private KeyCodeField _colorLockKey;
        private BoolField[] _colorToggles;

        private void Awake()
        {
            Log = Logger;
            Logger.LogInfo("DVD Logo Mod -- loaded!");

            _recentCfg = Config.Bind("Logo", "Recent", "", "");
            _currentCfg = Config.Bind("Logo", "Current", "", "");
            _current = _currentCfg.Value;
            _recent.AddRange(_recentCfg.Value.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries));

            StartCoroutine(InitUI());
        }

        private IEnumerator InitUI()
        {
            yield return null;
            CreateDVDCanvas();
            CreateConfig();
        }

        private void Update()
        {
            if (_bounce == null || _colorToggles == null)
                return;

            _bounce.enlargeKey = _enlargeKey.value;
            _bounce.colorLockKey = _colorLockKey.value;

            for (int i = 0; i < _colorToggles.Length; i++)
                _bounce.colorEnabled[i] = _colorToggles[i].value;
        }

        private void CreateConfig()
        {
            PluginConfigurator config = PluginConfigurator.Create("DVD Logo", "com.lazurut.dvdlogomod");
            ConfigPanel panel = config.rootPanel;

            _enlargeKey = new KeyCodeField(panel, "Enlarge image", "enlarge_key", KeyCode.BackQuote);
            _colorLockKey = new KeyCodeField(panel, "Color lock image", "color_lock_key", KeyCode.CapsLock);

            ButtonField choose = new ButtonField(panel, "Choose logo", "choose_logo");
            choose.onClick += PickFile;

            ButtonField reset = new ButtonField(panel, "Reload logo", "reload_logo");
            reset.onClick += ResetLogo;

            ButtonField clear = new ButtonField(panel, "Clear recent", "clear_recent");
            clear.onClick += ClearRecent;

            ConfigPanel colors = new ConfigPanel(panel, "Colors", "colors");
            BoolField[] toggles = new BoolField[DVDBounce.ColorNames.Length];

            for (int i = 0; i < toggles.Length; i++)
                toggles[i] = new BoolField(colors, DVDBounce.ColorNames[i], "color_" + i, true);

            _colorToggles = toggles;

            new ConfigHeader(panel, "Recent", 20);

            for (int i = 0; i < MaxRecent; i++)
            {
                int index = i;
                _slotNames[i] = new ConfigHeader(panel, "-", 14);
                _slotButtons[i] = new ButtonField(panel, "Use", "recent_" + i);
                _slotButtons[i].onClick += () => Select(_recent[index]);
            }

            RefreshSlots();
        }

        private void RefreshSlots()
        {
            for (int i = 0; i < MaxRecent; i++)
            {
                bool has = i < _recent.Count;
                _slotNames[i].hidden = !has;
                _slotButtons[i].hidden = !has;

                if (has)
                    _slotNames[i].text = Path.GetFileName(_recent[i]);
            }
        }

        private void Remember(string path)
        {
            _recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            _recent.Insert(0, path);

            if (_recent.Count > MaxRecent)
                _recent.RemoveRange(MaxRecent, _recent.Count - MaxRecent);

            _recentCfg.Value = string.Join("|", _recent.ToArray());
            RefreshSlots();
        }

        private void ClearRecent()
        {
            _recent.Clear();
            _recentCfg.Value = "";
            RefreshSlots();
        }

        private void PickFile()
        {
            string path = FilePicker.Open();
            if (path != null)
                Select(path);
        }

        private void Select(string path)
        {
            _current = path;
            _currentCfg.Value = path;
            Remember(path);
            LoadMedia();
        }

        private void ResetLogo()
        {
            _current = "";
            _currentCfg.Value = "";
            LoadMedia();
        }

        private void CreateDVDCanvas()
        {
            GameObject canvasGO = new GameObject("DVDLogoCanvas");
            DontDestroyOnLoad(canvasGO);

            Canvas canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 9999;

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            canvasGO.AddComponent<GraphicRaycaster>();

            GameObject imageGO = new GameObject("DVDLogo");
            imageGO.transform.SetParent(canvasGO.transform, false);

            Image img = imageGO.AddComponent<Image>();
            img.raycastTarget = false;

            RectTransform rt = imageGO.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(200, 120);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(200, 120);

            _bounce = imageGO.AddComponent<DVDBounce>();
            _bounce.speed = 250f;
            _bounce.gifSpeedMultiplier = 1f;

            LoadMedia();
        }

        private void LoadMedia()
        {
            if (_bounce == null)
                return;

            _bounce.ClearMedia();

            bool ok = (!string.IsNullOrEmpty(_current) && Apply(_current))
                || Apply(PluginPath(GifName))
                || Apply(PluginPath(PngName));

            if (!ok)
            {
                _bounce.SetStaticSprite(null);
                Log.LogWarning("No logo found.");
            }
        }

        private bool Apply(string path)
        {
            if (!File.Exists(path))
                return false;

            if (Path.GetExtension(path).ToLowerInvariant() == ".gif")
            {
                List<GifFrame> frames = TryLoadGif(path);
                if (frames == null)
                    return false;

                _bounce.SetAnimatedFrames(frames);
                Log.LogInfo($"GIF loaded ({frames.Count}).");
                return true;
            }

            Sprite sprite = LoadPngSprite(path);
            if (sprite == null)
                return false;

            _bounce.SetStaticSprite(sprite);
            Log.LogInfo("PNG loaded.");
            return true;
        }

        private string PluginPath(string name)
        {
            return Path.Combine(
                Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                name);
        }

        private List<GifFrame> TryLoadGif(string path)
        {
            try
            {
                List<GifFrame> frames = GifDecoder.Decode(File.ReadAllBytes(path));
                return (frames != null && frames.Count > 0) ? frames : null;
            }
            catch (Exception ex)
            {
                Log.LogWarning($"GIF error: {ex.Message}");
                return null;
            }
        }

        private Sprite LoadPngSprite(string path)
        {
            try
            {
                Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(File.ReadAllBytes(path)))
                {
                    Destroy(tex);
                    return null;
                }

                return Sprite.Create(
                    tex,
                    new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f),
                    100f);
            }
            catch (Exception ex)
            {
                Log.LogWarning($"PNG error: {ex.Message}");
                return null;
            }
        }
    }

    public class DVDBounce : MonoBehaviour
    {
        public float speed = 250f;

        public float gifSpeedMultiplier = 1f;

        public float enlargeMultiplier = 1.5f;

        public KeyCode enlargeKey = KeyCode.BackQuote;
        public KeyCode colorLockKey = KeyCode.CapsLock;

        public static readonly string[] ColorNames =
            { "Red", "Green", "Blue", "Yellow", "Cyan", "Magenta", "Orange", "Purple" };

        public readonly bool[] colorEnabled = new bool[ColorNames.Length];

        private Vector2 _baseSize;
        private bool _isEnlarged = false;

        private Vector2 _velocity;
        private RectTransform _rt;

        private readonly Color[] _colors = new Color[]
        {
            Color.red,
            Color.green,
            Color.blue,
            Color.yellow,
            Color.cyan,
            Color.magenta,
            new Color(1f, 0.5f, 0f),
            new Color(0.5f, 0f, 1f),
        };
        private int _colorIndex = 0;
        private Image _image;

        private bool _colorLocked = false;

        private List<GifFrame> _frames;
        private Sprite[] _frameSprites;
        private Sprite _staticSprite;
        private Coroutine _animationCoroutine;
        private RectTransform _canvasRect;

        private void Awake()
        {
            _rt = GetComponent<RectTransform>();
            _image = GetComponent<Image>();
            _baseSize = _rt.sizeDelta;

            for (int i = 0; i < colorEnabled.Length; i++)
                colorEnabled[i] = true;

            Canvas canvas = GetComponentInParent<Canvas>();
            _canvasRect = canvas.GetComponent<RectTransform>();
        }

        private void Start()
        {
            float angle = UnityEngine.Random.Range(25f, 65f);
            _velocity = new Vector2(
                Mathf.Cos(angle * Mathf.Deg2Rad),
                Mathf.Sin(angle * Mathf.Deg2Rad)) * speed;

            if (_frames == null || _frames.Count == 0)
                ApplyColor();

            if (_frames != null && _frames.Count > 0 && _animationCoroutine == null)
                _animationCoroutine = StartCoroutine(PlayAnimation());
        }

        public void ClearMedia()
        {
            if (_animationCoroutine != null)
            {
                StopCoroutine(_animationCoroutine);
                _animationCoroutine = null;
            }

            if (_image != null)
                _image.sprite = null;

            if (_frameSprites != null)
            {
                foreach (Sprite s in _frameSprites)
                    DestroySprite(s);
            }

            DestroySprite(_staticSprite);

            _frameSprites = null;
            _frames = null;
            _staticSprite = null;
        }

        private static void DestroySprite(Sprite sprite)
        {
            if (sprite == null)
                return;

            Destroy(sprite.texture);
            Destroy(sprite);
        }

        public void SetStaticSprite(Sprite sprite)
        {
            _staticSprite = sprite;

            if (_image == null)
                return;

            _image.sprite = sprite;
            _image.preserveAspect = true;

            if (_colorLocked)
                _image.color = Color.white;
            else
                ApplyColor();
        }

        public void SetAnimatedFrames(List<GifFrame> frames)
        {
            _frames = frames;

            _frameSprites = new Sprite[_frames.Count];
            for (int i = 0; i < _frames.Count; i++)
            {
                Texture2D tex = _frames[i].Texture;
                _frameSprites[i] = Sprite.Create(
                    tex,
                    new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f),
                    100f);
            }

            if (_image != null)
            {
                if (_frameSprites.Length > 0)
                    _image.sprite = _frameSprites[0];
                _image.preserveAspect = true;
                _image.color = Color.white;
            }

            if (isActiveAndEnabled && _animationCoroutine == null && _frameSprites.Length > 0)
                _animationCoroutine = StartCoroutine(PlayAnimation());
        }

        private IEnumerator PlayAnimation()
        {
            int frameIndex = 0;

            while (true)
            {
                GifFrame frame = _frames[frameIndex];

                if (_image != null)
                    _image.sprite = _frameSprites[frameIndex];

                float mult = Mathf.Max(gifSpeedMultiplier, 0.01f);
                float delay = Mathf.Max(frame.DelaySeconds / mult, 0.02f);
                yield return new WaitForSeconds(delay);

                frameIndex = (frameIndex + 1) % _frames.Count;
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(enlargeKey))
            {
                _isEnlarged = !_isEnlarged;
                _rt.sizeDelta = _isEnlarged ? _baseSize * enlargeMultiplier : _baseSize;
            }

            if (Input.GetKeyDown(colorLockKey))
            {
                _colorLocked = !_colorLocked;

                if (_colorLocked)
                {
                    if (_image != null)
                        _image.color = new Color(1f, 1f, 1f);
                }
                else
                {
                    ApplyColor();
                }
            }

            if (!_colorLocked && !colorEnabled[_colorIndex])
                NextColor();

            Vector2 pos = _rt.anchoredPosition;
            Vector2 size = _rt.sizeDelta;

            Rect canvasRect = _canvasRect.rect;
            float screenW = canvasRect.width;
            float screenH = canvasRect.height;

            float halfW = size.x * 0.5f;
            float halfH = size.y * 0.5f;

            pos += _velocity * Time.deltaTime;

            bool bounced = false;

            if (pos.x - halfW <= 0f)
            {
                pos.x = halfW;
                _velocity.x = Mathf.Abs(_velocity.x);
                bounced = true;
            }
            else if (pos.x + halfW >= screenW)
            {
                pos.x = screenW - halfW;
                _velocity.x = -Mathf.Abs(_velocity.x);
                bounced = true;
            }

            if (pos.y - halfH <= 0f)
            {
                pos.y = halfH;
                _velocity.y = Mathf.Abs(_velocity.y);
                bounced = true;
            }
            else if (pos.y + halfH >= screenH)
            {
                pos.y = screenH - halfH;
                _velocity.y = -Mathf.Abs(_velocity.y);
                bounced = true;
            }

            if (bounced && !_colorLocked)
            {
                NextColor();
            }

            _rt.anchoredPosition = pos;
        }

        private void NextColor()
        {
            for (int i = 1; i <= _colors.Length; i++)
            {
                int next = (_colorIndex + i) % _colors.Length;

                if (colorEnabled[next])
                {
                    _colorIndex = next;
                    break;
                }
            }

            ApplyColor();
        }

        private void ApplyColor()
        {
            if (_image == null)
                return;

            bool any = Array.IndexOf(colorEnabled, true) >= 0;
            _image.color = any ? _colors[_colorIndex] : Color.white;
        }
    }
}