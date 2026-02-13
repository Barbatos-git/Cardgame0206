using UnityEngine;

public class OverSceneController : MonoBehaviour
{
    public void Retry()
    {
        SceneFlow.Instance.RestartToTitle();
    }

    public void Quit()
    {
        SceneFlow.Instance.QuitGame();
    }
}
