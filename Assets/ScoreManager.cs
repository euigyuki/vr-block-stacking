using TMPro;
using UnityEngine;

public class ScoreManager : MonoBehaviour
{
    public TextMeshProUGUI scoreText;

    private int currentStackedCount = 0;

    private void OnEnable()
    {
        StackableBlock.OnStackedStateChanged += HandleScoreChange;
    }

    private void OnDisable()
    {
        StackableBlock.OnStackedStateChanged -= HandleScoreChange;
    }

    private void Start()
    {
        UpdateScoreUI();
    }

    private void HandleScoreChange(StackableBlock block, bool isStacked)
    {
        if (isStacked) currentStackedCount++;
        else currentStackedCount = Mathf.Max(0, currentStackedCount - 1);

        UpdateScoreUI();
    }

    private void UpdateScoreUI()
    {
        if (scoreText != null)
            scoreText.text = "Live Score: " + currentStackedCount;
    }
}
