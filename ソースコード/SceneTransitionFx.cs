using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using DG.Tweening;

/// <summary>
/// Shader转场：DOTween驱动材质 _Progress。
/// 0 = 不遮挡，1 = 完全遮挡（按shader定义）
/// 
/// - Ink：墨滴/溶解
/// - Shards：碎裂块
/// </summary>
public sealed class SceneTransitionFx : MonoBehaviour
{
    public static SceneTransitionFx Instance { get; private set; }

    public enum TransitionStyle { Ink, Shards }

    [Header("UI")]
    [SerializeField] private Canvas transitionCanvas;
    [SerializeField] private CanvasGroup canvasGroup;   // 推荐：挂在Canvas上（可为空）
    [SerializeField] private Image overlayImage;

    [Header("Materials")]
    [SerializeField] private Material inkMaterial;
    [SerializeField] private Material shardsMaterial;

    [Header("Shader Param")]
    [SerializeField] private string progressProp = "_Progress";

    [Header("Timing")]
    [SerializeField] private float coverDuration = 0.6f;
    [SerializeField] private float uncoverDuration = 0.6f;
    [SerializeField] private Ease ease = Ease.InOutQuad;

    [Header("Debug")]
    [SerializeField] private bool logProgress = false;

    private Material _matInstance;
    private Tween _tween;
    private bool _busy;

    public bool IsBusy => _busy;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        if (transitionCanvas == null || overlayImage == null)
        {
            Debug.LogError("[SceneTransitionFx] Missing references: transitionCanvas / overlayImage.", this);
            return;
        }

        // 默认隐藏
        HideInstant();
    }

    /// <summary>
    /// 对外入口：指定样式切场景（遮挡->LoadScene->揭开）
    /// </summary>
    public void LoadSceneWithFx(string sceneName, TransitionStyle style)
    {
        if (_busy) return;

        var src = (style == TransitionStyle.Shards) ? shardsMaterial : inkMaterial;
        if (src == null)
        {
            Debug.LogError("[SceneTransitionFx] Source material not assigned for style=" + style, this);
            return;
        }

        LoadSceneWithMaterial(sceneName, src);
    }

    /// <summary>
    /// 兼容：不想传枚举时也能用（默认Ink）
    /// </summary>
    public void LoadSceneWithInk(string sceneName)
    {
        LoadSceneWithFx(sceneName, TransitionStyle.Ink);
    }

    private void LoadSceneWithMaterial(string sceneName, Material src)
    {
        if (_busy) return;
        _busy = true;

        _tween?.Kill();

        // 每次切换都实例化一份材质，避免改到共享
        _matInstance = new Material(src);
        overlayImage.material = _matInstance;

        ShowInstant();
        SetProgress(0f);

        _tween = DOTween.Sequence()
            .Append(DOTween.To(GetProgress, SetProgress, 1f, coverDuration).SetEase(ease))
            .AppendCallback(() =>
            {
                SceneManager.LoadScene(sceneName);
            })
            .AppendInterval(0.05f) // 给一帧初始化（可删）
            .Append(DOTween.To(GetProgress, SetProgress, 0f, uncoverDuration).SetEase(ease))
            .AppendCallback(() =>
            {
                HideInstant();
                _busy = false;
            });
    }

    private float GetProgress()
    {
        if (_matInstance == null) return 0f;
        return _matInstance.GetFloat(progressProp);
    }

    private void SetProgress(float v)
    {
        if (_matInstance == null) return;
        _matInstance.SetFloat(progressProp, v);

        if (logProgress)
            Debug.Log($"[SceneTransitionFx] {progressProp}={v:0.000}");
    }

    private void ShowInstant()
    {
        if (transitionCanvas != null) transitionCanvas.enabled = true;
        if (overlayImage != null) overlayImage.gameObject.SetActive(true);

        if (canvasGroup != null)
        {
            canvasGroup.alpha = 1f;
            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false; // 不挡UI
        }
    }

    private void HideInstant()
    {
        // 先把progress归零，避免下次进来直接全黑
        if (_matInstance != null)
            _matInstance.SetFloat(progressProp, 0f);

        if (overlayImage != null) overlayImage.gameObject.SetActive(false);

        if (canvasGroup != null)
        {
            canvasGroup.alpha = 0f;
            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;
        }

        if (transitionCanvas != null) transitionCanvas.enabled = false;
    }
}
