using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class SceneFlow : MonoBehaviour
{
    public static SceneFlow Instance { get; private set; }

    [Header("Scene Names (Build Settingsと一致させる)")]
    [SerializeField] private string titleSceneName = "TitleScene";
    [SerializeField] private string gameSceneName = "GameScene";
    [SerializeField] private string overSceneName = "GameOver";

    private bool _isEnding;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        _isEnding = false;
    }

    // ===== Title =====
    public void GoToGame()
    {
        _isEnding = false;

        // 开局前先做一次“新局重置”
        ResetManager.ResetForNewRun();

        if (SceneTransitionFx.Instance != null)
            SceneTransitionFx.Instance.LoadSceneWithFx(gameSceneName, SceneTransitionFx.TransitionStyle.Ink);
        else
            SceneManager.LoadScene(gameSceneName);
    }

    // ===== GameScene =====
    public void GameOver()
    {
        if (_isEnding) return;
        _isEnding = true;

        if (SceneTransitionFx.Instance != null)
            SceneTransitionFx.Instance.LoadSceneWithFx(overSceneName, SceneTransitionFx.TransitionStyle.Shards);
        else
            SceneManager.LoadScene(overSceneName);
    }

    // ===== Over =====
    public void RestartToTitle()
    {
        _isEnding = false;

        // 回 Title 前重置（也可以先 Load 再 Reset，看你需求）
        ResetManager.ResetForNewRun();

        if (SceneTransitionFx.Instance != null)
            SceneTransitionFx.Instance.LoadSceneWithFx(titleSceneName, SceneTransitionFx.TransitionStyle.Ink);
        else
            SceneManager.LoadScene(titleSceneName);
    }

    public void QuitGame()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
