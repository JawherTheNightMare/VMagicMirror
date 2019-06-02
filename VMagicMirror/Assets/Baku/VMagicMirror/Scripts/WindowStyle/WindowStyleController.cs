using System;
using System.Collections;
using UnityEngine;
using UniRx;

namespace Baku.VMagicMirror
{
    using static NativeMethods;

    public class WindowStyleController : MonoBehaviour
    {
        //Player Settingで決められるデフォルトウィンドウサイズと合わせてるが、常識的な値であれば多少ズレても害はないです
        const int DefaultWindowWidth = 800;
        const int DefaultWindowHeight = 600;

        const string InitialPositionXKey = "InitialPositionX";
        const string InitialPositionYKey = "InitialPositionY";

        [SerializeField]
        private float opaqueThreshold = 0.1f;

        [SerializeField]
        private ReceivedMessageHandler handler = null;

        [SerializeField]
        private float windowPositionCheckInterval = 5.0f;

        [SerializeField]
        private Camera cam = null;

        private float _windowPositionCheckCount = 0;
        private Vector2Int _prevWindowPosition = Vector2Int.zero;

        private uint defaultWindowStyle;
        private uint defaultExWindowStyle;

        private bool _isTransparent = false;
        private bool _isWindowFrameHidden = false;
        private bool _windowDraggableWhenFrameHidden = true;
        private bool _preferIgnoreMouseInput = false;

        private int _hitTestJudgeCountDown = 0;
        //private bool _isMouseLeftButtonDownPreviousFrame = false;
        private bool _isDragging = false;
        private Vector2Int _dragStartMouseOffset = Vector2Int.zero;

        private Texture2D _colorPickerTexture = null;
        private bool _isOnOpaquePixel = false;

        private bool _isClickThrough = false;

        private void Awake()
        {
#if !UNITY_EDITOR
            defaultWindowStyle = GetWindowLong(GetUnityWindowHandle(), GWL_STYLE);
            defaultExWindowStyle = GetWindowLong(GetUnityWindowHandle(), GWL_EXSTYLE);
#endif
        }

        private void Start()
        {
            _colorPickerTexture = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            handler.Commands.Subscribe(message =>
            {
                switch (message.Command)
                {
                    case MessageCommandNames.MouseButton:
                        string info = message.Content;
                        if (info == "LDown")
                        {
                            ReserveHitTestJudgeOnNextFrame();
                        }
                        break;
                    case MessageCommandNames.Chromakey:
                        var argb = message.ToColorFloats();
                        SetWindowTransparency(argb[0] == 0);
                        break;
                    case MessageCommandNames.WindowFrameVisibility:
                        SetWindowFrameVisibility(message.ToBoolean());
                        break;
                    case MessageCommandNames.IgnoreMouse:
                        SetIgnoreMouseInput(message.ToBoolean());
                        break;
                    case MessageCommandNames.TopMost:
                        SetTopMost(message.ToBoolean());
                        break;
                    case MessageCommandNames.WindowDraggable:
                        SetWindowDraggable(message.ToBoolean());
                        break;
                    case MessageCommandNames.MoveWindow:
                        int[] xy = message.ToIntArray();
                        MoveWindow(xy[0], xy[1]);
                        break;
                    case MessageCommandNames.ResetWindowSize:
                        ResetWindowSize();
                        break;
                    default:
                        break;
                }

            });

            //既定で最前面に表示
            SetTopMost(true);

            InitializeWindowPositionCheckStatus();
            StartCoroutine(PickColorCoroutine());
        }

        private void Update()
        {
            UpdateClickThrough();
            UpdateDragStatus();
            UpdateWindowPositionCheck();
        }

        private void OnDestroy()
        {
#if !UNITY_EDITOR
            var windowPosition = GetUnityWindowPosition();
            PlayerPrefs.SetInt(InitialPositionXKey, windowPosition.x);
            PlayerPrefs.SetInt(InitialPositionYKey, windowPosition.y);
#endif
        }

        private void UpdateClickThrough()
        {
            if (!_isTransparent)
            {
                //不透明ウィンドウ = 絶対にクリックは取る(不透明なのに裏ウィンドウが触れると不自然！)
                SetClickThrough(false);
                return;
            }

            if (!_windowDraggableWhenFrameHidden && _preferIgnoreMouseInput)
            {
                //透明であり、明示的にクリック無視が指定されている = 指定通りにクリックを無視
                SetClickThrough(true);
                return;
            }

            //透明であり、クリックはとってほしい = マウス直下のピクセル状態で判断
            SetClickThrough(!_isOnOpaquePixel);
        }

        private void UpdateDragStatus()
        {
            if (_isWindowFrameHidden &&
                _windowDraggableWhenFrameHidden &&
                _hitTestJudgeCountDown == 1 &&
                _isOnOpaquePixel
                )
            {
                _hitTestJudgeCountDown = 0;
                if (!Application.isFocused)
                {
                    SetUnityWindowActive();
                }
                _isDragging = true;
#if !UNITY_EDITOR
                var mousePosition = GetWindowsMousePosition();
                var windowPosition = GetUnityWindowPosition();
                //以降、このオフセットを保てるようにウィンドウを動かす
                _dragStartMouseOffset = mousePosition - windowPosition;
#endif
            }

            //タッチスクリーンでパッと見の操作が破綻しないために…。
            if (!Input.GetMouseButton(0))
            {
                _isDragging = false;
            }

            if (_isDragging)
            {
#if !UNITY_EDITOR
                var mousePosition = GetWindowsMousePosition();
                SetUnityWindowPosition(
                    mousePosition.x - _dragStartMouseOffset.x, 
                    mousePosition.y - _dragStartMouseOffset.y
                    );
#endif
            }

            if (_hitTestJudgeCountDown > 0)
            {
                _hitTestJudgeCountDown--;
            }
        }

        private void InitializeWindowPositionCheckStatus()
        {
            _windowPositionCheckCount = windowPositionCheckInterval;
            if (PlayerPrefs.HasKey(InitialPositionXKey) &&
                PlayerPrefs.HasKey(InitialPositionYKey)
                )
            {
#if !UNITY_EDITOR
                int x = PlayerPrefs.GetInt(InitialPositionXKey);
                int y = PlayerPrefs.GetInt(InitialPositionYKey);
                _prevWindowPosition = new Vector2Int(x, y);
                SetUnityWindowPosition(x, y);
#endif
            }
            else
            {
#if !UNITY_EDITOR
                _prevWindowPosition = GetUnityWindowPosition();
                PlayerPrefs.SetInt(InitialPositionXKey, _prevWindowPosition.x);
                PlayerPrefs.SetInt(InitialPositionYKey, _prevWindowPosition.y);
#endif
            }
        }

        private void UpdateWindowPositionCheck()
        {
            _windowPositionCheckCount -= Time.deltaTime;
            if (_windowPositionCheckCount > 0)
            {
                return;
            }
            _windowPositionCheckCount = windowPositionCheckInterval;

#if !UNITY_EDITOR
            var pos = GetUnityWindowPosition();
            if (pos.x != _prevWindowPosition.x ||
                pos.y != _prevWindowPosition.y)
            {
                _prevWindowPosition = pos;
                PlayerPrefs.SetInt(InitialPositionXKey, _prevWindowPosition.x);
                PlayerPrefs.SetInt(InitialPositionYKey, _prevWindowPosition.y);
            }
#endif
        }

        private void MoveWindow(int x, int y)
        {
#if !UNITY_EDITOR
            SetUnityWindowPosition(x, y);
#endif
        }

        private void ResetWindowSize()
        {
#if !UNITY_EDITOR
            SetUnityWindowSize(DefaultWindowWidth, DefaultWindowHeight);
#endif
        }

        private void SetWindowFrameVisibility(bool isVisible)
        {
            _isWindowFrameHidden = !isVisible;

            LogOutput.Instance.Write($"{nameof(SetWindowFrameVisibility)}:{isVisible}");
            var hwnd = GetUnityWindowHandle();
            uint windowStyle = isVisible ?
                defaultWindowStyle :
                WS_POPUP | WS_VISIBLE;
#if !UNITY_EDITOR
            SetWindowLong(hwnd, GWL_STYLE, windowStyle);
#endif
        }

        private void SetWindowTransparency(bool isTransparent)
        {
            _isTransparent = isTransparent;
#if !UNITY_EDITOR
            SetDwmTransparent(isTransparent);
#endif
        }

        private void SetIgnoreMouseInput(bool ignoreMouseInput)
        {
            _preferIgnoreMouseInput = ignoreMouseInput;
        }

        private void SetTopMost(bool isTopMost)
        {
#if !UNITY_EDITOR
        SetUnityWindowTopMost(isTopMost);
#endif
        }

        private void SetWindowDraggable(bool isDraggable)
        {
            if (!isDraggable)
            {
                _isDragging = false;
            }

            _windowDraggableWhenFrameHidden = isDraggable;
        }

        private void ReserveHitTestJudgeOnNextFrame()
        {
            //UniRxのほうが実行が先なはずなので、
            //1カウント分はメッセージが来た時点のフレームで消費し、
            //さらに1フレーム待ってからヒットテスト判定
            _hitTestJudgeCountDown = 2;

            //タッチスクリーンだとMouseButtonUpが取れない事があるらしいため、この段階でフラグを折る
             _isDragging = false;
        }

        /// <summary>
        /// 背景を透過すべきかの判別方法: キルロボさんのブログ https://qiita.com/kirurobo/items/013cee3fa47a5332e186
        /// </summary>
        /// <returns></returns>
        private IEnumerator PickColorCoroutine()
        {
            while (Application.isPlaying)
            {
                yield return new WaitForEndOfFrame();
                ObservePixelUnderCursor(cam);
            }
            yield return null;
        }

        /// <summary>
        /// マウス直下の画素が透明かどうかを判定
        /// </summary>
        /// <param name="cam"></param>
        private void ObservePixelUnderCursor(Camera cam)
        {
            Vector2 mousePos = Input.mousePosition;
            Rect camRect = cam.pixelRect;

            if (!camRect.Contains(mousePos))
            {
                _isOnOpaquePixel = false;
                return;
            }

            try
            {
                // マウス直下の画素を読む (参考 http://tsubakit1.hateblo.jp/entry/20131203/1386000440 )
                _colorPickerTexture.ReadPixels(new Rect(mousePos, Vector2.one), 0, 0);
                Color color = _colorPickerTexture.GetPixel(0, 0);

                // アルファ値がしきい値以上ならば不透過
                _isOnOpaquePixel = (color.a >= opaqueThreshold);
            }
            catch (Exception ex)
            {
                // 稀に範囲外になるとのこと(元ブログに記載あり)
                Debug.LogError(ex.Message);
                _isOnOpaquePixel = false;
            }
        }

        private void SetClickThrough(bool through)
        {
            if (_isClickThrough == through)
            {
                return;
            }

            _isClickThrough = through;

            var hwnd = GetUnityWindowHandle();
            uint exWindowStyle = _isClickThrough ?
                WS_EX_LAYERED | WS_EX_TRANSPARENT :
                defaultExWindowStyle;

#if !UNITY_EDITOR
            SetWindowLong(hwnd, GWL_EXSTYLE, exWindowStyle);
#endif
        }
    }
}